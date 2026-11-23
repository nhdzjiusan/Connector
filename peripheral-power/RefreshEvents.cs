using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PeripheralPower;

[Flags]
internal enum RefreshSignal
{
    None = 0, Usb = 1, Bluetooth = 2, Resume = 4, BluetoothValue = 8,
    UsbArrival = 16, UsbRemoval = 32, VerifyTopology = 64, Manual = 128
}

internal sealed class RefreshDebounce
{
    private long firstEvent, lastEvent, nextScan, resumeAt;
    private long pushAt = long.MaxValue, verifyAt = long.MaxValue;
    private RefreshSignal pending;

    internal bool ResumePending => (pending & RefreshSignal.Resume) != 0;

    internal void Add(RefreshSignal signal, long now)
    {
        if ((signal & RefreshSignal.BluetoothValue) != 0)
            pushAt = Math.Min(pushAt, now + 150);
        signal &= ~RefreshSignal.BluetoothValue;
        if (signal == RefreshSignal.None) return;
        if (pending == RefreshSignal.None) firstEvent = now;
        pending |= signal;
        lastEvent = now;
        if ((signal & RefreshSignal.Resume) != 0) resumeAt = now + 1500;
    }

    internal void VerifyLater(long now) => verifyAt = Math.Min(verifyAt, now + 1000);
    internal void ScanStarted(long now) => nextScan = now + 1800;
    internal void ScanFinished(long now) => nextScan = Math.Max(nextScan, now + 800);

    private long EventDue => pending == RefreshSignal.None ? long.MaxValue
        : Math.Max(resumeAt, Math.Min(firstEvent + 2200, lastEvent + 650));

    internal int? Delay(long now, bool running)
    {
        long due = pushAt;
        long scan = ResumePending ? EventDue : Math.Min(EventDue, verifyAt);
        if (!running) due = Math.Min(due, Math.Max(Math.Max(resumeAt, nextScan), scan));
        return due == long.MaxValue ? null : (int)Math.Clamp(due - now, 0, int.MaxValue);
    }

    internal bool TakePush(long now)
    {
        if (now < pushAt) return false;
        pushAt = long.MaxValue;
        return true;
    }

    internal RefreshSignal TakeScan(long now, bool direct = false)
    {
        if (now < resumeAt || ResumePending && now < EventDue || !direct && now < nextScan) return RefreshSignal.None;
        RefreshSignal result = direct ? RefreshSignal.Manual : RefreshSignal.None;
        if (now >= EventDue || direct && ResumePending)
        {
            result |= pending;
            pending = RefreshSignal.None;
        }
        if (now >= verifyAt)
        {
            result |= RefreshSignal.VerifyTopology;
            verifyAt = long.MaxValue;
        }
        return result;
    }
}

internal sealed class TopologyConfirmation
{
    private readonly Dictionary<string, long> absent = new(StringComparer.Ordinal);
    private bool pending, explicitRemoval;

    internal void Request(bool removal, bool resumed = false)
    {
        pending = true;
        if (resumed)
        {
            absent.Clear();
            explicitRemoval = false;
        }
        else explicitRemoval |= removal;
    }

    internal (PollResult Result, bool Confirmed, bool Verify) Evaluate(BatteryReading[] old, string prefix,
        PollResult result, long now)
    {
        if (!pending || result.Present is null) return (result, false, false);

        var present = new HashSet<string>(result.Present, StringComparer.Ordinal);
        present.UnionWith(result.Fresh.Select(r => r.Id));
        var freshAbsences = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in old)
        {
            if (!row.Id.StartsWith(prefix, StringComparison.Ordinal) || present.Contains(row.Id)) continue;
            if (explicitRemoval) continue;
            long since = absent.GetValueOrDefault(row.Id, now);
            if (now - since >= 1000) continue;
            freshAbsences[row.Id] = since;
            present.Add(row.Id);
        }
        absent.Clear();
        foreach (var item in freshAbsences) absent.Add(item.Key, item.Value);
        explicitRemoval = false;
        pending = absent.Count != 0;
        return (result with { Present = present }, true, pending);
    }
}

internal sealed class DeviceChangeWindow : NativeWindow, IDisposable
{
    private const int DeviceInterface = 5;
    private static readonly Guid HidInterface = new("4D1E55B2-F16F-11CF-88CB-001111000030");
    private readonly Action<RefreshSignal> changed;
    private readonly Action? layoutChanged;
    private nint registration;

    internal DeviceChangeWindow(Action<RefreshSignal> changed, bool register = true, Action? layoutChanged = null)
    {
        this.changed = changed;
        this.layoutChanged = layoutChanged;
        CreateHandle(new CreateParams { Caption = "PeripheralPower device events", Style = unchecked((int)0x80000000) });
        if (!register) return;
        var filter = new BroadcastInterface { Size = Marshal.SizeOf<BroadcastInterface>(), Type = DeviceInterface, Class = HidInterface };
        registration = RegisterDeviceNotification(Handle, ref filter, 0);
        if (registration == 0) ErrorLog.Write("HID 设备通知", new Win32Exception());
    }

    internal static RefreshSignal Classify(int code, bool hidInterface) => code switch
    {
        0x0007 => RefreshSignal.Usb,
        0x8000 when hidInterface => RefreshSignal.UsbArrival,
        0x8004 when hidInterface => RefreshSignal.UsbRemoval,
        _ => RefreshSignal.None
    };

    protected override void WndProc(ref Message m)
    {
        if (m.Msg is 0x007E or 0x001A) layoutChanged?.Invoke();
        if (m.Msg == 0x0219)
        {
            int code = (int)m.WParam;
            bool hid = false;
            if (code is 0x8000 or 0x8004 && m.LParam != 0 && Marshal.ReadInt32(m.LParam) >= Marshal.SizeOf<BroadcastInterface>() &&
                Marshal.ReadInt32(m.LParam, sizeof(int)) == DeviceInterface)
                hid = Marshal.PtrToStructure<BroadcastInterface>(m.LParam).Class == HidInterface;
            var signal = Classify(code, hid);
            if (signal != RefreshSignal.None) changed(signal);
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (registration != 0)
        {
            if (!UnregisterDeviceNotification(registration)) ErrorLog.Write("注销 HID 设备通知", new Win32Exception());
            registration = 0;
        }
        if (Handle != 0) DestroyHandle();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BroadcastInterface
    {
        public int Size, Type, Reserved;
        public Guid Class;
        public ushort Name;
    }

    [DllImport("user32", EntryPoint = "RegisterDeviceNotificationW", SetLastError = true)]
    private static extern nint RegisterDeviceNotification(nint window, ref BroadcastInterface filter, uint flags);
    [DllImport("user32", SetLastError = true)]
    private static extern bool UnregisterDeviceNotification(nint notification);
}
