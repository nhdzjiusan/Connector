using HidSharp;
using Microsoft.Win32;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Windows.Devices.Enumeration;

namespace PeripheralPower;

internal static class Program
{
    private const string Usage = "PeripheralPower [--quiet | --scan <report.json> | --version | --help]\n无需管理员权限。--quiet 在后台启动；--scan 输出本地设备诊断。";
    private static PowerContext? current;
    private static bool restart;
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint processId);

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 0 && args is not ["--quiet"] and not ["--error-recovery"])
        {
            AttachConsole(uint.MaxValue);
            if (args is ["--scan", var report]) ScanToFile(report);
            else if (args is ["--version"]) Console.WriteLine(typeof(Program).Assembly.GetName().Version!.ToString(3));
            else if (args is ["--help"]) Console.WriteLine($"{typeof(Program).Assembly.GetName().Version!.ToString(3)}\n{Usage}");
            else { Console.Error.WriteLine(Usage); Environment.ExitCode = 2; }
            return;
        }
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_,e) =>
        {
            ErrorLog.Write("界面线程",e.Exception);
            current?.OfferRestart();
        };
        AppDomain.CurrentDomain.UnhandledException += (_,e) =>
        {
            ErrorLog.Write("进程异常",e.ExceptionObject as Exception ?? new Exception("未知异常"));
            if (e.IsTerminating && args is not ["--error-recovery"]) Launch("--error-recovery");
        };
        if (args is ["--error-recovery"]) { RecoveryTray(); return; }

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
            current = app;
            if (!args.Contains("--quiet")) app.ShowPanel();
            Application.Run(app);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("应用启动或消息循环",ex);
            Launch("--error-recovery");
            Environment.ExitCode = 1;
        }
        finally
        {
            instance.ReleaseMutex();
            instance.Dispose();
        }
        if (restart) Launch("--quiet");
    }

    internal static void Restart() { restart = true; current?.ExitThread(); }
    private static void Launch(string arg)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath,arg)); }
        catch (Exception ex) { ErrorLog.Write("重新启动",ex); }
    }
    private static void RecoveryTray()
    {
        ApplicationConfiguration.Initialize();
        using var menu = new ContextMenuStrip();
        using var icon = new NotifyIcon { Icon = SystemIcons.Warning, Text = "外设电量 · 点击重启", ContextMenuStrip = menu, Visible = true };
        void Retry() { Launch("--quiet"); Application.ExitThread(); }
        menu.Items.Add("重启",null,(_,_) => Retry());
        menu.Items.Add("退出",null,(_,_) => Application.ExitThread());
        icon.MouseClick += (_,e) => { if(e.Button == MouseButtons.Left) Retry(); };
        icon.BalloonTipClicked += (_,_) => Retry();
        icon.ShowBalloonTip(5000,"外设电量","应用遇到错误，可点击图标重启",ToolTipIcon.Warning);
        Application.Run();
    }

    private static void ScanToFile(string report)
    {
        var details = new ConcurrentQueue<string>();
        try
        {
            report = Path.GetFullPath(report);
            if (!Directory.Exists(Path.GetDirectoryName(report))) throw new DirectoryNotFoundException("报告目录不存在");
            var usb = UsbBattery.ReadAsync(details.Enqueue);
            var bluetooth = BluetoothBattery.ReadAsync();
            Task.WhenAll(usb, bluetooth).GetAwaiter().GetResult();
            var readings = usb.Result.Fresh.Concat(bluetooth.Result.Fresh).ToArray();
            File.WriteAllText(report, JsonSerializer.Serialize(new { readings, details, partial = usb.Result.Partial || bluetooth.Result.Partial },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"扫描失败：{ex.Message}");
            ErrorLog.Write("诊断报告",ex);
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

    public static bool RepairStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey,true);
        if (key?.GetValue(RunValue) is not string value || string.Equals(value,LaunchCommand,StringComparison.OrdinalIgnoreCase))
            return false;
        key.SetValue(RunValue,LaunchCommand);
        return true;
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
    private readonly TaskbarStrip strip = new();
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
    private bool restartOffered;
    private bool usbChanged, bleChanged;

    public PowerContext(EventWaitHandle openPanel)
    {
        _ = dispatcher.Handle;
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        panel.SetInterval(Settings.LoadInterval());
        bool repaired = false;
        try { repaired = Settings.RepairStartup(); panel.SetStartup(Settings.StartsAtLogin()); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            ErrorLog.Write("读取自启动设置",ex);
            panel.Report("无法读取自启动设置");
        }
        panel.IntervalChanged += SetInterval;
        panel.StartupChanged += enabled => SetStartup(enabled);
        panel.RefreshRequested += () => _ = RefreshAsync();
        panel.ExitRequested += ExitThread;

        menu.Items.Add("打开电量与设置", null, (_, _) => ShowPanel(Cursor.Position));
        menu.Items.Add("立即刷新", null, (_, _) => _ = RefreshAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出（保留自启设置）", null, (_, _) => ExitThread());
        menu.Items.Add("退出并关闭自启", null, (_, _) => { if (SetStartup(false)) ExitThread(); });

        launcher.Icon = launcherIcon;
        launcher.Text = "外设电量";
        ConfigureIcon(launcher);
        launcher.Visible = true;
        if (repaired) launcher.ShowBalloonTip(5000,"外设电量","已将登录自启修正到当前程序路径。",ToolTipIcon.Info);
        launcher.BalloonTipClicked += (_,_) => { if (restartOffered) Program.Restart(); };
        strip.ContextMenuStrip = menu;
        strip.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowPanel(Cursor.Position); };
        strip.AvailabilityChanged += visible => launcher.Visible = !visible || restartOffered;

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
        try { watcher = BluetoothBattery.Watch(() => OnUi(() => { bleChanged = true; QueueDeviceRefresh(); })); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        { ErrorLog.Write("蓝牙设备监听",ex); }
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
            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.None)
            { if (restartOffered) Program.Restart(); else ShowPanel(Cursor.Position); }
        };
    }

    internal void OfferRestart() => OnUi(() =>
    {
        if (restartOffered) return;
        restartOffered = true;
        launcher.Visible = true;
        launcher.ShowBalloonTip(5000,"外设电量","应用遇到错误，可点击图标重启",ToolTipIcon.Warning);
    });

    private void DeviceChanged(object? sender, DeviceListChangedEventArgs args) =>
        OnUi(() => { usbChanged = true; QueueDeviceRefresh(); });

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
            ErrorLog.Write("保存刷新间隔",ex);
            panel.SetInterval(poll.Interval / 60_000);
            panel.Report("刷新间隔保存失败");
        }
    }

    private bool SetStartup(bool enabled)
    {
        try
        {
            Settings.SetStartup(enabled);
            panel.Report(enabled ? "已开启登录自启动" : "已关闭登录自启动");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            panel.SetStartup(!enabled);
            ErrorLog.Write("保存自启",ex);
            panel.Report("无法保存自启动设置");
            return false;
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
        bool removeUsb = usbChanged, removeBle = bleChanged;
        usbChanged = bleChanged = false;
        string? warning = null;
        panel.SetData(readings, updated, busy: true);
        try
        {
            var usb = SafePoll("USB", () => UsbBattery.ReadAsync());
            var bluetooth = SafePoll("蓝牙", BluetoothBattery.ReadAsync);
            await Task.WhenAll(usb, bluetooth);
            if (closing) return;
            var now = DateTime.Now;
            usbChanged |= removeUsb && usb.Result.Present == null;
            bleChanged |= removeBle && bluetooth.Result.Present == null;
            readings = Merge("usb-",usb.Result,removeUsb,now).Concat(Merge("ble-",bluetooth.Result,removeBle,now))
                .OrderBy(r => r.Kind).ThenBy(r => r.Name).ToArray();
            if (usb.Result.Fresh.Length + bluetooth.Result.Fresh.Length > 0) updated = now;
            if (usb.Result.Partial || bluetooth.Result.Partial || readings.Any(r => r.Stale))
                warning = "部分设备未响应 · 已保留上次读数";
            strip.SetDevices(readings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            ErrorLog.Write("刷新",ex);
            warning = "读取设备时发生错误，可以立即重试";
        }
        finally
        {
            running = false;
            if (!closing)
            {
                panel.SetData(readings, updated, busy: false);
                if (warning != null) panel.Report(warning);
                if (queued)
                {
                    queued = false;
                    dispatcher.BeginInvoke(() => _ = RefreshAsync());
                }
            }
        }
    }

    private IEnumerable<BatteryReading> Merge(string prefix, PollResult result, bool topologyChanged, DateTime now)
    {
        var fresh = result.Fresh.ToDictionary(r => r.Id);
        foreach (var row in fresh.Values) yield return row with { SampledAt = now };
        foreach (var old in readings.Where(r => r.Id.StartsWith(prefix,StringComparison.Ordinal) && !fresh.ContainsKey(r.Id)))
            if (!topologyChanged || result.Present == null || result.Present.Contains(old.Id))
                yield return old with { Stale = true, Charging = false, FullOnPower = false };
    }

    private static async Task<PollResult> SafePoll(string source, Func<Task<PollResult>> read)
    {
        try { return await read(); }
        catch (Exception ex) { return PollResult.Failed(source,ex); }
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
        strip.Dispose();
        launcher.Visible = false;
        launcher.Dispose();
        launcherIcon.Dispose();
        menu.Dispose();
        panel.Dispose();
        dispatcher.Dispose();
        base.ExitThreadCore();
    }
}
