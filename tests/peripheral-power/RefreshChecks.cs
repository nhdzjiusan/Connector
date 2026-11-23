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
        check(debounce.Delay(1850, false) == 950 && debounce.TakeScan(2800) == (RefreshSignal.Usb | RefreshSignal.Bluetooth),
            "coalesce HID and topology events until the scan cooldown");
        foreach (long now in new long[] { 3000, 3550, 4100, 4650, 5150 })
            debounce.Add(RefreshSignal.Usb, now);
        check(debounce.Delay(5150, false) == 50 && debounce.Delay(5200, false) == 0,
            "continuous device messages cannot postpone refresh indefinitely");
        debounce.TakeScan(5200);
        debounce.ScanStarted(5300);
        debounce.Add(RefreshSignal.BluetoothValue, 5350);
        check(debounce.Delay(5400, true) == 100 && debounce.TakePush(5500),
            "BLE pushes need no USB scan or scan cooldown");
        debounce.Add(RefreshSignal.Resume, 6100);
        debounce.ScanFinished(8300);
        check(debounce.Delay(8300, false) == 800 && debounce.TakeScan(9100) == RefreshSignal.Resume,
            "a queued resume leaves a cooldown after a long scan");
        debounce.Add(RefreshSignal.Resume, 10000);
        debounce.VerifyLater(10000);
        debounce.Add(RefreshSignal.UsbArrival, 11400);
        check(debounce.TakeScan(11500, direct: true) == RefreshSignal.None && debounce.Delay(11500, false) == 550 &&
            debounce.TakeScan(12050) == (RefreshSignal.Resume | RefreshSignal.UsbArrival | RefreshSignal.VerifyTopology),
            "resume has a settle gate and coalesces late arrivals without a busy timer");

        var callbacks = new List<RefreshSignal>();
        using var window = new DeviceChangeWindow(callbacks.Add, register: false);
        SendMessage(window.Handle, 0x0219, 0x0007, 0);
        var info = Marshal.AllocHGlobal(32);
        try
        {
            Marshal.WriteInt32(info, 32);
            Marshal.WriteInt32(info, 4, 5);
            Marshal.WriteInt32(info, 8, 0);
            Marshal.StructureToPtr(new Guid("4D1E55B2-F16F-11CF-88CB-001111000030"), info + 12, false);
            SendMessage(window.Handle, 0x0219, 0x8000, info);
            SendMessage(window.Handle, 0x0219, 0x8004, info);
            Marshal.StructureToPtr(Guid.Empty, info + 12, false);
            SendMessage(window.Handle, 0x0219, 0x8000, info);
            SendMessage(window.Handle, 0x0219, 0x8004, 0);
            SendMessage(window.Handle, 0x0219, 0x0017, 0);
        }
        finally { Marshal.FreeHGlobal(info); }
        check(callbacks.SequenceEqual([RefreshSignal.Usb, RefreshSignal.UsbArrival, RefreshSignal.UsbRemoval]),
            "hidden window distinguishes HID arrival and removal and ignores unrelated notifications");

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
        check(PowerContext.PreferBluetoothPushes([ble with { Percent = 60 }, other],
            new Dictionary<string, BatteryReading> { [ble.Id] = rows.Single(row => row.Id == ble.Id) })
            .Single(row => row.Id == ble.Id) is { Percent: 21, SampledAt: var pushTime } && pushTime == stamp,
            "a scan that was in flight cannot replace a newer BLE push");

        var topology = new TopologyConfirmation();
        topology.Request(removal: true, resumed: true);
        var first = topology.Evaluate([usb], "usb-", new([], [], false), 100);
        var uncertain = topology.Evaluate([usb], "usb-", new([], null, true), 350);
        var tooSoon = topology.Evaluate([usb], "usb-", new([], [], false), 500);
        var confirmed = topology.Evaluate([usb], "usb-", new([], [], false), 1100);
        check(first.Verify && first.Result.Present!.Contains(usb.Id) && !uncertain.Confirmed &&
            tooSoon.Result.Present!.Contains(usb.Id) && confirmed.Confirmed && !confirmed.Verify &&
            !confirmed.Result.Present!.Contains(usb.Id),
            "wake requires two separated authoritative absences and ignores a failed enumeration");
        topology.Request(removal: true);
        check(topology.Evaluate([usb], "usb-", new([], [], false), 2000) is
            { Confirmed: true, Verify: false, Result.Present.Count: 0 }, "explicit HID removal still waits for authoritative inventory");

        var screen = new Rectangle(-1600, 0, 1600, 900);
        var bar = new Rectangle(-1600, 852, 1600, 48);
        check(TaskbarGeometry.IsAnchoredBar(bar, screen) &&
            !TaskbarGeometry.IsAnchoredBar(bar with { Y = 898 }, screen) &&
            !TaskbarGeometry.IsAnchoredBar(new Rectangle(-1600, 0, 40, 900), screen),
            "monitor-local taskbar accepts negative coordinates and rejects hidden and vertical geometry");
        var layout = new TaskbarStrip.LayoutKey(10, 20, bar, screen, 96);
        var visibility = new TaskbarVisibility();
        var slot = new Rectangle(-1550, 858, 78, 29);
        Rectangle[] evidence = [new(-1400, 852, 40, 48)];
        visibility.Rejected(100);
        check(!visibility.Ready(1100, layout, slot, evidence, 1) && !visibility.Ready(2200, layout, slot, evidence, 1) &&
            visibility.Ready(2200, layout, slot, evidence, 2), "fallback needs time and two independent matching UIA snapshots");
        visibility.Rejected(3000);
        check(!visibility.Ready(4000, layout, slot, evidence, 3) &&
            !visibility.Ready(5100, layout, slot, [new(-1401, 852, 40, 48)], 4) &&
            visibility.Ready(5700, layout, slot, [new(-1401, 852, 40, 48)], 5),
            "inconsistent UIA geometry resets the recovery sequence");
        check(TaskbarStrip.BlocksForShellState(3, true) && TaskbarStrip.BlocksForShellState(4, true) &&
            TaskbarStrip.BlocksForShellState(1, false) && !TaskbarStrip.BlocksForShellState(6, true),
            "exclusive fullscreen, presentation and locked session suppress the overlay");
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
