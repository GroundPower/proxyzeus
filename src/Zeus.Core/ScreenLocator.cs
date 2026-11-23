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
        var scales = Scales(image, out string scaleNote);
        if (scales.Count == 0) { note = "karo boyutu ölçülemedi" + scaleNote; return null; }
        ScreenFit? best = null; double runnerUp = 0;
        foreach (double s in scales)
        {
            var fit = Align(image, keys, mapWidth, mapHeight, s, playerX, playerY, out double second);
            if (fit == null) continue;
            if (best == null || fit.Score > best.Score) { runnerUp = Math.Max(best?.Score ?? 0, second); best = fit; }
            else runnerUp = Math.Max(runnerUp, fit.Score);
        }
        if (best == null) { note = $"hizalama bulunamadı (ölçek adayları {string.Join("/", scales.Select(s => s.ToString("0.00")))})"; return null; }
        best = Refine(image, keys, mapWidth, mapHeight, best);
        double margin = best.Score > 0 ? (best.Score - runnerUp) / best.Score : 0;
        best = best with { Margin = margin };
        note = $"ölçek {best.Scale:0.000}, köken {best.OriginX:0.0},{best.OriginY:0.0}, skor {best.Score:0.0}, fark %{margin * 100:0}, kenar {best.Edges}";
        if (best.Edges < 12) { note += " (yetersiz kenar)"; return null; }
        if (margin < 0.12) { note += " (belirsiz)"; return null; }
        return best;
    }

    /// <summary>Tile size candidates of a frame (diagnostics and tests).</summary>
    public static List<double> MeasureScale(ScreenFrame frame, out string note) => Scales(new Gradients(frame), out note);

    // ---------------------------------------------------------------- image

    private sealed class Gradients
    {
        public readonly int W, H;
        public readonly float[] Gx, Gy, Luma;
        public Gradients(ScreenFrame f)
        {
            W = f.Width; H = f.Height;
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
            double sum = 0; int n = 0;
            for (double lag = s; lag + 2 < ac.Length; lag += s) { double v = Peak(ac, lag); if (!double.IsNaN(v)) { sum += v; n++; } }
            // Mean peak height weighted by √count: a multiple of the tile size has as high peaks but
            // fewer of them, half the tile size twice as many but half as high.
            return n >= 2 ? sum / Math.Sqrt(n) : double.NaN;
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
        // alignment decides. Smallest first, at most four distinct ones.
        double Both(double s) { double vx = Mean(acx, s), vy = Mean(acy, s); return (double.IsNaN(vx) ? 0 : vx) + (double.IsNaN(vy) ? 0 : vy); }
        var result = new List<double>();
        foreach (var p in peaks.OrderByDescending(p => p.V))
        {
            if (result.Any(r => Math.Abs(p.S / r - 1) < 0.03)) continue;
            result.Add(Sharpen(p.S, Both));
            if (result.Count == 4) break;
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
    private static float EdgeX(Gradients g, double x, int y)
    {
        int b = (int)Math.Round(x);
        if (b < 2 || b >= g.W - 2 || y < 0 || y >= g.H) return -1;
        int row = y * g.W;
        return Math.Max(g.Gx[row + b], Math.Max(g.Gx[row + b - 1], g.Gx[row + b + 1]));
    }

    private static float EdgeY(Gradients g, int x, double y)
    {
        int b = (int)Math.Round(y);
        if (b < 2 || b >= g.H - 2 || x < 0 || x >= g.W) return -1;
        return Math.Max(g.Gy[b * g.W + x], Math.Max(g.Gy[(b - 1) * g.W + x], g.Gy[(b + 1) * g.W + x]));
    }

    private static int Key(int[] keys, int w, int h, int x, int y) => x < 0 || y < 0 || x >= w || y >= h ? 0 : keys[y * w + x];

    /// <summary>
    /// How well an alignment explains the image: mean edge strength where the map says two different
    /// things touch, minus the mean where it says they are the same. <paramref name="stride"/> thins
    /// the samples for the wide search.
    /// </summary>
    private static double Score(Gradients g, int[] keys, int mw, int mh, double s, double ox, double oy, int stride, out int edges)
    {
        double diff = 0, same = 0; int nd = 0, ns = 0;
        int t0 = Math.Max(0, (int)Math.Ceiling((2 - ox) / s)), t1 = Math.Min(mw, (int)Math.Floor((g.W - 3 - ox) / s));
        int r0 = Math.Max(-1, (int)Math.Floor(-oy / s)), r1 = Math.Min(mh, (int)Math.Ceiling((g.H - oy) / s));
        // Vertical boundaries: between column t-1 and t.
        for (int t = t0; t <= t1; t += stride)
        {
            double x = ox + t * s;
            for (int r = r0; r <= r1; r += stride)
            {
                int a = Key(keys, mw, mh, t - 1, r), b = Key(keys, mw, mh, t, r);
                int y = (int)(oy + (r + 0.5) * s);
                float e = EdgeX(g, x, y);
                if (e < 0) continue;
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
                int x = (int)(ox + (t + 0.5) * s);
                float e = EdgeY(g, x, y);
                if (e < 0) continue;
                if (a != b) { diff += e; nd++; } else { same += e; ns++; }
            }
        }
        edges = nd;
        if (nd == 0) return 0;
        return diff / nd - (ns > 0 ? same / ns : 0);
    }

    /// <summary>Strongest response of a set of evenly spaced boundaries (period s) in one axis: the grid phase.</summary>
    private static double Phase(Gradients g, double s, bool horizontal)
    {
        int length = horizontal ? g.W : g.H;
        var profile = new double[length];
        for (int y = 0; y < g.H; y += 2)
            for (int x = 0; x < g.W; x += horizontal ? 1 : 2)
                if (horizontal) profile[x] += g.Gx[y * g.W + x];
                else profile[y] += g.Gy[y * g.W + x];
        double best = 0, bestV = double.MinValue;
        for (double p = 0; p < s; p += 0.1)
        {
            double v = 0; int n = 0;
            for (double b = p; b < length - 1; b += s) { v += At(profile, b); n++; }
            if (n > 0 && v / n > bestV) { bestV = v / n; best = p; }
        }
        return best;
    }

    private static ScreenFit? Align(Gradients g, int[] keys, int mw, int mh, double s, double px, double py, out double second)
    {
        second = 0;
        double phx = Phase(g, s, true), phy = Phase(g, s, false);
        // Origins on the grid that keep the player on screen.
        int mx0 = (int)Math.Floor((-px * s - phx) / s) - 1, mx1 = (int)Math.Ceiling((g.W - px * s - phx) / s) + 1;
        int my0 = (int)Math.Floor((-py * s - phy) / s) - 1, my1 = (int)Math.Ceiling((g.H - py * s - phy) / s) + 1;
        long cells = (long)(g.W / s + 2) * (long)(g.H / s + 2) * 2;
        long candidates = (long)(mx1 - mx0 + 1) * (my1 - my0 + 1);
        int stride = 1;
        while (cells / (stride * stride) * candidates > 25_000_000 && stride < 16) stride++;
        var scored = new List<(double V, int Mx, int My)>();
        for (int mx = mx0; mx <= mx1; mx++)
            for (int my = my0; my <= my1; my++)
            {
                double v = Score(g, keys, mw, mh, s, phx + mx * s, phy + my * s, stride, out int n);
                if (n >= 4) scored.Add((v, mx, my));
            }
        if (scored.Count == 0) return null;
        var top = scored.OrderByDescending(c => c.V).Take(stride > 1 ? 12 : 2).ToList();
        if (stride > 1)
            top = top.Select(c => (Score(g, keys, mw, mh, s, phx + c.Mx * s, phy + c.My * s, 1, out _), c.Mx, c.My))
                .OrderByDescending(c => c.Item1).ToList();
        var (bv, bmx, bmy) = top[0];
        second = top.Count > 1 ? top[1].V : 0;
        Score(g, keys, mw, mh, s, phx + bmx * s, phy + bmy * s, 1, out int edges);
        return new ScreenFit(s, phx + bmx * s, phy + bmy * s, bv, 0, edges);
    }

    // ---------------------------------------------------------------- 3. sub-pixel

    /// <summary>
    /// Measures where each predicted edge really is (centroid of the gradient around it) and fits
    /// origin and tile size to those positions, per axis.
    /// </summary>
    private static ScreenFit Refine(Gradients g, int[] keys, int mw, int mh, ScreenFit fit)
    {
        double s = fit.Scale, ox = fit.OriginX, oy = fit.OriginY;
        for (int round = 0; round < 3; round++)
        {
            var xs = new List<(double T, double Pos)>(); var ys = new List<(double T, double Pos)>();
            for (int t = 0; t <= mw; t++)
            {
                double x = ox + t * s;
                if (x < 3 || x > g.W - 4) continue;
                for (int r = -1; r <= mh; r++)
                {
                    if (Key(keys, mw, mh, t - 1, r) == Key(keys, mw, mh, t, r)) continue;
                    for (double f = 0.3; f <= 0.71; f += 0.2)
                    {
                        int y = (int)(oy + (r + f) * s);
                        if (y < 0 || y >= g.H) continue;
                        if (Centroid(g.Gx, y * g.W, 1, x, g.W) is double c) xs.Add((t, c));
                    }
                }
            }
            for (int r = 0; r <= mh; r++)
            {
                double y = oy + r * s;
                if (y < 3 || y > g.H - 4) continue;
                for (int t = -1; t <= mw; t++)
                {
                    if (Key(keys, mw, mh, t, r - 1) == Key(keys, mw, mh, t, r)) continue;
                    for (double f = 0.3; f <= 0.71; f += 0.2)
                    {
                        int x = (int)(ox + (t + f) * s);
                        if (x < 0 || x >= g.W) continue;
                        if (Centroid(g.Gy, x, g.W, y, g.H) is double c) ys.Add((r, c));
                    }
                }
            }
            var lx = Line(xs, ox, s); var ly = Line(ys, oy, s);
            double ns = lx.N + ly.N > 0 ? (lx.S * lx.N + ly.S * ly.N) / (lx.N + ly.N) : s;
            if (Math.Abs(ns / s - 1) > 0.01) ns = s; // never far from the measured size
            ox = lx.N > 0 ? lx.O : ox; oy = ly.N > 0 ? ly.O : oy; s = ns;
        }
        double score = Score(g, keys, mw, mh, s, ox, oy, 1, out int edges);
        return fit with { Scale = s, OriginX = ox, OriginY = oy, Score = Math.Max(score, fit.Score), Edges = edges };
    }

    /// <summary>Gradient centroid within ±1.6 px of <paramref name="at"/> along a line (null when there is no clear edge).</summary>
    private static double? Centroid(float[] grad, int start, int stride, double at, int length)
    {
        int b0 = (int)Math.Floor(at - 1.6), b1 = (int)Math.Ceiling(at + 1.6);
        if (b0 < 1 || b1 >= length - 1) return null;
        double sum = 0, moment = 0, peak = 0, outside = 0;
        for (int b = b0; b <= b1; b++)
        {
            double v = grad[start + b * stride];
            sum += v; moment += v * b; peak = Math.Max(peak, v);
        }
        outside = Math.Max(grad[start + (b0 - 1) * stride], grad[start + (b1 + 1) * stride]);
        if (peak < 30 || sum <= 0 || outside > peak * 0.8) return null;
        return moment / sum;
    }

    /// <summary>Robust line fit pos = o + t × s (iteratively drops points far from the line).</summary>
    private static (double O, double S, int N) Line(List<(double T, double Pos)> points, double o, double s)
    {
        if (points.Count < 6) return (o, s, 0);
        var use = points;
        for (int pass = 0; pass < 3; pass++)
        {
            use = use.Where(p => Math.Abs(p.Pos - (o + p.T * s)) <= (pass == 0 ? 1.6 : 0.8)).ToList();
            if (use.Count < 6) return (o, s, 0);
            double mt = use.Average(p => p.T), mp = use.Average(p => p.Pos);
            double vt = use.Sum(p => (p.T - mt) * (p.T - mt));
            if (vt > 1e-9 && use.Select(p => p.T).Distinct().Count() >= 3)
                s = use.Sum(p => (p.T - mt) * (p.Pos - mp)) / vt;
            o = mp - s * mt;
        }
        return (o, s, use.Count);
    }
}
