using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PeripheralPower;

// A non-activating, per-pixel-alpha window over free space in the primary taskbar.
// Explorer keeps ownership of its buttons, their positions, and the notification area.
internal sealed class TaskbarStrip : Form
{
    private static readonly uint TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    private readonly System.Windows.Forms.Timer tick = new();
    private readonly TaskbarVisibility recovery = new();
    private ForegroundObserver? observer;
    internal readonly record struct LayoutKey(nint Taskbar, nint Monitor, Rectangle Bar, Rectangle Screen, uint Dpi, int Alignment = -1);
    private BatteryReading[] devices = [];
    private Rectangle[] occupied = [];
    private LayoutKey? layout, measured;
    private int revision, measurement, failures;
    private long nextMeasure, expires, layoutSettledAt;
    private long blockedUntil;
    private bool measuring, dirty = true;
    private int first, count;
    private Color foreground;
    public event Action<bool>? AvailabilityChanged;
    private bool available;
    private bool measureErrorReported;

    public TaskbarStrip()
    {
        Text = "外设电量 · 任务栏";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        tick.Tick += (_, _) => LayoutStrip();
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x80000 | 0x8000000 | 0x80; return p; }
    }

    public void SetDevices(BatteryReading[] readings)
    {
        bool appearanceChanged = devices.Length != readings.Length || devices.Where((row, i) =>
            row.Kind != readings[i].Kind || row.Percent != readings[i].Percent ||
            row.Charging != readings[i].Charging || row.FullOnPower != readings[i].FullOnPower).Any();
        devices = readings;
        if (!appearanceChanged) return;
        if (readings.Length > 0) observer ??= new(WakeLayout);
        else
        {
            observer?.Dispose();
            if (observer?.IsActive != true) observer = null;
        }
        dirty = true;
        LayoutStrip();
    }

    private void Availability(bool value)
    {
        if (!value && Visible) Hide();
        if (available == value) return;
        available = value;
        AvailabilityChanged?.Invoke(value);
    }

    private void LayoutStrip()
    {
        tick.Stop();
        try { EvaluateLayout(); }
        finally
        {
            if (devices.Length > 0 && !Disposing && !IsDisposed && !tick.Enabled)
            {
                long now = Environment.TickCount64;
                long delay = available ? 1000 : layout is null || measuring ? 3000 :
                    Math.Clamp(Math.Max(nextMeasure, layoutSettledAt) - now, 100, 8000);
                tick.Interval = (int)delay;
                tick.Start();
            }
        }
    }

    private void WakeLayout()
    {
        if (devices.Length == 0 || Disposing || IsDisposed || tick.Enabled && tick.Interval <= 40) return;
        tick.Stop();
        tick.Interval = 40;
        tick.Start();
    }

    private void EvaluateLayout()
    {
        var key = devices.Length > 0 ? ReadLayout() : null;
        if (key != layout)
        {
            InvalidateSnapshot();
            layout = key;
        }
        if (key is not { } current) return;
        long now = Environment.TickCount64;
        if (!measuring && now >= nextMeasure && now >= layoutSettledAt)
            _ = MeasureAsync(current, revision, now);
        if (!CanUseSnapshot(current, measured, now, expires) || occupied.Length == 0 || now < blockedUntil)
        {
            if (available) RejectLayout(now);
            return;
        }

        var bar = current.Bar;
        float scale = current.Dpi / 96f;
        int cell = (int)Math.Ceiling(78 * scale), margin = (int)Math.Ceiling(6 * scale);
        var (bestEnd, bestCount) = TaskbarGeometry.FindGap(bar, occupied, cell, margin, devices.Length);
        if (bestCount == 0) { RejectLayout(now); return; }
        int start = bestCount == devices.Length ? 0 : (int)((Environment.TickCount64 / 4000) % devices.Length);
        int height = Math.Min(bar.Height, (int)Math.Ceiling(29 * scale));
        var bounds = new Rectangle(bestEnd - bestCount * cell, bar.Top + (bar.Height-height)/2, bestCount*cell, height);
        if (!CanOccupy(current.Taskbar, bounds))
        {
            blockedUntil = now + 1000;
            RejectLayout(now);
            return;
        }
        if (!available && !recovery.Ready(now, current, bounds, occupied, measurement)) return;
        if (dirty || !Visible || Bounds != bounds || first != start || count != bestCount)
        {
            first = start; count = bestCount;
            try
            {
                using var bitmap = Draw(devices, first, count, cell, height, scale, foreground);
                if (!PaintLayer(bitmap, bounds.Location))
                {
                    blockedUntil = now + 1000;
                    RejectLayout(now);
                    return;
                }
            }
            catch (ExternalException ex)
            {
                ErrorLog.Write("绘制任务栏电量", ex);
                blockedUntil = now + 3000;
                RejectLayout(now);
                return;
            }
            bool opening = !Visible;
            if (opening) Show();
            const uint noActivate = 0x0010, noZOrder = 0x0004;
            // Acquire the layer above Explorer once, after checking that the slot is clear.
            // Moving an already visible strip must not elevate it above another window.
            if (!SetWindowPos(Handle, opening ? -1 : 0, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                    opening ? noActivate : noActivate | noZOrder) ||
                !CanUseSnapshot(current, measured, now, expires))
            { RejectLayout(now); return; }
            dirty = false;
        }
        // Explorer or another topmost window may have moved ahead of us. Do not steal its z-order.
        if (RootAt(new Point(bounds.Left + bounds.Width / 2, bounds.Top + height / 2)) != Handle &&
            !IsAboveTaskbar(Handle, current.Taskbar))
        {
            blockedUntil = now + 1000;
            RejectLayout(now);
            return;
        }
        Availability(true);
    }

    internal static bool CanUseSnapshot(LayoutKey current, LayoutKey? measured, long now, long expires) =>
        measured == current && now < expires;

    private void InvalidateSnapshot()
    {
        revision++;
        measured = null;
        occupied = [];
        layoutSettledAt = Environment.TickCount64 + 250;
        nextMeasure = layoutSettledAt;
        blockedUntil = 0;
        dirty = true;
        RejectLayout(Environment.TickCount64);
    }

    private void RejectLayout(long now)
    {
        recovery.Rejected(now);
        Availability(false);
    }

    internal void NotifyLayoutChanged()
    {
        if (devices.Length == 0 || Disposing || IsDisposed) return;
        InvalidateSnapshot();
        WakeLayout();
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // Display, per-monitor DPI and Explorer alignment/theme/settings broadcasts.
        if ((m.Msg is (0x007E or 0x02E0 or 0x001A or 0x031A) ||
            TaskbarCreatedMessage != 0 && m.Msg == TaskbarCreatedMessage) && !Disposing && !IsDisposed)
        {
            NotifyLayoutChanged();
        }
    }

    private async Task MeasureAsync(LayoutKey key, int requestedRevision, long started)
    {
        measuring = true;
        bool success = false;
        try
        {
            var result = await Task.Run(() => TaskbarGeometry.Read(key.Taskbar, key.Bar));
            if (IsDisposed || Disposing || requestedRevision != revision || layout != key) return;
            // The native notification host covers the clock and notification overflow trigger
            // even if the UIA provider omits part of its subtree.
            var tray = FindWindowEx(key.Taskbar, 0, "TrayNotifyWnd", null);
            if (Environment.TickCount64 >= started + 4000 || result.Length == 0 || tray == 0 ||
                !IsWindowVisible(tray) || !GetWindowRect(tray, out var trayRect) ||
                !TaskbarGeometry.HasTaskEvidence(key.Bar, trayRect.RectValue, result))
            {
                measured = null;
                RejectLayout(Environment.TickCount64);
                return;
            }
            occupied = result.Append(trayRect.RectValue).Distinct()
                .OrderBy(r => r.Left).ThenBy(r => r.Top).ThenBy(r => r.Width).ThenBy(r => r.Height).ToArray();
            measureErrorReported = false;
            var ink = Equals(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme", 0), 1) ? Color.FromArgb(30,30,30) : Color.White;
            if (ink != foreground) { foreground = ink; dirty = true; }
            measured = key;
            expires = started + 4000;
            measurement++;
            success = true;
        }
        catch (Exception e) when (e is InvalidOperationException or COMException or UnauthorizedAccessException or
            ArgumentException or System.Security.SecurityException or SafeArrayTypeMismatchException or SafeArrayRankMismatchException)
        {
            if (IsDisposed || Disposing || requestedRevision != revision) return;
            measured = null;
            RejectLayout(Environment.TickCount64);
            if (!measureErrorReported) ErrorLog.Write("任务栏定位",e);
            measureErrorReported = true;
        }
        finally
        {
            measuring = false;
            if (requestedRevision == revision && !IsDisposed && !Disposing)
            {
                failures = success ? 0 : Math.Min(3, failures + 1);
                nextMeasure = Environment.TickCount64 + (success ? 2000 : Math.Min(8000, failures * 3000));
            }
            WakeLayout();
        }
    }

    private static LayoutKey? ReadLayout()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == 0 || !IsWindowVisible(taskbar) || !GetWindowRect(taskbar, out var raw)) return null;
        var bar = raw.RectValue;
        var monitor = MonitorFromWindow(taskbar, 0);
        var screen = Screen.FromHandle(taskbar).Bounds;
        uint dpi = GetDpiForWindow(taskbar);
        var appBar = new AppBarData { Size = (uint)Marshal.SizeOf<AppBarData>() };
        const uint getState = 0x00000004, autoHide = 0x00000001;
        if (monitor == 0 || dpi is < 48 or > 768 || !TaskbarGeometry.IsAnchoredBar(bar, screen) ||
            (SHAppBarMessage(getState, ref appBar) & autoHide) != 0) return null;

        var foreground = GetForegroundWindow();
        bool applicationInFront = IsForegroundApp(foreground, taskbar);
        if (SHQueryUserNotificationState(out int notificationState) >= 0 &&
            BlocksForShellState(notificationState, applicationInFront)) return null;
        if (applicationInFront)
        {
            // Secondary Explorer taskbars have a different layout and notification host.
            // Use the OS tray while the active window is on another display.
            if (MonitorFromWindow(foreground, 2) != monitor ||
                ForegroundBounds(foreground) is Rectangle window && TaskbarGeometry.CoversScreen(window, screen, 3))
                return null;
        }
        return new(taskbar, monitor, bar, screen, dpi, ReadAlignment());
    }

    internal static bool BlocksForShellState(int status, bool hasForegroundApp) =>
        status is 1 or 3 or 4 || status == 2 && !hasForegroundApp;

    private static int ReadAlignment()
    {
        // Win11 can switch alignment without changing the bar's native rectangle.
        // If this setting is unavailable, settings messages and periodic UIA still apply.
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                "TaskbarAl", -1) is int value ? value : -1;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        { return -1; }
    }

    private bool CanOccupy(nint taskbar, Rectangle bounds)
    {
        var foreground = GetForegroundWindow();
        if (IsForegroundApp(foreground, taskbar) && ForegroundBounds(foreground)?.IntersectsWith(bounds) == true)
            return false;
        nint own = IsHandleCreated ? Handle : 0;
        int y = bounds.Top + bounds.Height / 2;
        return ClearAt(bounds.Left + 1) && ClearAt(bounds.Left + bounds.Width / 2) && ClearAt(bounds.Right - 2);

        bool ClearAt(int x)
        {
            var root = RootAt(new Point(x, y));
            return root != 0 && (root == taskbar || root == own);
        }
    }

    private static nint RootAt(Point point) => GetAncestor(WindowFromPoint(point), 2);

    private static bool IsAboveTaskbar(nint strip, nint taskbar)
    {
        // The layered strip can be transparent at the sampled point.
        const uint nextInZOrder = 2;
        for (int i = 0; i < 128 && strip != 0; i++)
        {
            strip = GetWindow(strip, nextInZOrder);
            if (strip == taskbar) return true;
        }
        return false;
    }

    private static bool IsForegroundApp(nint window, nint taskbar)
    {
        if (window == 0 || window == taskbar || window == GetShellWindow() || !IsWindowVisible(window) || IsIconic(window))
            return false;
        var name = new System.Text.StringBuilder(80);
        GetClassName(window, name, name.Capacity);
        return name.ToString() is not ("WorkerW" or "Progman");
    }

    private static Rectangle? ForegroundBounds(nint window)
    {
        const int extendedFrameBounds = 9;
        if (DwmGetWindowAttribute(window, extendedFrameBounds, out var rect, Marshal.SizeOf<Rect>()) == 0)
            return rect.RectValue;
        return GetWindowRect(window, out rect) ? rect.RectValue : null;
    }

    internal static Bitmap Draw(BatteryReading[] devices, int first, int count, int cell, int height, float scale, Color ink)
    {
        var bmp = new Bitmap(count * cell, height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(2, 0, 0, 0)); // Make the entire strip clickable.
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe UI", 14, FontStyle.Regular, GraphicsUnit.Pixel);
        using var format = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center };
        for (int i = 0; i < count; i++)
        {
            var b = devices[(first + i) % devices.Length];
            g.ResetTransform();
            g.TranslateTransform(i * cell, height / 2f - 12 * scale);
            g.ScaleTransform(scale, scale);
            var state = g.Save();
            g.TranslateTransform(0, 3); g.ScaleTransform(.65f,.65f);
            TrayArt.DeviceGlyph(g, b.Kind, 0, 0, ink);
            g.Restore(state);
            string text = b.Percent + "%";
            using var brush = new SolidBrush(b.Percent < 15 ? Color.FromArgb(245,69,77) : ink);
            g.DrawString(text, font, brush, new RectangleF(22,0,42,24), format);
            float x = 25 + g.MeasureString(text, font, PointF.Empty, format).Width;
            if (b.FullOnPower)
            {
                using var pen = new Pen(Color.FromArgb(30,190,105),2.4f) { StartCap=LineCap.Round, EndCap=LineCap.Round, LineJoin=LineJoin.Round };
                g.DrawLines(pen, new PointF[]{new(x,12),new(x+3,15),new(x+10,7)});
            }
            else if (b.Charging)
            {
                using var blue = new SolidBrush(Color.FromArgb(45,153,255));
                g.FillPolygon(blue, new PointF[]{new(x+6,3),new(x,13),new(x+4,13),new(x+2,21),new(x+11,10),new(x+6,10)});
            }
        }
        return bmp;
    }

    private bool PaintLayer(Bitmap image, Point where)
    {
        var screen = GetDC(0);
        if (screen == 0) return false;
        nint dc = 0, bmp = 0, previous = 0;
        try
        {
            dc = CreateCompatibleDC(screen);
            if (dc == 0) return false;
            bmp = image.GetHbitmap(Color.FromArgb(0));
            previous = SelectObject(dc,bmp);
            if (previous == 0 || previous == -1) return false;
            var src = Point.Empty; var size = image.Size;
            var blend = new Blend{Alpha = 255, Format = 1};
            return UpdateLayeredWindow(Handle, screen, ref where, ref size, dc, ref src, 0, ref blend, 2);
        }
        finally
        {
            if (previous != 0 && previous != -1) SelectObject(dc,previous);
            if (bmp != 0) DeleteObject(bmp);
            if (dc != 0) DeleteDC(dc);
            ReleaseDC(0,screen);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            observer?.Dispose();
            observer = null;
            tick.Dispose();
        }
        base.Dispose(disposing);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int L,T,R,B; public Rectangle RectValue => Rectangle.FromLTRB(L,T,R,B); }
    [StructLayout(LayoutKind.Sequential)] private struct AppBarData { public uint Size; public nint Window; public uint Callback, Edge; public Rect Bounds; public nint Param; }
    [StructLayout(LayoutKind.Sequential, Pack=1)] private struct Blend { public byte Op,Flags,Alpha,Format; }
    [DllImport("user32",CharSet=CharSet.Unicode)] private static extern nint FindWindow(string cls,string? title);
    [DllImport("user32",CharSet=CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32",CharSet=CharSet.Unicode)] private static extern nint FindWindowEx(nint parent,nint child,string cls,string? title);
    [DllImport("user32")] private static extern nint MonitorFromWindow(nint hwnd,uint flags);
    [DllImport("user32")] private static extern bool IsIconic(nint hwnd);
    [DllImport("shell32")] private static extern nuint SHAppBarMessage(uint message,ref AppBarData data);
    [DllImport("shell32")] private static extern int SHQueryUserNotificationState(out int status);
    [DllImport("dwmapi")] private static extern int DwmGetWindowAttribute(nint hwnd,int attribute,out Rect value,int size);
    [DllImport("user32")] private static extern bool GetWindowRect(nint hwnd,out Rect r);
    [DllImport("user32")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32")] private static extern nint GetShellWindow();
    [DllImport("user32")] private static extern nint GetForegroundWindow();
    [DllImport("user32")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32")] private static extern nint GetWindow(nint hwnd,uint command);
    [DllImport("user32")] private static extern nint GetAncestor(nint hwnd,uint flags);
    [DllImport("user32",CharSet=CharSet.Unicode)] private static extern int GetClassName(nint hwnd,System.Text.StringBuilder name,int size);
    [DllImport("user32")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32")] private static extern bool SetWindowPos(nint hwnd,nint after,int x,int y,int w,int h,uint flags);
    [DllImport("user32")] private static extern bool UpdateLayeredWindow(nint hwnd,nint dest,ref Point at,ref Size size,nint source,ref Point origin,uint key,ref Blend blend,uint flags);
    [DllImport("user32")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32")] private static extern int ReleaseDC(nint hwnd,nint dc);
    [DllImport("gdi32")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32")] private static extern nint SelectObject(nint dc,nint obj);
    [DllImport("gdi32")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32")] private static extern bool DeleteObject(nint obj);
}
