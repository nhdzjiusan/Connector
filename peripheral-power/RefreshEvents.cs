namespace PeripheralPower;

[Flags]
internal enum RefreshSignal { None = 0, Usb = 1, Bluetooth = 2, Resume = 4, BluetoothValue = 8 }

internal sealed class RefreshDebounce
{
    private const int QuietMs = 650;
    private const int MaxWaitMs = 2200;
    private const int ScanCooldownMs = 1800;
    private const int IdleAfterScanMs = 800;
    private long firstEvent, lastEvent;
    private long nextScan;
    internal RefreshSignal Pending { get; private set; }

    internal void Add(RefreshSignal signal, long now)
    {
        if (signal == RefreshSignal.None) return;
        if (Pending == RefreshSignal.None) firstEvent = now;
        Pending |= signal;
        lastEvent = now;
    }

    internal int Delay(long now)
    {
        long due = Math.Min(firstEvent + MaxWaitMs, lastEvent + QuietMs);
        if ((Pending & ~RefreshSignal.BluetoothValue) != RefreshSignal.None) due = Math.Max(due, nextScan);
        return (int)Math.Clamp(due - now, 0, int.MaxValue);
    }

    internal void ScanStarted(long now) => nextScan = now + ScanCooldownMs;
    internal void ScanFinished(long now) => nextScan = Math.Max(nextScan, now + IdleAfterScanMs);

    internal RefreshSignal Take()
    {
        var result = Pending;
        Pending = RefreshSignal.None;
        return result;
    }
}

internal sealed class DeviceChangeWindow : NativeWindow, IDisposable
{
    private readonly Action changed;

    internal DeviceChangeWindow(Action changed)
    {
        this.changed = changed;
        // WM_DEVICECHANGE broadcasts go to top-level windows, including this hidden window.
        CreateHandle(new CreateParams { Caption = "PeripheralPower device events", Style = unchecked((int)0x80000000) });
    }

    protected override void WndProc(ref Message m)
    {
        const int deviceChange = 0x0219;
        if (m.Msg == deviceChange && m.WParam.ToInt64() is 0x0007 or 0x8000 or 0x8004)
            changed();
        base.WndProc(ref m);
    }

    public void Dispose() => DestroyHandle();
}
