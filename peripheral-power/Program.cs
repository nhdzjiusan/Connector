using HidSharp;
using Microsoft.Win32;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Windows.Devices.Enumeration;

namespace PeripheralPower;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--scan", var report])
        {
            ScanToFile(report);
            return;
        }

        using var openPanel = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PeripheralPower.OpenPanel");
        using var instance = new Mutex(true, @"Local\PeripheralPower.Instance", out var first);
        if (!first)
        {
            if (!args.Contains("--quiet")) openPanel.Set();
            return;
        }

        try
        {
            ApplicationConfiguration.Initialize();
            using var app = new PowerContext(openPanel);
            if (!args.Contains("--quiet")) app.ShowPanel();
            Application.Run(app);
        }
        finally
        {
            instance.ReleaseMutex();
        }
    }

    private static void ScanToFile(string report)
    {
        var details = new ConcurrentQueue<string>();
        try
        {
            var usb = UsbBattery.ReadAsync(details.Enqueue);
            var bluetooth = BluetoothBattery.ReadAsync();
            Task.WhenAll(usb, bluetooth).GetAwaiter().GetResult();
            var readings = usb.Result.Concat(bluetooth.Result).ToArray();
            File.WriteAllText(report, JsonSerializer.Serialize(new { readings, details },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(report, ex.ToString());
            Environment.ExitCode = 1;
        }
    }
}

internal static class Settings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "PeripheralPower";
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PeripheralPower");
    private static readonly string IntervalFile = Path.Combine(Folder, "interval.txt");
    private static string LaunchCommand => $"\"{Application.ExecutablePath}\" --quiet";

    public static int LoadInterval()
    {
        try
        {
            if (int.TryParse(File.ReadAllText(IntervalFile), out int minutes) && minutes is >= 5 and <= 15)
                return minutes;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return 10;
    }

    public static void SaveInterval(int value)
    {
        Directory.CreateDirectory(Folder);
        var temp = IntervalFile + ".tmp";
        File.WriteAllText(temp, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.Move(temp, IntervalFile, overwrite: true);
    }

    public static bool StartsAtLogin()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue(RunValue) as string, LaunchCommand, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, LaunchCommand);
        else if (string.Equals(key.GetValue(RunValue) as string, LaunchCommand, StringComparison.OrdinalIgnoreCase))
            key.DeleteValue(RunValue, throwOnMissingValue: false);
    }
}

internal sealed class PowerContext : ApplicationContext
{
    private readonly Control dispatcher = new();
    private readonly BatteryPanel panel = new();
    private readonly System.Windows.Forms.Timer poll = new();
    private readonly System.Windows.Forms.Timer deviceDelay = new() { Interval = 1100 };
    private readonly Dictionary<string, TrayReading> icons = [];
    private readonly ContextMenuStrip menu = new();
    private readonly NotifyIcon launcher = new();
    private readonly Icon launcherIcon = TrayArt.MakeLauncher();
    private readonly RegisteredWaitHandle openWait;
    private readonly DeviceWatcher? watcher;
    private BatteryReading[] readings = [];
    private DateTime? updated;
    private bool running;
    private bool queued;
    private bool closing;

    private sealed class TrayReading(NotifyIcon icon, Icon drawing, BatteryReading battery) : IDisposable
    {
        public NotifyIcon Icon { get; } = icon;
        public Icon Drawing { get; set; } = drawing;
        public BatteryReading Battery { get; set; } = battery;

        public void Dispose()
        {
            Icon.Visible = false;
            Icon.Dispose();
            Drawing.Dispose();
        }
    }

    public PowerContext(EventWaitHandle openPanel)
    {
        _ = dispatcher.Handle;
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        panel.SetInterval(Settings.LoadInterval());
        try { panel.SetStartup(Settings.StartsAtLogin()); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            panel.Report("无法读取自启动设置");
        }
        panel.IntervalChanged += SetInterval;
        panel.StartupChanged += SetStartup;
        panel.RefreshRequested += () => _ = RefreshAsync();
        panel.ExitRequested += ExitThread;

        menu.Items.Add("打开电量与设置", null, (_, _) => ShowPanel(Cursor.Position));
        menu.Items.Add("立即刷新", null, (_, _) => _ = RefreshAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitThread());

        launcher.Icon = launcherIcon;
        launcher.Text = "外设电量";
        ConfigureIcon(launcher);
        launcher.Visible = true;

        poll.Interval = checked(panel.IntervalMinutes * 60_000);
        poll.Tick += (_, _) => _ = RefreshAsync();
        poll.Start();
        deviceDelay.Tick += (_, _) =>
        {
            deviceDelay.Stop();
            _ = RefreshAsync(queueIfRunning: true);
        };

        DeviceList.Local.Changed += DeviceChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        try { watcher = BluetoothBattery.Watch(QueueDeviceRefresh); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { }
        openWait = ThreadPool.RegisterWaitForSingleObject(openPanel, (_, _) => OnUi(() => ShowPanel()), null, -1, false);
        dispatcher.BeginInvoke(() => _ = RefreshAsync());
    }

    public void ShowPanel(Point? near = null)
    {
        panel.Open(near);
        if (updated == null || DateTime.Now - updated.Value > TimeSpan.FromSeconds(30)) _ = RefreshAsync();
    }

    private void ConfigureIcon(NotifyIcon icon)
    {
        icon.ContextMenuStrip = menu;
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.None) ShowPanel(Cursor.Position);
        };
    }

    private void DeviceChanged(object? sender, DeviceListChangedEventArgs args) => QueueDeviceRefresh();

    private void PowerChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Resume) QueueDeviceRefresh();
    }

    private void QueueDeviceRefresh()
    {
        OnUi(() =>
        {
            deviceDelay.Stop();
            deviceDelay.Start();
        });
    }

    private void OnUi(Action action)
    {
        if (closing) return;
        try { dispatcher.BeginInvoke(() => { if (!closing) action(); }); }
        catch (InvalidOperationException) { } // Dispatcher closed before the device callback completed.
    }

    private void SetInterval(int value)
    {
        try
        {
            Settings.SaveInterval(value);
            poll.Stop();
            poll.Interval = checked(value * 60_000);
            poll.Start();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            panel.SetInterval(poll.Interval / 60_000);
            panel.Report("刷新间隔保存失败");
        }
    }

    private void SetStartup(bool enabled)
    {
        try
        {
            Settings.SetStartup(enabled);
            panel.Report(enabled ? "已开启登录自启动" : "已关闭登录自启动");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            panel.SetStartup(!enabled);
            panel.Report("无法保存自启动设置");
        }
    }

    private async Task RefreshAsync(bool queueIfRunning = false)
    {
        if (closing) return;
        if (running)
        {
            queued |= queueIfRunning;
            return;
        }

        running = true;
        panel.SetData(readings, updated, busy: true);
        try
        {
            var usb = Task.Run(() => UsbBattery.ReadAsync());
            var bluetooth = BluetoothBattery.ReadAsync();
            await Task.WhenAll(usb, bluetooth);
            if (closing) return;
            readings = usb.Result.Concat(bluetooth.Result)
                .GroupBy(reading => reading.Id).Select(group => group.First())
                .OrderBy(reading => reading.Kind).ThenBy(reading => reading.Name).ToArray();
            updated = DateTime.Now;
            UpdateTray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            if (!closing) panel.Report("读取设备时发生错误，可以立即重试");
        }
        finally
        {
            running = false;
            if (!closing)
            {
                panel.SetData(readings, updated, busy: false);
                if (queued)
                {
                    queued = false;
                    dispatcher.BeginInvoke(() => _ = RefreshAsync());
                }
            }
        }
    }

    private void UpdateTray()
    {
        var present = readings.Select(battery => battery.Id).ToHashSet();
        foreach (var id in icons.Keys.Where(id => !present.Contains(id)).ToArray())
        {
            icons[id].Dispose();
            icons.Remove(id);
        }

        foreach (var battery in readings)
        {
            if (icons.TryGetValue(battery.Id, out var entry))
            {
                if (entry.Battery == battery) continue;
                var previous = entry.Drawing;
                entry.Drawing = TrayArt.MakeReading(battery);
                entry.Icon.Icon = entry.Drawing;
                entry.Icon.Text = Tooltip(battery);
                entry.Battery = battery;
                previous.Dispose();
                continue;
            }

            var picture = TrayArt.MakeReading(battery);
            var icon = new NotifyIcon { Icon = picture, Text = Tooltip(battery) };
            ConfigureIcon(icon);
            icons.Add(battery.Id, new TrayReading(icon, picture, battery));
            icon.Visible = true;
        }

        launcher.Visible = icons.Count == 0;
    }

    private static string Tooltip(BatteryReading battery)
    {
        var text = $"{battery.Percent}% · {battery.Name}";
        if (battery.PowerStatus.Length > 0) text += $" · {battery.PowerStatus}";
        return text.Length <= 63 ? text : text[..62] + "…";
    }

    protected override void ExitThreadCore()
    {
        if (closing) return;
        closing = true;
        openWait.Unregister(null);
        SystemEvents.PowerModeChanged -= PowerChanged;
        DeviceList.Local.Changed -= DeviceChanged;
        if (watcher?.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
        poll.Dispose();
        deviceDelay.Dispose();
        foreach (var entry in icons.Values) entry.Dispose();
        launcher.Visible = false;
        launcher.Dispose();
        launcherIcon.Dispose();
        menu.Dispose();
        panel.Dispose();
        dispatcher.Dispose();
        base.ExitThreadCore();
    }
}
