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
                if (sheet.A(x, y) > 40) { rowSum[y]++; colSum[x]++; }
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
        BoxW = scaled.Max(f => f.W) + 2;
        BoxH = scaled.Max(f => f.H) + 2;
        for (var i = 0; i < 12; i++)
        {
            _right[i] = Bitmap(scaled[i], false);
            _left[i] = Bitmap(scaled[i], true);
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

    public void Dispose()
    {
        foreach (var b in _right.Concat(_left)) if (b != IntPtr.Zero) DeleteObject(b);
        Array.Clear(_right); Array.Clear(_left);
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
