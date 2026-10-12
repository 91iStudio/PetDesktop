using static PetDesktop.Native;

namespace PetDesktop;

/// <summary>A picture in memory: premultiplied BGRA, one uint per pixel (the layout layered windows want).</summary>
sealed class Pixels(int w, int h)
{
    public readonly int W = w, H = h;
    public readonly uint[] Data = new uint[w * h];
    public byte A(int x, int y) => (byte)(Data[y * W + x] >> 24);
}

/// <summary>
/// The pet's frames, made once and kept: a sheet of 12 poses (4×3) is cut by transparency, scaled to the chosen height,
/// stood on one line of paws and turned into ready bitmaps — both facing right and mirrored. Showing a frame later is a
/// plain copy, nothing is computed while the pet walks.
/// </summary>
sealed class Sprites : IDisposable
{
    public const int Stand = 0, Blink = 1, Walk1 = 2, Walk2 = 3, Sleep = 4, Happy = 5, Sad = 6, Held = 7, Eat = 8, Sit = 9, Play = 10, Wave = 11;

    public int BoxW { get; private set; }
    public int BoxH { get; private set; }
    readonly IntPtr[] _right = new IntPtr[12], _left = new IntPtr[12];
    /// <summary>The «held» pose turned around the scruff: angles from -12° to +12° in steps of 4°.</summary>
    public static readonly int[] SwingAngles = { -12, -8, -4, 0, 4, 8, 12 };
    readonly IntPtr[] _heldRight = new IntPtr[7], _heldLeft = new IntPtr[7];
    public Pixels? Face { get; private set; }

    static IntPtr _gdip;

    public static Pixels? LoadPng(string path)
    {
        if (_gdip == IntPtr.Zero) { var input = new GdiplusStartupInput { Version = 1 }; GdiplusStartup(out _gdip, ref input, IntPtr.Zero); }
        if (GdipCreateBitmapFromFile(path, out var bmp) != 0) return null;
        try
        {
            GdipGetImageWidth(bmp, out var w); GdipGetImageHeight(bmp, out var h);
            var rect = new GpRect { Width = (int)w, Height = (int)h };
            var data = new GpBitmapData();
            if (GdipBitmapLockBits(bmp, ref rect, 1, PixelFormat32bppPARGB, ref data) != 0) return null;
            var px = new Pixels((int)w, (int)h);
            unsafe
            {
                for (var y = 0; y < h; y++)
                {
                    var row = (uint*)((byte*)data.Scan0 + (long)y * data.Stride);
                    for (var x = 0; x < w; x++) px.Data[y * w + x] = row[x];
                }
            }
            GdipBitmapUnlockBits(bmp, ref data);
            return px;
        }
        finally { GdipDisposeImage(bmp); }
    }

    /// <summary>Cut a sheet into its 12 poses. The model does not keep the grid exact, so the cuts go along the emptiest lines near the thirds.</summary>
    public static List<Pixels> Cut(Pixels sheet, int cols, int rows)
    {
        var rowSum = new int[sheet.H]; var colSum = new int[sheet.W];
        for (var y = 0; y < sheet.H; y++)
            for (var x = 0; x < sheet.W; x += 2)
                // a sampled pixel counts for its neighbour column too: otherwise every odd column looks empty and the cut
                // lands on the bare grid line, slicing a paw or a tail of the next pose into this one
                if (sheet.A(x, y) > 40) { rowSum[y]++; colSum[x]++; if (x + 1 < sheet.W) colSum[x + 1]++; }
        int[] Cuts(int[] sum, int n, int size)
        {
            var o = new int[n + 1]; o[n] = size;
            for (var k = 1; k < n; k++)
            {
                double ideal = size * k / (double)n, span = size / (double)n * 0.3, best = double.MaxValue;
                for (var p = (int)(ideal - span); p <= (int)(ideal + span); p++)
                {
                    var cost = sum[p] + Math.Abs(p - ideal) * 0.02;
                    if (cost < best) { best = cost; o[k] = p; }
                }
            }
            return o;
        }
        var ys = Cuts(rowSum, rows, sheet.H); var xs = Cuts(colSum, cols, sheet.W);
        var list = new List<Pixels>();
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
            {
                int x0 = xs[c], y0 = ys[r], cw = xs[c + 1] - x0, ch = ys[r + 1] - y0;
                int minX = cw, minY = ch, maxX = -1, maxY = -1;
                for (var y = 0; y < ch; y++)
                    for (var x = 0; x < cw; x++)
                        if (sheet.A(x0 + x, y0 + y) > 40) { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
                if (maxX < 0) { minX = 0; minY = 0; maxX = cw - 1; maxY = ch - 1; }
                var f = new Pixels(maxX - minX + 1, maxY - minY + 1);
                for (var y = 0; y < f.H; y++)
                    Array.Copy(sheet.Data, (y0 + minY + y) * sheet.W + x0 + minX, f.Data, y * f.W, f.W);
                list.Add(f);
            }
        return list;
    }

    /// <summary>Area-average downscale of premultiplied pixels (smooth, done once).</summary>
    public static Pixels Scale(Pixels src, int w, int h)
    {
        var dst = new Pixels(Math.Max(1, w), Math.Max(1, h));
        double sx = (double)src.W / dst.W, sy = (double)src.H / dst.H;
        for (var y = 0; y < dst.H; y++)
        {
            int y0 = (int)(y * sy), y1 = Math.Max(y0 + 1, Math.Min(src.H, (int)Math.Ceiling((y + 1) * sy)));
            for (var x = 0; x < dst.W; x++)
            {
                int x0 = (int)(x * sx), x1 = Math.Max(x0 + 1, Math.Min(src.W, (int)Math.Ceiling((x + 1) * sx)));
                ulong a = 0, r = 0, g = 0, b = 0; var n = 0;
                for (var yy = y0; yy < y1; yy++)
                    for (var xx = x0; xx < x1; xx++)
                    {
                        var p = src.Data[yy * src.W + xx];
                        a += p >> 24; r += (p >> 16) & 255; g += (p >> 8) & 255; b += p & 255; n++;
                    }
                dst.Data[y * dst.W + x] = (uint)(a / (ulong)n) << 24 | (uint)(r / (ulong)n) << 16 | (uint)(g / (ulong)n) << 8 | (uint)(b / (ulong)n);
            }
        }
        return dst;
    }

    /// <summary>Frames for a pet of this height (physical pixels).</summary>
    public Sprites(List<Pixels> frames, int height)
    {
        var unit = (frames[Stand].H + frames[Sit].H + frames[Wave].H) / 3.0;
        var s = height / unit;
        var scaled = frames.Select(f => Scale(f, (int)Math.Round(f.W * s), (int)Math.Round(f.H * s))).ToList();
        // wider than the widest pose: room for the held pose to swing without being clipped
        BoxW = scaled.Max(f => f.W) + 2 + (int)(scaled.Max(f => f.H) * 0.45);
        BoxH = scaled.Max(f => f.H) + 2;
        for (var i = 0; i < 12; i++)
        {
            _right[i] = Bitmap(scaled[i], false);
            _left[i] = Bitmap(scaled[i], true);
        }
        for (var i = 0; i < SwingAngles.Length; i++)
        {
            _heldRight[i] = Swung(scaled[Held], SwingAngles[i], false);
            _heldLeft[i] = Swung(scaled[Held], SwingAngles[i], true);
        }
        Face = scaled[Stand];
    }

    /// <summary>A box-sized bitmap with the frame stood bottom-centre (so changing frames never moves the paws).</summary>
    IntPtr Bitmap(Pixels f, bool mirror)
    {
        var bmi = new BITMAPINFOHEADER { Size = 40, Width = BoxW, Height = -BoxH, Planes = 1, BitCount = 32 };
        var hbm = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        unsafe
        {
            var dst = (uint*)bits;
            new Span<uint>(dst, BoxW * BoxH).Clear();
            int ox = (BoxW - f.W) / 2, oy = BoxH - f.H - 1;
            for (var y = 0; y < f.H; y++)
                for (var x = 0; x < f.W; x++)
                    dst[(oy + y) * BoxW + ox + (mirror ? f.W - 1 - x : x)] = f.Data[y * f.W + x];
        }
        return hbm;
    }

    public IntPtr Get(int frame, int dir) => dir < 0 ? _left[frame] : _right[frame];
    public IntPtr GetHeld(int swing, int dir) => dir < 0 ? _heldLeft[swing] : _heldRight[swing];

    /// <summary>The frame stood in the box like the others, then turned around the scruff (top centre), bilinear.</summary>
    IntPtr Swung(Pixels f, int degrees, bool mirror)
    {
        var box = new Pixels(BoxW, BoxH);
        int ox = (BoxW - f.W) / 2, oy = BoxH - f.H - 1;
        for (var y = 0; y < f.H; y++)
            for (var x = 0; x < f.W; x++)
                box.Data[(oy + y) * BoxW + ox + (mirror ? f.W - 1 - x : x)] = f.Data[y * f.W + x];
        double px = BoxW / 2.0, py = oy + f.H * 0.06, a = -degrees * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        var bmi = new BITMAPINFOHEADER { Size = 40, Width = BoxW, Height = -BoxH, Planes = 1, BitCount = 32 };
        var hbm = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        unsafe
        {
            var dst = (uint*)bits;
            for (var y = 0; y < BoxH; y++)
                for (var x = 0; x < BoxW; x++)
                {
                    double sx = cos * (x - px) - sin * (y - py) + px, sy = sin * (x - px) + cos * (y - py) + py;
                    dst[y * BoxW + x] = Sample(box, sx, sy);
                }
        }
        return hbm;
    }

    static uint Sample(Pixels p, double x, double y)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        if (x0 < -1 || y0 < -1 || x0 >= p.W || y0 >= p.H) return 0;
        double fx = x - x0, fy = y - y0;
        uint At(int xx, int yy) => xx < 0 || yy < 0 || xx >= p.W || yy >= p.H ? 0 : p.Data[yy * p.W + xx];
        uint c00 = At(x0, y0), c10 = At(x0 + 1, y0), c01 = At(x0, y0 + 1), c11 = At(x0 + 1, y0 + 1);
        uint Mix(int shift)
        {
            double v = ((c00 >> shift) & 255) * (1 - fx) * (1 - fy) + ((c10 >> shift) & 255) * fx * (1 - fy)
                     + ((c01 >> shift) & 255) * (1 - fx) * fy + ((c11 >> shift) & 255) * fx * fy;
            return (uint)Math.Clamp(Math.Round(v), 0, 255);
        }
        return Mix(24) << 24 | Mix(16) << 16 | Mix(8) << 8 | Mix(0);
    }

    public void Dispose()
    {
        foreach (var b in _right.Concat(_left).Concat(_heldRight).Concat(_heldLeft)) if (b != IntPtr.Zero) DeleteObject(b);
        Array.Clear(_right); Array.Clear(_left); Array.Clear(_heldRight); Array.Clear(_heldLeft);
    }

    // ───── Small things drawn in code ─────

    /// <summary>A soft pink heart (for strokes), antialiased by supersampling.</summary>
    public static Pixels Heart(int size)
    {
        var p = new Pixels(size, size);
        const int ss = 4;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                int inside = 0, edge = 0;
                for (var sy = 0; sy < ss; sy++)
                    for (var sx = 0; sx < ss; sx++)
                    {
                        double u = ((x + (sx + 0.5) / ss) / size - 0.5) * 2.6, v = -((y + (sy + 0.5) / ss) / size - 0.45) * 2.6;
                        var f = Math.Pow(u * u + v * v - 1, 3) - u * u * v * v * v;
                        var f2 = Math.Pow(u * u * 1.18 + v * v * 1.18 - 1, 3) - u * u * 1.18 * v * v * v * 1.18;
                        if (f <= 0) { inside++; if (f2 > 0) edge++; }
                    }
                if (inside == 0) continue;
                var a = inside / (double)(ss * ss);
                var e = edge / (double)inside;
                // fill #F27AA0, outline #A84466
                double r = 0xF2 * (1 - e) + 0xA8 * e, g = 0x7A * (1 - e) + 0x44 * e, b = 0xA0 * (1 - e) + 0x66 * e;
                p.Data[y * size + x] = (uint)(a * 255) << 24 | (uint)(r * a) << 16 | (uint)(g * a) << 8 | (uint)(b * a);
            }
        return p;
    }

    /// <summary>
    /// The studio's little face (the same as the site's icon): ears, a round head with a dark-brown outline, a cream muzzle,
    /// big eyes with highlights, blush and a pink nose. Drawn in code, antialiased by supersampling.
    /// </summary>
    public static Pixels PetFace(int size)
    {
        var p = new Pixels(size, size);
        const int ss = 4;
        // shapes in a 64×64 space, painted back to front: (inside test, colour ARGB)
        static bool InEllipse(double x, double y, double cx, double cy, double rx, double ry) => (x - cx) * (x - cx) / (rx * rx) + (y - cy) * (y - cy) / (ry * ry) <= 1;
        static bool InTri(double x, double y, double ax, double ay, double bx, double by, double cx, double cy)
        {
            double d1 = (x - bx) * (ay - by) - (ax - bx) * (y - by), d2 = (x - cx) * (by - cy) - (bx - cx) * (y - cy), d3 = (x - ax) * (cy - ay) - (cx - ax) * (y - ay);
            return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
        }
        uint Pick(double x, double y)
        {
            const uint Outline = 0xFF6B4430, Fur = 0xFFF7C29C, Inner = 0xFFF4B7C8, Muzzle = 0xFFFDF0DA, Eye = 0xFF3B2A22, White = 0xFFFFFFFF, Blush = 0xCCF59FAE, Nose = 0xFFF07FA0;
            uint c = 0;
            // ears: outline triangle, then the inner pink
            if (InTri(x, y, 12, 32, 13.5, 6.5, 31, 20) || InTri(x, y, 52, 32, 50.5, 6.5, 33, 20)) c = Outline;
            if (InTri(x, y, 15, 29, 15.8, 10.5, 28, 20.5) || InTri(x, y, 49, 29, 48.2, 10.5, 36, 20.5)) c = Inner;
            if (InEllipse(x, y, 32, 37, 23.3, 20.3)) c = Outline;
            if (InEllipse(x, y, 32, 37, 20.7, 17.7)) c = Fur;
            if (InEllipse(x, y, 32, 45, 10, 7)) c = Muzzle;
            if (InEllipse(x, y, 16.5, 43, 3.6, 2.2) || InEllipse(x, y, 47.5, 43, 3.6, 2.2)) c = Blush;
            if (InEllipse(x, y, 23, 36, 4.2, 5.2) || InEllipse(x, y, 41, 36, 4.2, 5.2)) c = Eye;
            if (InEllipse(x, y, 24.3, 34, 1.7, 1.7) || InEllipse(x, y, 42.3, 34, 1.7, 1.7)) c = White;
            if (InEllipse(x, y, 32, 42, 2.4, 1.6)) c = Nose;
            return c;
        }
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                double a = 0, r = 0, g = 0, b = 0;
                for (var sy = 0; sy < ss; sy++)
                    for (var sx = 0; sx < ss; sx++)
                    {
                        var c = Pick((x + (sx + 0.5) / ss) * 64.0 / size, (y + (sy + 0.5) / ss) * 64.0 / size);
                        double ca = (c >> 24) / 255.0;
                        a += ca; r += ((c >> 16) & 255) * ca; g += ((c >> 8) & 255) * ca; b += (c & 255) * ca;
                    }
                const double n = ss * ss;
                p.Data[y * size + x] = (uint)(a / n * 255) << 24 | (uint)(r / n) << 16 | (uint)(g / n) << 8 | (uint)(b / n);
            }
        return p;
    }

    /// <summary>
    /// The egg, while the baby is inside: the baby's own colours and pattern (the same egg as on the site), a glossy
    /// highlight and the dark-brown outline; tilted around the bottom for the wobble. Drawn in code, supersampled.
    /// </summary>
    public static Pixels Egg(int size, uint baseColor, uint mark, string? pattern, bool rare, double tiltDeg)
    {
        int w = (int)(size * 0.8), h = size;
        var p = new Pixels(w, h);
        const int ss = 3;
        double a = tiltDeg * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        double cx = w / 2.0, cy = h * 0.56, ry = h * 0.43, rx = w * 0.4, outline = Math.Max(1.6, size / 34.0);
        // the egg: an ellipse a little wider at the bottom
        bool Inside(double x, double y, double grow)
        {
            var v = (y - cy) / (ry + grow);
            var k = rx * (1 + 0.14 * v) + grow;
            return (x - cx) * (x - cx) / (k * k) + v * v <= 1;
        }
        static uint Rgb(uint c) => c & 0xFFFFFF;
        uint lighter = Mix(Rgb(baseColor), 0xFFFFFF, 0.25), darker = Mix(Rgb(baseColor), 0x000000, 0.12);
        var rng = new Random((int)(Rgb(baseColor) ^ Rgb(mark) ^ (uint)(pattern?.Length ?? 0) * 7919u)); // the same egg every time
        var spots = Enumerable.Range(0, 9).Select(_ => (x: cx + (rng.NextDouble() - 0.5) * rx * 1.7, y: cy + (rng.NextDouble() - 0.6) * ry * 1.6, r: size * (0.04 + rng.NextDouble() * 0.04))).ToArray();
        uint Color(double x, double y)
        {
            // turn the point back around the bottom of the egg (the wobble)
            double px = x - cx, py = y - (cy + ry);
            double ux = cos * px + sin * py + cx, uy = -sin * px + cos * py + cy + ry;
            if (!Inside(ux, uy, outline)) return 0;
            if (!Inside(ux, uy, 0)) return 0xFF6B4430;
            var shade = (uy - (cy - ry)) / (2 * ry);
            var c = Mix(lighter, darker, Math.Clamp(shade * 1.1 - (ux - cx) / rx * 0.15, 0, 1));
            var onMark = pattern switch
            {
                "spots" => spots.Any(s => (ux - s.x) * (ux - s.x) + (uy - s.y) * (uy - s.y) < s.r * s.r),
                "stripes" => Math.Abs(Math.Sin((uy - (ux - cx) * 0.25) / (size * 0.085))) > 0.72,
                "socks" => uy > cy + ry * 0.55,
                "patches" => spots.Take(3).Any(s => (ux - s.x) * (ux - s.x) / 2.2 + (uy - s.y) * (uy - s.y) / 1.4 < s.r * s.r * 3),
                "stars" => spots.Any(s => Math.Abs(ux - s.x) + Math.Abs(uy - s.y) < s.r * 0.9),
                "heart" => (ux - cx) * (ux - cx) + (uy - cy - ry * 0.15) * (uy - cy - ry * 0.15) < size * size * 0.012,
                _ => false,
            };
            if (onMark) c = Rgb(mark);
            if (rare && spots.Skip(4).Any(s => Math.Abs(ux - s.x) + Math.Abs(uy - s.y) < size * 0.018)) c = 0xFFFFFF;
            // glossy highlight, upper left
            var hx = (ux - (cx - rx * 0.38)) / (rx * 0.2); var hy = (uy - (cy - ry * 0.45)) / (ry * 0.16);
            if (hx * hx + hy * hy < 1) c = Mix(c, 0xFFFFFF, 0.45);
            return 0xFF000000 | c;
        }
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                double A = 0, R = 0, G = 0, B = 0;
                for (var sy = 0; sy < ss; sy++)
                    for (var sx = 0; sx < ss; sx++)
                    {
                        var c = Color(x + (sx + 0.5) / ss, y + (sy + 0.5) / ss);
                        if (c == 0) continue;
                        A++; R += (c >> 16) & 255; G += (c >> 8) & 255; B += c & 255;
                    }
                if (A == 0) continue;
                var n = (double)(ss * ss);
                p.Data[y * w + x] = (uint)(A / n * 255) << 24 | (uint)(R / n) << 16 | (uint)(G / n) << 8 | (uint)(B / n);
            }
        return p;
    }

    static uint Mix(uint a, uint b, double t)
    {
        uint Ch(int s) => (uint)Math.Round(((a >> s) & 255) * (1 - t) + ((b >> s) & 255) * t);
        return Ch(16) << 16 | Ch(8) << 8 | Ch(0);
    }

    /// <summary>"#RRGGBB" → 0xFFRRGGBB.</summary>
    public static uint Hex(string? s, uint fallback) =>
        s is { Length: 7 } && s[0] == '#' && uint.TryParse(s.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var v) ? 0xFF000000 | v : fallback;

    /// <summary>A tray icon from a picture (the pet's face, or the heart before there is a pet).</summary>
    public static IntPtr Icon(Pixels src, int size)
    {
        var side = Math.Min(src.W, (int)(src.H * 0.8));
        var crop = new Pixels(side, side);
        var ox = (src.W - side) / 2;
        for (var y = 0; y < side; y++) Array.Copy(src.Data, y * src.W + ox, crop.Data, y * side, side);
        var px = Scale(crop, size, size);
        var bmi = new BITMAPINFOHEADER { Size = 40, Width = size, Height = -size, Planes = 1, BitCount = 32 };
        var color = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        unsafe { var d = (uint*)bits; for (var i = 0; i < px.Data.Length; i++) d[i] = px.Data[i]; }
        var mask = CreateBitmap(size, size, 1, 1, IntPtr.Zero);
        var info = new ICONINFO { Icon = 1, Mask = mask, Color = color };
        var icon = CreateIconIndirect(ref info);
        DeleteObject(color); DeleteObject(mask);
        return icon;
    }

    public static IntPtr Dib(Pixels p)
    {
        var bmi = new BITMAPINFOHEADER { Size = 40, Width = p.W, Height = -p.H, Planes = 1, BitCount = 32 };
        var hbm = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        unsafe { var d = (uint*)bits; for (var i = 0; i < p.Data.Length; i++) d[i] = p.Data[i]; }
        return hbm;
    }
}
