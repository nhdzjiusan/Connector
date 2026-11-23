using System.Runtime.InteropServices;

namespace PeripheralPower;

internal static class TaskbarGeometry
{
    // UIAutomationCore returns a row per element. Column zero is reserved for a node
    // handle; cached-only mode asks for no live element references.
    private const int BoundsProperty = 30001, ControlTypeProperty = 30003;
    private const int ClassProperty = 30012, ControlElementProperty = 30016, OffscreenProperty = 30022, ButtonType = 50000;

    internal static unsafe Rectangle[] Read(nint hwnd, Rectangle bar)
    {
        Marshal.ThrowExceptionForHR(UiaNodeFromHandle(hwnd, out var node));
        try
        {
            int trueCondition = 0;
            int* properties = stackalloc int[] { ControlTypeProperty, ClassProperty, OffscreenProperty, BoundsProperty, ControlElementProperty };
            var request = new CacheRequest
            {
                Condition = (nint)(&trueCondition),
                Scope = 7, // TreeScope_Subtree
                Properties = (nint)properties,
                PropertyCount = 5
            };
            Marshal.ThrowExceptionForHR(UiaGetUpdatedCache(node, ref request, 0, 0, out var rows, out _));
            return OccupiedBounds(rows, bar);
        }
        finally { UiaNodeRelease(node); }
    }

    internal static Rectangle[] OccupiedBounds(object[,]? rows, Rectangle bar)
    {
        if (rows is null || rows.GetLength(1) < 6 || rows.GetLowerBound(0) != 0 || rows.GetLowerBound(1) != 0) return [];
        var spans = new List<Rectangle>();
        for (int i = 1; i < rows.GetLength(0); i++)
        {
            if (rows[i, 1] is not ButtonType && rows[i, 2] is not ("TrayNotifyWnd" or "MSTaskSwWClass"))
                continue;
            // Some shell providers omit IsOffscreen. UIA's default for that property is false.
            if (rows[i, 3] is true || rows[i, 5] is false || rows[i, 4] is not double[] { Length: 4 } box ||
                box[2] <= 0 || box[3] <= 0) continue;
            double right = box[0] + box[2], bottom = box[1] + box[3];
            if (!double.IsFinite(box[0]) || !double.IsFinite(box[1]) || !double.IsFinite(right) || !double.IsFinite(bottom))
                continue;
            double left = Math.Max(box[0], bar.Left), top = Math.Max(box[1], bar.Top);
            right = Math.Min(right, bar.Right);
            bottom = Math.Min(bottom, bar.Bottom);
            if (left < right && top < bottom)
                spans.Add(Rectangle.FromLTRB((int)Math.Floor(left), (int)Math.Floor(top),
                    (int)Math.Ceiling(right), (int)Math.Ceiling(bottom)));
        }
        return spans.ToArray();
    }

    internal static bool IsAnchoredBar(Rectangle bar, Rectangle screen)
    {
        if (bar.Width <= bar.Height || bar.Height <= 0 || bar.Width < screen.Width * .9) return false;
        var visible = Rectangle.Intersect(bar, screen);
        return visible.Width >= bar.Width * .95 && visible.Height >= bar.Height * .95 &&
            (Math.Abs(bar.Top - screen.Top) <= 2 || Math.Abs(bar.Bottom - screen.Bottom) <= 2);
    }

    internal static bool CoversScreen(Rectangle window, Rectangle screen, int tolerance) =>
        window.Width > 0 && window.Height > 0 &&
        window.Left <= screen.Left + tolerance && window.Top <= screen.Top + tolerance &&
        window.Right >= screen.Right - tolerance && window.Bottom >= screen.Bottom - tolerance;

    internal static (int End, int Count) FindGap(Rectangle bar, IEnumerable<Rectangle> occupied,
        int cell, int margin, int devices)
    {
        int end = bar.Right - margin, bestEnd = 0, bestCount = 0;
        // Prefer the rightmost gap when it fits as many devices.
        foreach (var box in occupied.Where(bar.IntersectsWith).Select(box => Rectangle.Intersect(box, bar))
            .OrderByDescending(box => box.Right).Append(new Rectangle(bar.Left, bar.Top, 0, bar.Height)))
        {
            int fits = Math.Min(devices, (end - box.Right - margin) / cell);
            if (fits > bestCount) { bestCount = fits; bestEnd = end; }
            end = Math.Min(end, box.Left - margin);
            if (bestCount == devices) break;
        }
        return (bestEnd, bestCount);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CacheRequest
    {
        public nint Condition;
        public int Scope;
        public nint Properties;
        public int PropertyCount;
        public nint Patterns;
        public int PatternCount, ElementMode;
    }

    [DllImport("UIAutomationCore")]
    private static extern int UiaNodeFromHandle(nint hwnd, out nint node);
    [DllImport("UIAutomationCore")]
    private static extern bool UiaNodeRelease(nint node);
    [DllImport("UIAutomationCore")]
    private static extern int UiaGetUpdatedCache(nint node, ref CacheRequest request, int normalize, nint condition,
        [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT)] out object[,] rows,
        [MarshalAs(UnmanagedType.BStr)] out string tree);
}
