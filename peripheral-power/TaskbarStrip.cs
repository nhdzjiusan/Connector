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
    private readonly System.Windows.Forms.Timer tick = new() { Interval = 500 };
    private BatteryReading[] devices = [];
    private Rectangle[] occupied = [];
    private Rectangle measuredBar;
    private long nextMeasure;
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
        if (devices.SequenceEqual(readings)) return;
        devices = readings;
        tick.Enabled = readings.Length > 0;
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
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (devices.Length == 0 || taskbar == 0 || !IsWindowVisible(taskbar) ||
            !GetWindowRect(taskbar, out var raw)) { Availability(false); return; }
        var bar = raw.RectValue;
        var screen = Screen.FromHandle(taskbar).Bounds;
        if (bar.Width < bar.Height || Rectangle.Intersect(bar, screen).Height < bar.Height * .8 ||
            IsFullScreen(screen, taskbar)) { Availability(false); return; }
        if (!measuring && (bar != measuredBar || Environment.TickCount64 >= nextMeasure))
            _ = MeasureAsync(taskbar, bar);
        if (measuredBar != bar || occupied.Length == 0) { Availability(false); return; }

        float scale = GetDpiForWindow(taskbar) / 96f;
        int cell = (int)Math.Ceiling(78 * scale), margin = (int)Math.Ceiling(6 * scale);
        int end = bar.Right - margin, bestEnd = 0, bestCount = 0;
        // Consider the rightmost gap first. If necessary rotate through devices in a smaller gap.
        foreach (var box in occupied.OrderByDescending(r => r.Right).Append(new Rectangle(bar.Left,bar.Top,0,bar.Height)))
        {
            int fits = Math.Min(devices.Length, (end - box.Right - margin) / cell);
            if (fits > bestCount) { bestCount = fits; bestEnd = end; }
            end = Math.Min(end, box.Left - margin);
            if (bestCount == devices.Length) break;
        }
        if (bestCount == 0) { Availability(false); return; }
        int start = bestCount == devices.Length ? 0 : (int)((Environment.TickCount64 / 4000) % devices.Length);
        int height = Math.Min(bar.Height, (int)Math.Ceiling(29 * scale));
        var bounds = new Rectangle(bestEnd - bestCount * cell, bar.Top + (bar.Height-height)/2, bestCount*cell, height);
        if (dirty || !Visible || Bounds != bounds || first != start || count != bestCount)
        {
            first = start; count = bestCount;
            using var bitmap = Draw(devices, first, count, cell, height, scale, foreground);
            if (!PaintLayer(bitmap, bounds.Location)) { Availability(false); return; }
            if (!Visible) Show();
            SetWindowPos(Handle, (nint)(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010);
            dirty = false;
        }
        else if (GetAncestor(WindowFromPoint(new Point(bounds.Left + 3, bounds.Top + height/2)), 2) == taskbar)
            SetWindowPos(Handle,(nint)(-1),0,0,0,0,0x0013);
        Availability(true);
    }

    private async Task MeasureAsync(nint hwnd, Rectangle bar)
    {
        measuring = true;
        try
        {
            var result = await Task.Run(() => TaskbarGeometry.Read(hwnd, bar));
            if (IsDisposed) return;
            occupied = result;
            measureErrorReported = false;
            var ink = Equals(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme", 0), 1) ? Color.FromArgb(30,30,30) : Color.White;
            if (ink != foreground) { foreground = ink; dirty = true; }
            measuredBar = bar;
        }
        catch (Exception e) when (e is InvalidOperationException or COMException or UnauthorizedAccessException or ArgumentException)
        {
            occupied = [];
            measuredBar = bar; // Back off even if Explorer disappears during the first measurement.
            if (!measureErrorReported) ErrorLog.Write("任务栏定位",e);
            measureErrorReported = true;
        }
        finally { measuring = false; nextMeasure = Environment.TickCount64 + 3000; }
    }

    private static bool IsFullScreen(Rectangle screen, nint taskbar)
    {
        var window = GetForegroundWindow();
        if (window == 0 || window == taskbar || window == GetShellWindow()) return false;
        if (!GetWindowRect(window, out var rect) || !rect.RectValue.Contains(screen)) return false;
        var name = new System.Text.StringBuilder(80);
        GetClassName(window, name, name.Capacity);
        return name.ToString() is not ("WorkerW" or "Progman");
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
        var dc = CreateCompatibleDC(screen);
        var bmp = image.GetHbitmap(Color.FromArgb(0));
        var previous = SelectObject(dc,bmp);
        try
        {
            var src = Point.Empty; var size = image.Size;
            var blend = new Blend{Alpha = 255, Format = 1};
            return UpdateLayeredWindow(Handle, screen, ref where, ref size, dc, ref src, 0, ref blend, 2);
        }
        finally { SelectObject(dc,previous); DeleteObject(bmp); DeleteDC(dc); ReleaseDC(0,screen); }
    }

    protected override void Dispose(bool disposing) { if (disposing) tick.Dispose(); base.Dispose(disposing); }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int L,T,R,B; public Rectangle RectValue => Rectangle.FromLTRB(L,T,R,B); }
    [StructLayout(LayoutKind.Sequential, Pack=1)] private struct Blend { public byte Op,Flags,Alpha,Format; }
    [DllImport("user32",CharSet=CharSet.Unicode)] private static extern nint FindWindow(string cls,string? title);
    [DllImport("user32")] private static extern bool GetWindowRect(nint hwnd,out Rect r);
    [DllImport("user32")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32")] private static extern nint GetShellWindow();
    [DllImport("user32")] private static extern nint GetForegroundWindow();
    [DllImport("user32")] private static extern nint WindowFromPoint(Point point);
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
