using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace PeripheralPower;

internal static class TrayArt
{
    public static readonly Color Ink = Color.FromArgb(28, 39, 58);
    public static readonly Color Muted = Color.FromArgb(87, 105, 128);
    public static readonly Color Accent = Color.FromArgb(54, 81, 204);
    public static readonly Color Border = Color.FromArgb(223, 230, 240);
    public static readonly Color Surface = Color.FromArgb(245, 248, 252);

    public static (Color Foreground, Color Background) BatteryColors(int percent, bool charging) =>
        charging ? (Color.FromArgb(40, 81, 193), Color.FromArgb(231, 239, 255)) :
        percent <= 20 ? (Color.FromArgb(177, 51, 63), Color.FromArgb(255, 234, 235)) :
        percent <= 40 ? (Color.FromArgb(141, 82, 16), Color.FromArgb(255, 239, 214)) :
        (Color.FromArgb(0, 111, 91), Color.FromArgb(227, 249, 240));

    public static Icon MakeReading(BatteryReading reading) => MakeIcon(g =>
    {
        // White figure and a solid background stay readable on light and dark taskbars.
        var hue = reading.Kind switch
        {
            DeviceKind.Mouse => Color.FromArgb(20, 105, 93),
            DeviceKind.Keyboard => Color.FromArgb(67, 76, 171),
            _ => Color.FromArgb(108, 63, 151)
        };
        var onPower = reading.Charging || reading.IsReference;
        if (reading.Percent <= 20 && !onPower) hue = Color.FromArgb(168, 44, 59);
        Fill(g, new RectangleF(0.8f, 0.8f, 30.4f, 30.4f), 7, hue);
        var percent = reading.Percent.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var font = new Font("Segoe UI", percent.Length == 3 ? 16f : 20f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Color.White);
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(percent, font, ink, new RectangleF(0, 7, 32, 24), center);
        using var line = new Pen(onPower ? Color.FromArgb(149, 224, 255) : Color.FromArgb(202, 219, 255), 2.2f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(line, 10.5f, 6.5f, 21.5f, 6.5f);
    });

    public static Icon MakeLauncher() => MakeIcon(g =>
    {
        Fill(g, new RectangleF(1, 1, 30, 30), 7, Accent);
        using var pen = new Pen(Color.White, 1.9f) { LineJoin = LineJoin.Round };
        using var box = Rounded(new RectangleF(6.5f, 10, 17.5f, 12), 2.4f);
        g.DrawPath(pen, box);
        g.DrawLine(pen, 26, 14, 26, 18);
        using var fill = new SolidBrush(Color.FromArgb(173, 234, 255));
        g.FillRectangle(fill, 10, 13, 9, 6);
    });

    private static Icon MakeIcon(Action<Graphics> paint)
    {
        using var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            paint(g);
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public static GraphicsPath Rounded(RectangleF box, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(box.Width, box.Height));
        if (diameter <= 0) { path.AddRectangle(box); return path; }
        path.AddArc(box.Left, box.Top, diameter, diameter, 180, 90);
        path.AddArc(box.Right - diameter, box.Top, diameter, diameter, 270, 90);
        path.AddArc(box.Right - diameter, box.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(box.Left, box.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void Fill(Graphics g, RectangleF box, float radius, Color color)
    {
        if (box.Width <= 0 || box.Height <= 0) return;
        using var shape = Rounded(box, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, shape);
    }

    public static void Outline(Graphics g, RectangleF box, float radius, Color color)
    {
        using var shape = Rounded(box, radius);
        using var pen = new Pen(color, 1);
        g.DrawPath(pen, shape);
    }

    public static Color DeviceColor(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => Color.FromArgb(13, 105, 92),
        DeviceKind.Keyboard => Color.FromArgb(62, 76, 184),
        _ => Color.FromArgb(121, 67, 153)
    };

    public static Color DeviceBackground(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => Color.FromArgb(230, 247, 243),
        DeviceKind.Keyboard => Color.FromArgb(235, 239, 255),
        _ => Color.FromArgb(247, 237, 255)
    };

    public static string DeviceLabel(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => "鼠标",
        DeviceKind.Keyboard => "键盘",
        _ => "耳机"
    };

    public static void DeviceGlyph(Graphics g, DeviceKind kind, float x, float y)
    {
        var state = g.Save();
        g.TranslateTransform(x, y);
        using var pen = new Pen(DeviceColor(kind), 1.8f)
        {
            StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
        };
        switch (kind)
        {
            case DeviceKind.Mouse:
                using (var body = Rounded(new RectangleF(6, 2, 17, 25), 8))
                    g.DrawPath(pen, body);
                g.DrawLine(pen, 14.5f, 6, 14.5f, 11.5f);
                break;
            case DeviceKind.Keyboard:
                using (var body = Rounded(new RectangleF(1, 5.5f, 27, 18), 4))
                    g.DrawPath(pen, body);
                for (int col = 0; col < 5; col++)
                {
                    g.DrawLine(pen, 6 + col * 4, 11, 6.4f + col * 4, 11);
                    g.DrawLine(pen, 6 + col * 4, 14.7f, 6.4f + col * 4, 14.7f);
                }
                g.DrawLine(pen, 10, 18.5f, 19, 18.5f);
                break;
            case DeviceKind.Headset:
                g.DrawArc(pen, new RectangleF(3, 2.5f, 23, 23), 180, 180);
                g.DrawLine(pen, 3, 14, 3, 22);
                g.DrawLine(pen, 26, 14, 26, 22);
                using (var left = Rounded(new RectangleF(3, 14, 6, 11), 2.2f))
                using (var right = Rounded(new RectangleF(20, 14, 6, 11), 2.2f))
                {
                    g.DrawPath(pen, left);
                    g.DrawPath(pen, right);
                }
                break;
        }
        g.Restore(state);
    }
}
