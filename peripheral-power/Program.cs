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
            UsbBattery.InitializeRules();
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
    internal static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PeripheralPower");
    private static readonly string IntervalFile = Path.Combine(Folder, "interval.txt");
    private static readonly string SettingsFile = Path.Combine(Folder,"settings.json");
    private sealed record Options(int Interval = 10, bool Startup = false, bool Notifications = true, string Theme = "system");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static Options options = new();
    internal static bool Notifications => options.Notifications;
    internal static string Theme => options.Theme;
    private static string LaunchCommand => $"\"{Application.ExecutablePath}\" --quiet";

    public static int LoadInterval()
    {
        try
        {
            if (File.Exists(SettingsFile))
                options = JsonSerializer.Deserialize<Options>(File.ReadAllText(SettingsFile),Json) ?? new();
            else
            {
                int minutes = File.Exists(IntervalFile) && int.TryParse(File.ReadAllText(IntervalFile), out int n) ? n : 10;
                Save(new(Math.Clamp(minutes,5,15),StartsAtLogin()));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException)
        { ErrorLog.Write("读取设置",ex); }
        return Math.Clamp(options.Interval,5,15);
    }

    public static void SaveInterval(int value) => Save(options with {Interval=value});
    public static void SetNotifications(bool value) => Save(options with {Notifications=value});
    public static void SyncStartup(bool value) { if (options.Startup != value) Save(options with {Startup=value}); }
    private static void Save(Options next)
    {
        Directory.CreateDirectory(Folder);
        var temp = SettingsFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(next,Json));
        File.Move(temp, SettingsFile, overwrite: true);
        options = next;
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
    private BatteryPanel? panel;
    private bool startupEnabled;
    private string? startupWarning, lastWarning;
    private readonly System.Windows.Forms.Timer poll = new();
    private readonly System.Windows.Forms.Timer deviceDelay = new();
    private readonly RefreshDebounce refreshEvents = new();
    private int postedSignals;
    private readonly TaskbarStrip strip = new();
    private readonly ContextMenuStrip menu = new();
    private readonly NotifyIcon launcher = new();
    private readonly Icon launcherIcon = TrayArt.MakeLauncher();
    private readonly RegisteredWaitHandle openWait;
    private readonly DeviceWatcher? watcher;
    private readonly DeviceChangeWindow deviceWindow;
    private readonly BluetoothBattery.Notifications bluetoothNotifications;
    private BatteryReading[] readings = [];
    private DateTime? updated;
    private bool running;
    private volatile bool closing;
    private bool restartOffered;
    private bool usbChanged, bleChanged;

    public PowerContext(EventWaitHandle openPanel)
    {
        _ = dispatcher.Handle;
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        int minutes = Settings.LoadInterval();
        bool repaired = false;
        try { repaired = Settings.RepairStartup(); startupEnabled = Settings.StartsAtLogin(); Settings.SyncStartup(startupEnabled); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ErrorLog.Write("读取自启动设置",ex);
            startupWarning = "无法读取自启动设置";
        }

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
        strip.AvailabilityChanged += visible => { stripVisible = visible; UpdateFallback(); };

        poll.Interval = checked(minutes * 60_000);
        poll.Tick += (_, _) => _ = RefreshAsync();
        poll.Start();
        deviceDelay.Tick += (_, _) => ProcessRefreshEvents();

        deviceWindow = new(() => SignalRefresh(RefreshSignal.Usb));
        bluetoothNotifications = new(() => SignalRefresh(RefreshSignal.BluetoothValue),
            () => SignalRefresh(RefreshSignal.Bluetooth));
        DeviceList.Local.Changed += DeviceChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        try { watcher = BluetoothBattery.Watch(() => SignalRefresh(RefreshSignal.Bluetooth)); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        { ErrorLog.Write("蓝牙设备监听",ex); }
        openWait = ThreadPool.RegisterWaitForSingleObject(openPanel, (_, _) => OnUi(() => ShowPanel()), null, -1, false);
        dispatcher.BeginInvoke(() => _ = RefreshAsync());
    }

    public void ShowPanel(Point? near = null)
    {
        panel ??= CreatePanel();
        panel.SetData(readings, updated, running);
        if (startupWarning != null || lastWarning != null) panel.Report(startupWarning ?? lastWarning!);
        startupWarning = null;
        panel.Open(near);
        if (updated == null || DateTime.Now - updated.Value > TimeSpan.FromSeconds(30)) _ = RefreshAsync();
    }

    private BatteryPanel CreatePanel()
    {
        var view = new BatteryPanel();
        view.SetInterval(poll.Interval / 60_000);
        view.SetStartup(startupEnabled);
        view.SetNotifications(Settings.Notifications);
        view.IntervalChanged += SetInterval;
        view.StartupChanged += enabled => SetStartup(enabled);
        view.NotificationsChanged += enabled =>
        {
            try { Settings.SetNotifications(enabled); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { ErrorLog.Write("低电量通知设置",ex); view.SetNotifications(!enabled); view.Report("通知设置保存失败"); }
        };
        view.RefreshRequested += () => _ = RefreshAsync();
        view.ExitRequested += () =>
        {
            if (startupEnabled && MessageBox.Show(view,"本次退出后，下次登录仍将自动启动。现在退出？",
                "外设电量",MessageBoxButtons.OKCancel,MessageBoxIcon.Question) != DialogResult.OK) return;
            ExitThread();
        };
        return view;
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
        SignalRefresh(RefreshSignal.Usb);

    private void PowerChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Resume) SignalRefresh(RefreshSignal.Resume);
    }

    private void SignalRefresh(RefreshSignal signal)
    {
        if (closing || Interlocked.Or(ref postedSignals, (int)signal) != 0) return;
        OnUi(() =>
        {
            var pending = (RefreshSignal)Interlocked.Exchange(ref postedSignals, 0);
            refreshEvents.Add(pending, Environment.TickCount64);
            ArmRefreshTimer();
        });
    }

    private void ArmRefreshTimer()
    {
        deviceDelay.Stop();
        if (closing || running || refreshEvents.Pending == RefreshSignal.None) return;
        deviceDelay.Interval = Math.Max(1, refreshEvents.Delay(Environment.TickCount64));
        deviceDelay.Start();
    }

    private void ProcessRefreshEvents()
    {
        deviceDelay.Stop();
        if (closing || running) return;
        if (refreshEvents.Delay(Environment.TickCount64) > 0) { ArmRefreshTimer(); return; }
        var signals = refreshEvents.Take();
        if ((signals & RefreshSignal.Resume) != 0) bluetoothNotifications.Reset();
        if ((signals & RefreshSignal.BluetoothValue) != 0) ApplyBluetoothNotifications();
        var scan = signals & ~RefreshSignal.BluetoothValue;
        if (scan == RefreshSignal.None) return;
        usbChanged |= (scan & (RefreshSignal.Usb | RefreshSignal.Resume)) != 0;
        bleChanged |= (scan & (RefreshSignal.Bluetooth | RefreshSignal.Resume)) != 0;
        _ = RefreshAsync();
    }

    private void ApplyBluetoothNotifications()
    {
        var fresh = bluetoothNotifications.TakeUpdates();
        if (fresh.Length == 0) return;
        var now = DateTime.Now;
        readings = MergeBluetoothNotifications(readings, fresh, now);
        updated = now;
        strip.SetDevices(readings);
        UpdateFallback();
        NotifyLow();
        panel?.SetData(readings, updated, busy: false);
    }

    internal static BatteryReading[] MergeBluetoothNotifications(BatteryReading[] old, BatteryReading[] fresh, DateTime now)
    {
        var ids = fresh.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        return old.Where(row => !ids.Contains(row.Id)).Concat(fresh.Select(row => row with { SampledAt = now }))
            .OrderBy(row => row.Kind).ThenBy(row => row.Name).ToArray();
    }

    private void OnUi(Action action)
    {
        if (closing) return;
        try { dispatcher.BeginInvoke(() => { if (!closing) action(); }); }
        catch (InvalidOperationException ex) { if (!closing) ErrorLog.Write("设备事件分发",ex); }
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
            panel?.SetInterval(poll.Interval / 60_000);
            panel?.Report("刷新间隔保存失败");
        }
    }

    private bool SetStartup(bool enabled)
    {
        try
        {
            Settings.SetStartup(enabled);
            startupEnabled = enabled;
            Settings.SyncStartup(enabled);
            panel?.Report(enabled ? "已开启登录自启动" : "已关闭登录自启动");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            panel?.SetStartup(startupEnabled);
            ErrorLog.Write("保存自启",ex);
            panel?.Report("无法保存自启动设置");
            return false;
        }
    }

    private async Task RefreshAsync()
    {
        if (closing || running) return;

        running = true;
        refreshEvents.ScanStarted(Environment.TickCount64);
        deviceDelay.Stop();
        bool removeUsb = usbChanged, removeBle = bleChanged;
        usbChanged = bleChanged = false;
        string? warning = null;
        panel?.SetData(readings, updated, busy: true);
        try
        {
            var usb = SafePoll("USB", () => UsbBattery.ReadAsync());
            var bluetooth = SafePoll("蓝牙", () => BluetoothBattery.ReadAsync(bluetoothNotifications));
            await Task.WhenAll(usb, bluetooth);
            if (closing) return;
            var now = DateTime.Now;
            usbChanged |= removeUsb && usb.Result.Present == null;
            bleChanged |= removeBle && bluetooth.Result.Present == null;
            readings = Merge(readings,"usb-",usb.Result,removeUsb,now).Concat(Merge(readings,"ble-",bluetooth.Result,removeBle,now))
                .OrderBy(r => r.Kind).ThenBy(r => r.Name).ToArray();
            if (usb.Result.Fresh.Length + bluetooth.Result.Fresh.Length > 0) updated = now;
            if (usb.Result.Partial || bluetooth.Result.Partial || readings.Any(r => r.Stale))
                warning = "部分设备未响应 · 已保留上次读数";
            strip.SetDevices(readings);
            UpdateFallback();
            NotifyLow();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            ErrorLog.Write("刷新",ex);
            warning = "读取设备时发生错误，可以立即重试";
        }
        finally
        {
            running = false;
            refreshEvents.ScanFinished(Environment.TickCount64);
            if (!closing)
            {
                lastWarning = warning;
                panel?.SetData(readings, updated, busy: false);
                if (warning != null) panel?.Report(warning);
                ArmRefreshTimer();
            }
        }
    }

    internal static IEnumerable<BatteryReading> Merge(BatteryReading[] readings,string prefix, PollResult result, bool topologyChanged, DateTime now)
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

    private bool stripVisible;
    private readonly Dictionary<(DeviceKind,int,bool,bool),Icon> art = [];
    private readonly Dictionary<string,NotifyIcon> fallback = [];
    private readonly Dictionary<string,int> alerted = [];
    private void UpdateFallback()
    {
        var needed = stripVisible ? [] : readings.Select(r => r.Id).ToHashSet();
        var activeArt = new HashSet<(DeviceKind,int,bool,bool)>();
        foreach (var id in fallback.Keys.Where(id => !needed.Contains(id)).ToArray())
        { fallback[id].Dispose(); fallback.Remove(id); }
        foreach (var r in readings.Where(r => needed.Contains(r.Id)))
        {
            if (!fallback.TryGetValue(r.Id,out var icon)) { fallback[r.Id] = icon = new(); ConfigureIcon(icon); }
            var key = (r.Kind,r.Percent,r.Charging,r.IsReference);
            activeArt.Add(key);
            if (!art.TryGetValue(key,out var drawing)) art[key] = drawing = TrayArt.MakeReading(r);
            icon.Icon = drawing;
            var tip = $"{r.Name} {r.Percent}% {r.PowerStatus}".TrimEnd();
            icon.Text = tip[..Math.Min(63,tip.Length)];
            icon.Visible = true;
        }
        foreach (var key in art.Keys.Where(k => !activeArt.Contains(k)).ToArray())
        {
            art[key].Dispose();
            art.Remove(key);
        }
        launcher.Visible = restartOffered || (!stripVisible && fallback.Count == 0);
    }

    private void NotifyLow()
    {
        var low = CollectLowBatteryAlerts(readings, alerted, Settings.Notifications);
        if (low.Count == 0 || restartOffered) return;
        launcher.Visible = true;
        launcher.ShowBalloonTip(5000, "外设电量", string.Join("；", low), ToolTipIcon.Warning);
    }

    internal static List<string> CollectLowBatteryAlerts(BatteryReading[] readings, Dictionary<string, int> alerted, bool enabled)
    {
        foreach (var id in alerted.Keys.Where(id => !readings.Any(r => r.Id == id)).ToArray()) alerted.Remove(id);
        var low = new List<string>();
        foreach (var r in readings.Where(r => !r.Stale))
        {
            if (r.Charging || r.FullOnPower) { alerted.Remove(r.Id); continue; }
            if (r.IsReference || r.ReportedPower is HidPower.ExternalReference or HidPower.Unknown) continue;
            if (r.Percent > 20) { alerted.Remove(r.Id); continue; }
            int threshold = r.Percent <= 5 ? 5 : r.Percent <= 10 ? 10 : 20;
            if (!enabled || alerted.TryGetValue(r.Id,out int sent) && sent <= threshold) continue;
            alerted[r.Id] = threshold;
            low.Add($"{r.Name} 剩余 {r.Percent}%");
        }
        return low;
    }

    protected override void ExitThreadCore()
    {
        if (closing) return;
        closing = true;
        openWait.Unregister(null);
        SystemEvents.PowerModeChanged -= PowerChanged;
        DeviceList.Local.Changed -= DeviceChanged;
        if (watcher?.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
        deviceWindow.Dispose();
        bluetoothNotifications.Dispose();
        poll.Dispose();
        deviceDelay.Dispose();
        strip.Dispose();
        foreach (var icon in fallback.Values) icon.Dispose();
        foreach (var drawing in art.Values) drawing.Dispose();
        launcher.Visible = false;
        launcher.Dispose();
        launcherIcon.Dispose();
        menu.Dispose();
        panel?.Dispose();
        dispatcher.Dispose();
        base.ExitThreadCore();
    }
}
