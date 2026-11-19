using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace PeripheralPower;

// All geometry uses 96-DPI units. Ray offsets only affect the material below the UI.
internal static class GlassMaterial
{
    internal readonly record struct Lens(float X, float Y, float W, float H, float Radius)
    {
        public Vector2 Center => new(X + W / 2, Y + H / 2);
        public float Distance(Vector2 p)
        {
            Vector2 q = Vector2.Abs(p - Center) - new Vector2(W / 2 - Radius, H / 2 - Radius);
            return Vector2.Max(q, Vector2.Zero).Length() + Math.Min(Math.Max(q.X, q.Y), 0) - Radius;
        }
    }

    private static readonly Lens Header = new(12, 12, 300, 64, 26);
    private static readonly Lens Action = new(307, 23, 123, 42, 21);
    private static float Union(float a, float b)
    {
        float h = Math.Max(14 - Math.Abs(a - b), 0) / 14;
        return Math.Min(a, b) - h * h * 3.5f;
    }

    private static float Field(Lens lens, Vector2 p, bool joined) =>
        joined ? Union(lens.Distance(p), Action.Distance(p)) : lens.Distance(p);

    private static Vector2 Normal(Lens lens, Vector2 p, bool joined)
    {
        Vector2 n = new(Field(lens, p + Vector2.UnitX, joined) - Field(lens, p - Vector2.UnitX, joined),
                          Field(lens, p + Vector2.UnitY, joined) - Field(lens, p - Vector2.UnitY, joined));
        return n.LengthSquared() > .0001f ? Vector2.Normalize(n) : Vector2.Zero;
    }

    private sealed class Backdrop(int width, int height, bool dark)
    {
        private readonly Vector3[] pixels = Create(width, height, dark);
        private static Vector3[] Create(int w, int h, bool night)
        {
            var result = new Vector3[w * h];
            Vector3 blue = night ? new(35, 53, 97) : new(160, 193, 237);
            Vector3 jade = night ? new(15, 108, 123) : new(114, 211, 208);
            Vector3 iris = night ? new(87, 60, 130) : new(207, 174, 225);
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float u = (float)x / w, v = (float)y / h;
                float wave = v + .55f * u + .11f * MathF.Sin(6 * u + 2 * v);
                float t = .5f + .5f * MathF.Cos(6.7f * wave - .3f);
                var color = Vector3.Lerp(blue, jade, t);
                color = Vector3.Lerp(color, iris, .5f + .48f * MathF.Sin(5 * wave - 1.55f));
                // Wide silk ribbons and their finer caustic lines give the lens a visible source.
                float stripe = .5f + .5f * MathF.Cos(wave * 34);
                float sheen = .17f * MathF.Pow(stripe, 5) + .12f * MathF.Pow(stripe, 70);
                result[y * w + x] = Vector3.Lerp(color, new(213, 241, 251), sheen);
            }
            return result;
        }

        public Vector3 Sample(Vector2 p)
        {
            float x = Math.Clamp(p.X, 0, width - 1), y = Math.Clamp(p.Y, 0, height - 1);
            int i = (int)x, j = (int)y, k = Math.Min(i + 1, width - 1), l = Math.Min(j + 1, height - 1);
            return Vector3.Lerp(Vector3.Lerp(pixels[j * width + i], pixels[j * width + k], x - i),
                                Vector3.Lerp(pixels[l * width + i], pixels[l * width + k], x - i), y - j);
        }
    }

    public static Bitmap Render(int width, int height, int listHeight, int cardWidth, int count,
        float scroll, int settingsTop, float scale, bool dark)
    {
        var source = new Backdrop(width, height, dark);
        var footer = new Lens(14, settingsTop, 414, 88, 20);
        var outer = new Lens(0, 0, width, height, 23);
        var bytes = new int[width * height];
        var tint = dark ? new Vector3(21, 34, 56) : new Vector3(249, 253, 255);
        Parallel.For(0, height, y =>
        {
            var lens = outer;
            bool header = y < 82, inList = y >= 86 && y < 86 + listHeight;
            if (header) lens = Header;
            else if (inList)
            {
                float offset = y - 86 + scroll;
                float top = count == 0 ? 86 : 86 + MathF.Floor(offset / 78) * 78 - scroll;
                lens = new Lens(16, top, cardWidth, count == 0 ? listHeight : 70, 19);
            }
            else if (y >= settingsTop - 2) lens = footer;
            for (int x = 0; x < width; x++)
            {
                Vector2 p = new(x + .5f, y + .5f);
                float d = Field(lens, p, header);
                bool raised = lens != outer && d <= 1;
                var surface = raised ? lens : outer;
                if (!raised) d = surface.Distance(p);
                bool joined = raised && header;
                Vector2 n = Normal(surface, p, joined);
                float depth = Math.Max(0, -d);
                // Paraxial air/glass/air trace. The rear interface is sampled at the
                // displaced hit, with a different curvature from the front meniscus.
                float front = .62f * MathF.Exp(-depth / 3) - .27f * MathF.Exp(-MathF.Pow((depth - 5) / 3, 2));
                const float index = 1.46f;
                Vector2 inside = (1 - 1 / index) * front * n;
                Vector2 hit = p + 11 * inside;
                float backDepth = Math.Max(0, -Field(surface, hit, joined));
                Vector2 backSlope = -.42f * MathF.Exp(-backDepth / 4) * Normal(surface, hit, joined);
                Vector2 outgoing = index * inside + (1 - index) * backSlope;
                Vector2 center = joined && x > 310 ? Action.Center : surface.Center;
                Vector2 uv = hit + 12 * outgoing - .018f * (p - center) * Math.Clamp(depth / 12, 0, 1);
                Vector2 split = (1.5f / scale) * MathF.Exp(-depth / 4) * n;
                var ray = new Vector3(source.Sample(uv + split).X, source.Sample(uv).Y, source.Sample(uv - split).Z);
                var color = Vector3.Lerp(ray, tint, raised ? (dark ? .75f : .72f) : .21f);
                if (raised && d > 0) color = Vector3.Lerp(color, Vector3.Lerp(source.Sample(p), tint, .21f), Math.Min(d, 1));
                float light = Math.Clamp(-.48f * n.X - .88f * n.Y, 0, 1);
                float line = MathF.Exp(-MathF.Pow((depth - .9f) / .95f, 2));
                float shadow = MathF.Exp(-MathF.Pow((depth - 3.5f) / 2.3f, 2));
                color *= 1 - (.035f + .08f * (1 - light)) * shadow;
                color = Vector3.Lerp(color, new(248, 255, 255), line * (.14f + .57f * light));
                bytes[y * width + x] = unchecked((int)0xff000000) | (int)color.X << 16 | (int)color.Y << 8 | (int)color.Z;
            }
        });
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try { Marshal.Copy(bytes, 0, data.Scan0, bytes.Length); }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }
}
