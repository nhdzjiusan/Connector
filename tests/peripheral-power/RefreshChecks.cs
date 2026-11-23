using System.Runtime.InteropServices;
using PeripheralPower;

internal static class RefreshChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var debounce = new RefreshDebounce();
        debounce.ScanStarted(1000);
        debounce.Add(RefreshSignal.Usb, 1100);
        debounce.Add(RefreshSignal.Bluetooth, 1200);
        check(debounce.Delay(1850) == 950 && debounce.Take() == (RefreshSignal.Usb | RefreshSignal.Bluetooth),
            "coalesce HID and topology events until the scan cooldown");
        foreach (long now in new long[] { 3000, 3550, 4100, 4650, 5150 })
            debounce.Add(RefreshSignal.Usb, now);
        check(debounce.Delay(5150) == 50 && debounce.Delay(5200) == 0,
            "continuous device messages cannot postpone refresh indefinitely");
        debounce.Take();
        debounce.ScanStarted(5300);
        debounce.Add(RefreshSignal.BluetoothValue, 5350);
        check(debounce.Delay(6000) == 0 && debounce.Take() == RefreshSignal.BluetoothValue,
            "BLE pushes need no USB scan or scan cooldown");
        debounce.Add(RefreshSignal.Resume, 6100);
        debounce.ScanFinished(8300);
        check(debounce.Delay(8300) == 800 && debounce.Delay(9100) == 0,
            "a queued resume leaves a cooldown after a long scan");

        int callbacks = 0;
        using var window = new DeviceChangeWindow(() => callbacks++);
        SendMessage(window.Handle, 0x0219, 0x0007, 0);
        SendMessage(window.Handle, 0x0219, 0x8000, 0);
        SendMessage(window.Handle, 0x0219, 0x8004, 0);
        SendMessage(window.Handle, 0x0219, 0x0017, 0);
        check(callbacks == 3, "hidden window receives device changes and ignores unrelated notifications");

        check(BluetoothBattery.DecodePercent(1, 0) == 0 && BluetoothBattery.DecodePercent(1, 100) == 100 &&
            BluetoothBattery.DecodePercent(1, 101) is null && BluetoothBattery.DecodePercent(2, 20) is null,
            "reject malformed BLE battery notifications");
        var time = new DateTime(2026, 11, 24, 12, 0, 0);
        var usb = new BatteryReading("usb-Mouse", "USB", DeviceKind.Mouse, 42, false, "USB", SampledAt: time);
        var ble = new BatteryReading("ble-sample", "BLE", DeviceKind.Keyboard, 28, false, "蓝牙", SampledAt: time, Stale: true);
        var other = ble with { Id = "ble-other" };
        var stamp = time.AddSeconds(2);
        var rows = PowerContext.MergeBluetoothNotifications([usb, ble, other], [ble with { Percent = 21, Stale = false }], stamp);
        check(rows.Contains(usb) && rows.Contains(other) && rows.Length == 3 &&
            rows.Single(r => r.Id == ble.Id) is { Stale: false, Percent: 21 } changed && changed.SampledAt == stamp,
            "BLE push refreshes only the matching device and leaves other sample timestamps");

        var screen = new Rectangle(-1600, 0, 1600, 900);
        var bar = new Rectangle(-1600, 852, 1600, 48);
        check(TaskbarGeometry.IsAnchoredBar(bar, screen) &&
            !TaskbarGeometry.IsAnchoredBar(bar with { Y = 898 }, screen) &&
            !TaskbarGeometry.IsAnchoredBar(new Rectangle(-1600, 0, 40, 900), screen),
            "monitor-local taskbar accepts negative coordinates and rejects hidden and vertical geometry");
        var layout = new TaskbarStrip.LayoutKey(10, 20, bar, screen, 96);
        check(TaskbarStrip.CanUseSnapshot(layout, layout, 199, 200) &&
            !TaskbarStrip.CanUseSnapshot(layout, layout, 200, 200) &&
            !TaskbarStrip.CanUseSnapshot(layout with { Taskbar = 11 }, layout, 199, 200) &&
            !TaskbarStrip.CanUseSnapshot(layout with { Monitor = 21, Dpi = 144 }, layout, 199, 200) &&
            !TaskbarStrip.CanUseSnapshot(layout with { Alignment = 0 }, layout, 199, 200),
            "reject stale Explorer snapshots after handle, monitor, DPI or alignment changes");

        check(TaskbarGeometry.CoversScreen(new(-1599, 1, 1598, 898), screen, 3) &&
            !TaskbarGeometry.CoversScreen(new(0, 0, 1600, 900), screen, 3) &&
            !TaskbarGeometry.CoversScreen(new(-1600, 0, 1600, 852), screen, 3),
            "fullscreen frame tolerates a thin DWM border without confusing another monitor or work area");
        var tray = new Rectangle(-270, 852, 270, 48);
        var centered = TaskbarGeometry.FindGap(bar, [new(-1180, 852, 850, 48), tray], 78, 6, 3);
        var left = TaskbarGeometry.FindGap(bar, [new(-1600, 852, 850, 48), tray], 78, 6, 3);
        check(centered.Count == 3 && left.Count == 3 && centered.End < -1180 && left.End == -276,
            "recompute a nonoverlapping taskbar slot after changing alignment");

        var invalid = new object[,]
        {
            { 0, 50000, "root", false, new double[] {-1600,852,1600,48}, true },
            { 0, 50000, "Button", false, new double[] {double.NaN,852,20,48}, true },
            { 0, 50000, "Button", false, new double[] {-1610,846,20,20}, true }
        };
        check(TaskbarGeometry.OccupiedBounds(null, bar).Length == 0 &&
            TaskbarGeometry.OccupiedBounds(new object[1, 1], bar).Length == 0 &&
            TaskbarGeometry.OccupiedBounds(invalid, bar).SequenceEqual([new Rectangle(-1600, 852, 10, 14)]),
            "taskbar clips valid UIA bounds and rejects malformed snapshots");
    }

    [DllImport("user32", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
