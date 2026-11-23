namespace Zeus.Core;

/// <summary>One frame of the game window's client area (BGRA, top row first). Kept in memory only.</summary>
public sealed record ScreenFrame(int Width, int Height, byte[] Pixels);

/// <summary>
/// Where the world sits on the screen: world tile (tx, ty) covers the screen rectangle starting at
/// (OriginX + tx × Scale, OriginY + ty × Scale). Found from the image alone, so it is exact whatever
/// the zoom or the camera is doing.
/// </summary>
public sealed record ScreenFit(double Scale, double OriginX, double OriginY, double Score, double Margin, int Edges)
{
    public (int X, int Y) Tile(double screenX, double screenY) =>
        ((int)Math.Floor((screenX - OriginX) / Scale), (int)Math.Floor((screenY - OriginY) / Scale));
}

/// <summary>
/// Turns a click in the game window into a world tile from the window's image.
/// 1. The tile size: the world is drawn from tiles, so the image repeats itself every tile; the
///    autocorrelation of the image peaks at every multiple of the tile size.
/// 2. The position: wherever two different blocks touch in the world map, the image has an edge.
///    The alignment whose predicted edges fall on real edges is the one on screen.
/// 3. Sub-pixel: the measured edge positions are fitted to a line (origin and tile size).
/// </summary>
public static class ScreenLocator
{
    private const double MinScale = 4, MaxScale = 220;
    internal static bool Debug;

    /// <summary>Key of what is drawn in a tile; 0 = nothing (the backdrop shows through).</summary>
    public static int[] Keys(WorldMap map)
    {
        var keys = new int[map.Width * map.Height];
        for (int i = 0; i < keys.Length; i++)
        {
            var t = map.Tiles[i];
            keys[i] = t.Foreground == 0 && t.Background == 0 ? 0 : (t.Foreground << 16 | t.Background) + 1;
        }
        return keys;
    }

    /// <summary>
    /// Finds the world on the screen. <paramref name="playerX"/>/<paramref name="playerY"/> (tiles) only
    /// limit the search to alignments that keep the player on screen. Null when the image does not
    /// decide it clearly; <paramref name="note"/> says why (diagnostic log).
    /// </summary>
    public static ScreenFit? Locate(ScreenFrame frame, int[] keys, int mapWidth, int mapHeight, double playerX, double playerY, out string note)
    {
        note = "";
        if (frame.Width < 64 || frame.Height < 64 || frame.Pixels.Length < frame.Width * frame.Height * 4) { note = "görüntü yok"; return null; }
        var image = new Gradients(frame);
        bool anyEdge = false;
        for (int i = 0; i < image.Gx.Length; i++)
            if (image.Gx[i] > 0 || image.Gy[i] > 0) { anyEdge = true; break; }
        if (!anyEdge) { note = "düz/siyah görüntü"; return null; }
        var scales = Scales(image, out string scaleNote);
        if (scales.Count == 0) { note = "karo boyutu ölçülemedi" + scaleNote; return null; }
        ScreenFit? best = null; double runnerUp = 0;
        ScreenFit? ambiguousBest = null; double ambiguousSecond = 0;
        foreach (double s in scales)
        {
            var fit = Align(image, keys, mapWidth, mapHeight, s, playerX, playerY, out double second);
            if (fit == null) continue;
            if (fit.Score <= 0 || (fit.Score - second) < fit.Score * 0.10)
            {
                if (ambiguousBest == null || fit.Score > ambiguousBest.Score) { ambiguousBest = fit; ambiguousSecond = second; }
                continue;
            }
            if (best == null || fit.Score > best.Score) { runnerUp = Math.Max(best?.Score ?? 0, second); best = fit; }
            else runnerUp = Math.Max(runnerUp, fit.Score);
        }
        if (best == null && ambiguousBest != null) { best = ambiguousBest; runnerUp = ambiguousSecond; }
        if (best == null) { note = $"hizalama bulunamadı (ölçek adayları {string.Join("/", scales.Select(s => s.ToString("0.00")))})"; return null; }
        best = SnapY(image, keys, mapWidth, mapHeight, best);
        double margin = best.Score > 0 ? (best.Score - runnerUp) / best.Score : 0;
        best = best with { Margin = margin };
        note = $"ölçek {best.Scale:0.000}, köken {best.OriginX:0.0},{best.OriginY:0.0}, skor {best.Score:0.0}, fark %{margin * 100:0}, kenar {best.Edges}";
        if (best.Edges < 12) { note += " (yetersiz kenar)"; return null; }
        if (best.Score < 20) { note += " (zayıf skor)"; return null; }
        if (margin < 0.12) { note += " (belirsiz)"; return null; }
        return best;
    }

    /// <summary>Tile size candidates of a frame (diagnostics and tests).</summary>
    public static List<double> MeasureScale(ScreenFrame frame, out string note) => Scales(new Gradients(frame), out note);

    // ---------------------------------------------------------------- image

    private sealed class Gradients
    {
        public readonly int W, H;
        public readonly byte[] Pixels;
        public readonly float[] Gx, Gy, Luma;
        public Gradients(ScreenFrame f)
        {
            W = f.Width; H = f.Height;
            Pixels = f.Pixels;
            Gx = new float[W * H]; Gy = new float[W * H]; Luma = new float[W * H];
            var p = f.Pixels;
            for (int y = 0; y < H; y++)
            {
                int row = y * W;
                for (int x = 0; x < W; x++)
                {
                    int i = (row + x) * 4;
                    Luma[row + x] = p[i] + p[i + 1] + p[i + 2];
                    if (x > 0)
                    {
                        int j = i - 4;
                        Gx[row + x] = Math.Abs(p[i] - p[j]) + Math.Abs(p[i + 1] - p[j + 1]) + Math.Abs(p[i + 2] - p[j + 2]);
                    }
                    if (y > 0)
                    {
                        int j = i - W * 4;
                        Gy[row + x] = Math.Abs(p[i] - p[j]) + Math.Abs(p[i + 1] - p[j + 1]) + Math.Abs(p[i + 2] - p[j + 2]);
                    }
                }
            }
        }
    }

    // ---------------------------------------------------------------- 1. tile size

    /// <summary>Tile size candidates, best first (the true size and, when unclear, its half/double).</summary>
    private static List<double> Scales(Gradients g, out string note)
    {
        note = "";
        double[] acx = Autocorrelation(g, horizontal: true), acy = Autocorrelation(g, horizontal: false);
        double Peak(double[] ac, double lag)
        {
            if (lag < 2 || lag + 2 >= ac.Length) return double.NaN;
            return At(ac, lag) - 0.5 * (At(ac, lag - 1.5) + At(ac, lag + 1.5));
        }
        double Mean(double[] ac, double s)
        {
            double first = Peak(ac, s);
            if (double.IsNaN(first) || first <= 0) return double.NaN;
            double sum = 0; int n = 0;
            for (double lag = s; lag + 2 < ac.Length; lag += s) { double v = Peak(ac, lag); if (!double.IsNaN(v)) { sum += v; n++; } }
            // Geometric balance between the fundamental peak and harmonic mean, weighted by log count.
            return n >= 2 && sum > 0 ? Math.Sqrt(first * (sum / n)) * Math.Sqrt(Math.Log(n + 1)) : double.NaN;
        }
        var all = new List<(double S, double V)>();
        double top = MaxScale;
        for (double s = MinScale; s <= top; s *= 1.0015)
        {
            double vx = Mean(acx, s), vy = Mean(acy, s);
            double v = double.IsNaN(vx) ? vy : double.IsNaN(vy) ? vx : vx + vy;
            if (!double.IsNaN(v)) all.Add((s, v));
        }
        if (all.Count == 0) return [];
        double max = all.Max(a => a.V);
        if (max <= 0) { note = " (tekrar eden desen yok)"; return []; }
        // Every multiple of the tile size scores about as well as the tile size itself; the smallest
        // strong one is the tile. Local maxima only.
        var peaks = new List<(double S, double V)>();
        for (int i = 1; i + 1 < all.Count; i++)
            if (all[i].V >= all[i - 1].V && all[i].V >= all[i + 1].V && all[i].V >= max * 0.25) peaks.Add(all[i]);
        if (Debug) foreach (var p in peaks.OrderByDescending(p => p.V).Take(25)) Console.WriteLine($"    tepe {p.S:0.000} {p.V / max:0.00}");
        if (peaks.Count == 0) return [];
        // Several strong sizes (a tile, its multiples, a texture's own half-tile pattern): the map
        // alignment decides.
        double Both(double s) { double vx = Mean(acx, s), vy = Mean(acy, s); return (double.IsNaN(vx) ? 0 : vx) + (double.IsNaN(vy) ? 0 : vy); }
        var result = new List<double>();
        foreach (var p in peaks.OrderByDescending(p => p.V))
        {
            if (result.Any(r => Math.Abs(p.S / r - 1) < 0.03)) continue;
            result.Add(Sharpen(p.S, Both));
            if (result.Count == 5) break;
        }
        return result;
    }

    private static double Sharpen(double s, Func<double, double> score)
    {
        double best = s, bestV = score(s);
        for (double f = 0.994; f <= 1.006; f += 0.0002)
        {
            double v = score(s * f);
            if (v > bestV) { bestV = v; best = s * f; }
        }
        return best;
    }

    private static double At(double[] a, double x)
    {
        int i = (int)Math.Floor(x); double f = x - i;
        if (i < 0) return a[0];
        if (i + 1 >= a.Length) return a[^1];
        return a[i] * (1 - f) + a[i + 1] * f;
    }

    /// <summary>Normalised autocorrelation of the high-passed image along one axis (lags 0..n/2).</summary>
    private static double[] Autocorrelation(Gradients g, bool horizontal)
    {
        int length = horizontal ? g.W : g.H, lines = horizontal ? g.H : g.W;
        int n = 1; while (n < length * 2) n <<= 1;
        var power = new double[n];
        var re = new double[n]; var im = new double[n];
        int step = lines > 600 ? 2 : 1;
        // Two real lines per complex transform.
        for (int a = 1; a + step < lines - 1; a += 2 * step)
        {
            int b = a + step;
            Array.Clear(re); Array.Clear(im);
            for (int k = 1; k < length - 1; k++)
            {
                re[k] = HighPass(g, horizontal, a, k);
                im[k] = HighPass(g, horizontal, b, k);
            }
            Fft(re, im, false);
            for (int k = 0; k < n; k++)
            {
                int m = (n - k) & (n - 1);
                // Spectra of the two real lines from the combined transform.
                double ar = (re[k] + re[m]) / 2, ai = (im[k] - im[m]) / 2;
                double br = (im[k] + im[m]) / 2, bi = -(re[k] - re[m]) / 2;
                power[k] += ar * ar + ai * ai + br * br + bi * bi;
            }
        }
        Array.Clear(im);
        Fft(power, im, true);
        var ac = new double[length / 2];
        double zero = power[0] > 0 ? power[0] : 1;
        for (int k = 0; k < ac.Length; k++) ac[k] = power[k] / zero;
        return ac;
    }

    private static double HighPass(Gradients g, bool horizontal, int line, int k)
    {
        int x = horizontal ? k : line, y = horizontal ? line : k, w = g.W;
        if (x < 1 || y < 1 || x >= g.W - 1 || y >= g.H - 1) return 0;
        int i = y * w + x;
        return g.Luma[i] - (g.Luma[i - 1] + g.Luma[i + 1] + g.Luma[i - w] + g.Luma[i + w]) / 4;
    }

    private static void Fft(double[] re, double[] im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = 2 * Math.PI / len * (inverse ? 1 : -1);
            double wr = Math.Cos(angle), wi = Math.Sin(angle);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int j = 0; j < len / 2; j++)
                {
                    int u = i + j, v = i + j + len / 2;
                    double tr = re[v] * cr - im[v] * ci, ti = re[v] * ci + im[v] * cr;
                    re[v] = re[u] - tr; im[v] = im[u] - ti;
                    re[u] += tr; im[u] += ti;
                    double next = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = next;
                }
            }
        }
    }

    // ---------------------------------------------------------------- 2. position

    /// <summary>Strongest edge across a vertical boundary at screen x (±1 px), at height y.</summary>
    private static float EdgeX(Gradients g, double x, int y, bool sharp = false)
    {
        int b = (int)Math.Round(x);
        if (b < 2 || b >= g.W - 2 || y < 0 || y >= g.H) return -1;
        int row = y * g.W;
        return sharp ? g.Gx[row + b] : Math.Max(g.Gx[row + b], Math.Max(g.Gx[row + b - 1], g.Gx[row + b + 1]));
    }

    private static float EdgeY(Gradients g, int x, double y, bool sharp = false)
    {
        int b = (int)Math.Round(y);
        if (b < 2 || b >= g.H - 2 || x < 0 || x >= g.W) return -1;
        return sharp ? g.Gy[b * g.W + x] : Math.Max(g.Gy[b * g.W + x], Math.Max(g.Gy[(b - 1) * g.W + x], g.Gy[(b + 1) * g.W + x]));
    }

    private static int Key(int[] keys, int w, int h, int x, int y) => x < 0 || y < 0 || x >= w || y >= h ? 0 : keys[y * w + x];

    private static float CenterDiff(Gradients g, double x1, double y1, double x2, double y2)
    {
        int ix1 = (int)x1, iy1 = (int)y1, ix2 = (int)x2, iy2 = (int)y2;
        if (ix1 < 0 || iy1 < 0 || ix1 >= g.W || iy1 >= g.H || ix2 < 0 || iy2 < 0 || ix2 >= g.W || iy2 >= g.H) return -1;
        int i1 = (iy1 * g.W + ix1) * 4, i2 = (iy2 * g.W + ix2) * 4;
        var p = g.Pixels;
        return Math.Min(200f, Math.Abs(p[i1] - p[i2]) + Math.Abs(p[i1 + 1] - p[i2 + 1]) + Math.Abs(p[i1 + 2] - p[i2 + 2]));
    }

    /// <summary>
    /// How well an alignment explains the image: mean edge strength where the map says two different
    /// things touch, minus the mean where it says they are the same. <paramref name="stride"/> thins
    /// the samples for the wide search.
    /// </summary>
    private static double Score(Gradients g, int[] keys, int mw, int mh, double s, double ox, double oy, int stride, out int edges, bool sharp = false)
    {
        double diff = 0, same = 0; int nd = 0, ns = 0;
        double near = Math.Min(0.45 * s, Math.Max(1.5, 0.12 * s));
        int t0 = Math.Max(0, (int)Math.Ceiling((2 - ox) / s)), t1 = Math.Min(mw, (int)Math.Floor((g.W - 3 - ox) / s));
        int r0 = Math.Max(-1, (int)Math.Floor(-oy / s)), r1 = Math.Min(mh, (int)Math.Ceiling((g.H - oy) / s));
        // Vertical boundaries: between column t-1 and t.
        for (int t = t0; t <= t1; t += stride)
        {
            double x = ox + t * s;
            for (int r = r0; r <= r1; r += stride)
            {
                int a = Key(keys, mw, mh, t - 1, r), b = Key(keys, mw, mh, t, r);
                float e = 0; int cnt = 0;
                if (s >= 12)
                {
                    for (int k = 1; k <= 3; k++)
                    {
                        float ev = EdgeX(g, x, (int)(oy + (r + k * 0.25) * s), sharp);
                        if (ev >= 0) { e += Math.Min(ev, 200f); cnt++; }
                    }
                    if (cnt == 0) continue;
                    e /= cnt;
                }
                else
                {
                    e = EdgeX(g, x, (int)(oy + (r + 0.5) * s), sharp);
                    if (e < 0) continue;
                    e = Math.Min(e, 200f);
                }
                if (t - 1 >= 0 && t < mw && r >= 0 && r < mh)
                {
                    double cy = oy + (r + 0.5) * s;
                    float cd = CenterDiff(g, x - 0.5 * s, cy, x + 0.5 * s, cy);
                    if (cd >= 0) e += 0.4f * cd;
                    float ndiff = CenterDiff(g, x - near, cy, x + near, cy);
                    if (ndiff >= 0) e += 0.4f * ndiff;
                }
                if (a != b) { diff += e; nd++; } else { same += e; ns++; }
            }
        }
        int c0 = Math.Max(-1, (int)Math.Floor(-ox / s)), c1 = Math.Min(mw, (int)Math.Ceiling((g.W - ox) / s));
        int q0 = Math.Max(0, (int)Math.Ceiling((2 - oy) / s)), q1 = Math.Min(mh, (int)Math.Floor((g.H - 3 - oy) / s));
        for (int r = q0; r <= q1; r += stride)
        {
            double y = oy + r * s;
            for (int t = c0; t <= c1; t += stride)
            {
                int a = Key(keys, mw, mh, t, r - 1), b = Key(keys, mw, mh, t, r);
                float e = 0; int cnt = 0;
                if (s >= 12)
                {
                    for (int k = 1; k <= 3; k++)
                    {
                        float ev = EdgeY(g, (int)(ox + (t + k * 0.25) * s), y, sharp);
                        if (ev >= 0) { e += Math.Min(ev, 200f); cnt++; }
                    }
                    if (cnt == 0) continue;
                    e /= cnt;
                }
                else
                {
                    e = EdgeY(g, (int)(ox + (t + 0.5) * s), y, sharp);
                    if (e < 0) continue;
                    e = Math.Min(e, 200f);
                }
                if (r - 1 >= 0 && r < mh && t >= 0 && t < mw)
                {
                    double cx = ox + (t + 0.5) * s;
                    float cd = CenterDiff(g, cx, y - 0.5 * s, cx, y + 0.5 * s);
                    if (cd >= 0) e += 0.4f * cd;
                    float ndiff = CenterDiff(g, cx, y - near, cx, y + near);
                    if (ndiff >= 0) e += 0.4f * ndiff;
                }
                if (a != b) { diff += e; nd++; } else { same += e; ns++; }
            }
        }
        edges = nd;
        if (nd == 0) return 0;
        double raw = diff / nd - (ns > 0 ? same / ns : 0);
        return raw > 0 ? raw * nd / (nd + 16.0) : raw;
    }

    /// <summary>Strongest responses of a set of evenly spaced boundaries (period s) in one axis: top grid phases.</summary>
    private static List<double> Phases(Gradients g, double s, bool horizontal)
    {
        int length = horizontal ? g.W : g.H;
        var profile = new double[length];
        for (int y = 0; y < g.H; y += 2)
            for (int x = 0; x < g.W; x += horizontal ? 1 : 2)
                if (horizontal) profile[x] += g.Gx[y * g.W + x];
                else profile[y] += g.Gy[y * g.W + x];
        double step = s > 40 ? 0.2 : 0.1;
        int m = Math.Max(1, (int)Math.Ceiling(s / step));
        var vals = new double[m];
        for (int i = 0; i < m; i++)
        {
            double p = i * step, v = 0; int n = 0;
            for (double b = p; b < length - 1; b += s) { v += At(profile, b); n++; }
            vals[i] = n > 0 ? v / n : double.MinValue;
        }
        var peaks = new List<(double P, double V)>();
        for (int i = 0; i < m; i++)
        {
            double prev = vals[(i - 1 + m) % m], cur = vals[i], next = vals[(i + 1) % m];
            if (cur >= prev && cur >= next) peaks.Add((i * step, cur));
        }
        double minDist = Math.Min(s * 0.18, 2.5);
        var result = new List<double>();
        foreach (var pk in peaks.OrderByDescending(x => x.V))
        {
            if (result.Any(r => { double dist = Math.Abs(pk.P - r); return Math.Min(dist, s - dist) < minDist; })) continue;
            result.Add(pk.P);
            if (result.Count == 3) break;
        }
        if (result.Count == 0)
        {
            int bestIdx = 0;
            for (int i = 1; i < m; i++) if (vals[i] > vals[bestIdx]) bestIdx = i;
            result.Add(bestIdx * step);
        }
        return result;
    }

    private static ScreenFit? Align(Gradients g, int[] keys, int mw, int mh, double s, double px, double py, out double second)
    {
        second = 0;
        var phxs = Phases(g, s, true);
        var phys = Phases(g, s, false);
        bool fitsX = mw * s <= g.W, fitsY = mh * s <= g.H;
        double minOx = (fitsX ? 0 : g.W - mw * s) - s, maxOx = (fitsX ? g.W - mw * s : 0) + s;
        double minOy = (fitsY ? 0 : g.H - mh * s) - s, maxOy = (fitsY ? g.H - mh * s : 0) + s;
        long cells = (long)Math.Min(mw + 1, g.W / s + 2) * (long)Math.Min(mh + 1, g.H / s + 2) * 2;
        long estX = (long)Math.Min(g.W / s + 3, Math.Max(1, (maxOx - minOx) / s + 2));
        long estY = (long)Math.Min(g.H / s + 3, Math.Max(1, (maxOy - minOy) / s + 2));
        long candidates = estX * estY * phxs.Count * phys.Count;
        int stride = 1;
        while (cells / (stride * stride) * candidates > 8_000_000 && stride < 16) stride++;
        var scored = new List<(double V, double Ox, double Oy)>();
        foreach (double phx in phxs)
        {
            int mx0 = (int)Math.Floor((Math.Max(-px * s, minOx) - phx) / s) - 1;
            int mx1 = (int)Math.Ceiling((Math.Min(g.W - px * s, maxOx) - phx) / s) + 1;
            foreach (double phy in phys)
            {
                int my0 = (int)Math.Floor((Math.Max(-py * s, minOy) - phy) / s) - 1;
                int my1 = (int)Math.Ceiling((Math.Min(g.H - py * s, maxOy) - phy) / s) + 1;
                for (int mx = mx0; mx <= mx1; mx++)
                    for (int my = my0; my <= my1; my++)
                    {
                        double ox = phx + mx * s, oy = phy + my * s;
                        if (ox < minOx || ox > maxOx || oy < minOy || oy > maxOy) continue;
                        double sx = ox + px * s, sy = oy + py * s;
                        if (sx < 0 || sx > g.W || sy < 0 || sy > g.H) continue;
                        double v = Score(g, keys, mw, mh, s, ox, oy, stride, out int n);
                        if (n >= 4) scored.Add((v, ox, oy));
                    }
            }
        }
        if (scored.Count == 0) return null;
        var top = scored.OrderByDescending(c => c.V).Take(24).ToList();
        if (stride > 1)
            top = top.Select(c => (Score(g, keys, mw, mh, s, c.Ox, c.Oy, 1, out _), c.Ox, c.Oy))
                .OrderByDescending(c => c.Item1).ToList();
        var best = Polish(g, keys, mw, mh, s, top[0].Ox, top[0].Oy, top[0].V);
        var runner = top.Where(c => Math.Max(Math.Abs(c.Ox - best.Ox), Math.Abs(c.Oy - best.Oy)) >= s * 0.9)
            .Cast<(double V, double Ox, double Oy)?>().FirstOrDefault();
        if (runner == null && stride == 1)
            runner = scored.Where(c => Math.Max(Math.Abs(c.Ox - best.Ox), Math.Abs(c.Oy - best.Oy)) >= s * 0.9)
                .OrderByDescending(c => c.V).Cast<(double V, double Ox, double Oy)?>().FirstOrDefault();
        if (runner is { } r)
        {
            var rp = Polish(g, keys, mw, mh, s, r.Ox, r.Oy, r.V);
            if (rp.V > best.V) { second = best.V; best = rp; }
            else second = rp.V;
        }
        else second = 0;
        Score(g, keys, mw, mh, s, best.Ox, best.Oy, 1, out int edges);
        return new ScreenFit(s, best.Ox, best.Oy, best.V, 0, edges);
    }

    private static (double V, double Ox, double Oy) Polish(Gradients g, int[] keys, int mw, int mh, double s, double ox, double oy, double bestV)
    {
        double ox0 = ox, oy0 = oy, rad = s * 0.40, step = Math.Clamp(s / 24.0, 0.5, 2.0), fine = Math.Min(2.0, rad);
        void SweepX(double half, double st, bool sharp, ref double bv)
        {
            double bx = ox, lo = Math.Max(ox0 - rad, ox - half), hi = Math.Min(ox0 + rad, ox + half);
            for (double x = lo; x <= hi + 1e-6; x += st)
            {
                double v = Score(g, keys, mw, mh, s, x, oy, 1, out _, sharp);
                if (v > bv) { bv = v; bx = x; }
            }
            ox = bx;
        }
        void SweepY(double half, double st, bool sharp, ref double bv)
        {
            double by = oy, lo = Math.Max(oy0 - rad, oy - half), hi = Math.Min(oy0 + rad, oy + half);
            for (double y = lo; y <= hi + 1e-6; y += st)
            {
                double v = Score(g, keys, mw, mh, s, ox, y, 1, out _, sharp);
                if (v > bv) { bv = v; by = y; }
            }
            oy = by;
        }
        SweepX(rad, step, false, ref bestV);
        SweepY(rad, step, false, ref bestV);
        SweepX(rad, step, false, ref bestV);
        double coarseOx = ox, coarseOy = oy;
        double sharpV = Score(g, keys, mw, mh, s, ox, oy, 1, out _, sharp: true);
        SweepX(fine, 0.25, true, ref sharpV);
        SweepY(fine, 0.25, true, ref sharpV);
        double finalV = Score(g, keys, mw, mh, s, ox, oy, 1, out _);
        return finalV >= bestV * 0.95 ? (finalV, ox, oy) : (bestV, coarseOx, coarseOy);
    }

    private static ScreenFit SnapY(Gradients g, int[] keys, int mw, int mh, ScreenFit fit)
    {
        double s = fit.Scale, ox = fit.OriginX, oy = fit.OriginY, rad = Math.Min(s * 0.28, 13.0);
        double minScore = fit.Score * 0.52;
        double baseY = EdgeAxisY(g, keys, mw, mh, s, ox, oy), bestVy = baseY, bestOy = oy;
        for (double dy = -rad; dy <= rad + 1e-6; dy += 0.25)
        {
            double v = EdgeAxisY(g, keys, mw, mh, s, ox, oy + dy);
            if (v > bestVy && Score(g, keys, mw, mh, s, ox, oy + dy, 1, out _) >= minScore) { bestVy = v; bestOy = oy + dy; }
        }
        return bestVy > baseY + 1.0 ? fit with { OriginY = bestOy } : fit;
    }

    private static double EdgeAxisY(Gradients g, int[] keys, int mw, int mh, double s, double ox, double oy)
    {
        double diff = 0, same = 0; int nd = 0, ns = 0;
        int c0 = Math.Max(-1, (int)Math.Floor(-ox / s)), c1 = Math.Min(mw, (int)Math.Ceiling((g.W - ox) / s));
        int q0 = Math.Max(0, (int)Math.Ceiling((2 - oy) / s)), q1 = Math.Min(mh, (int)Math.Floor((g.H - 3 - oy) / s));
        for (int r = q0; r <= q1; r++)
        {
            double y = oy + r * s;
            for (int t = c0; t <= c1; t++)
            {
                float e = 0; int cnt = 0;
                if (s >= 12)
                {
                    for (int k = 1; k <= 3; k++)
                    {
                        float ev = EdgeY(g, (int)(ox + (t + k * 0.25) * s), y, true);
                        if (ev >= 0) { e += Math.Min(ev, 200f); cnt++; }
                    }
                    if (cnt == 0) continue;
                    e /= cnt;
                }
                else
                {
                    e = EdgeY(g, (int)(ox + (t + 0.5) * s), y, true);
                    if (e < 0) continue;
                    e = Math.Min(e, 200f);
                }
                if (Key(keys, mw, mh, t, r - 1) != Key(keys, mw, mh, t, r)) { diff += e; nd++; }
                else { same += e; ns++; }
            }
        }
        return nd == 0 ? 0 : diff / nd - (ns > 0 ? same / ns : 0);
    }
}
