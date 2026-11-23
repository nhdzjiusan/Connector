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

    internal static Rectangle[] OccupiedBounds(object[,] rows, Rectangle bar)
    {
        var spans = new List<Rectangle>();
        for (int i = 1; i < rows.GetLength(0); i++)
        {
            if (rows[i, 1] is not ButtonType && rows[i, 2] is not ("TrayNotifyWnd" or "MSTaskSwWClass"))
                continue;
            // Some shell providers omit IsOffscreen. UIA's default for that property is false.
            if (rows[i, 3] is true || rows[i, 5] is false || rows[i, 4] is not double[] { Length: 4 } box ||
                box[2] <= 0 || box[3] <= 0) continue;
            var rect = Rectangle.FromLTRB((int)Math.Floor(box[0]), (int)Math.Floor(box[1]),
                (int)Math.Ceiling(box[0] + box[2]), (int)Math.Ceiling(box[1] + box[3]));
            if (rect.IntersectsWith(bar)) spans.Add(rect);
        }
        return spans.ToArray();
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
