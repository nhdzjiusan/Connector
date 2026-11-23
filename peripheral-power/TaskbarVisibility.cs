using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PeripheralPower;

internal sealed class TaskbarVisibility
{
    private long retryAt, firstSeen;
    private int observation;
    private TaskbarStrip.LayoutKey key;
    private Rectangle slot;
    private Rectangle[] occupied = [];

    internal void Rejected(long now)
    {
        retryAt = now + 2000;
        observation = 0;
        occupied = [];
    }

    internal bool Ready(long now, TaskbarStrip.LayoutKey current, Rectangle bounds, Rectangle[] areas, int measurement)
    {
        if (observation == 0 || current != key || bounds != slot || !occupied.AsSpan().SequenceEqual(areas))
        {
            observation = measurement;
            firstSeen = now;
            key = current;
            slot = bounds;
            occupied = areas;
            return false;
        }
        return now >= retryAt && now - firstSeen >= 500 && measurement != observation;
    }
}

internal sealed class ForegroundObserver : IDisposable
{
    private const uint ForegroundChanged = 0x0003;
    private readonly WinEventCallback callback;
    private GCHandle callbackRoot;
    private nint hook;
    internal bool IsActive => hook != 0;

    internal ForegroundObserver(Action changed)
    {
        callback = (_, _, _, _, _, _, _) => changed();
        callbackRoot = GCHandle.Alloc(callback);
        const uint outOfContextSkipOwnProcess = 0x0002;
        hook = SetWinEventHook(ForegroundChanged, ForegroundChanged, 0, callback, 0, 0, outOfContextSkipOwnProcess);
        if (hook == 0)
        {
            callbackRoot.Free();
            ErrorLog.Write("前台窗口事件", new Win32Exception());
        }
    }

    public void Dispose()
    {
        if (hook == 0) return;
        if (!UnhookWinEvent(hook) && Marshal.GetLastWin32Error() != 1404) // ERROR_INVALID_HOOK_HANDLE
        {
            ErrorLog.Write("注销前台窗口事件", new Win32Exception());
            return;
        }
        hook = 0;
        callbackRoot.Free();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventCallback(nint hook, uint eventType, nint hwnd, int idObject, int idChild,
        uint thread, uint time);

    [DllImport("user32", SetLastError = true)]
    private static extern nint SetWinEventHook(uint first, uint last, nint module, WinEventCallback callback,
        uint process, uint thread, uint flags);
    [DllImport("user32", SetLastError = true)]
    private static extern bool UnhookWinEvent(nint hook);
}
