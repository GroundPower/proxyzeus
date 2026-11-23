using System.Text.Json;

namespace Zeus.Core;

/// <summary>A mouse event inside the game window, in client-area pixels. Delta: wheel notches (+ away from the user).</summary>
public sealed record MouseEvent(MouseEventKind Kind, int X, int Y, int Width, int Height, bool Shift, double Delta = 0,
    long Timestamp = 0, long WindowId = 0);
public enum MouseEventKind { LeftDown, Wheel }

/// <summary>
/// What the camera model keeps between runs. While path is on Zeus sets the game's zoom itself to
/// one of its own levels, so a level always means the same zoom: its measured scale is valid in every
/// session (for the same window height).
/// </summary>
public sealed record CameraMemory(int Height = 0, double Scale0 = 0, double StepFactor = 0, double OffsetX = 0, double OffsetY = 0,
    int Format = CameraMemory.CurrentFormat, int ZoomHeight = 0, double ZoomK = 0, double ZoomP = 1)
{
    /// <summary>Bumped when older files may hold values that must not be reused.</summary>
    public const int CurrentFormat = 5;
    public static CameraMemory Load(string? path)
    {
        try
        {
            if (path == null || !File.Exists(path)) return new();
            string text = File.ReadAllText(path);
            if (!text.Contains("\"Format\"", StringComparison.Ordinal)) return new();
            var memory = JsonSerializer.Deserialize<CameraMemory>(text) ?? new();
            return memory.Format == CurrentFormat ? memory : new();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Turns a mouse position in the game window into a world tile. On each axis
/// <c>screen = centre + scale * (tile - camera + offset)</c>; the camera follows the player but stops
/// at the world's edges.
///
/// The scale (pixels per tile) is the zoom. Zeus sets the zoom itself (OnZoomCamera) to one of its
/// own levels, so the scale of a level is a fixed number: it is measured once from clicks the game
/// resolves itself (screen point + exact tile), and the levels are linked by a constant factor
/// (scale = scale0 × factor ^ level). Both are remembered between runs.
/// </summary>
public sealed class CameraModel
{
    private sealed record Sample(double Dx, double Dy, double TileX, double TileY, double PlayerX, double PlayerY,
        int WorldWidth, int WorldHeight, int Level, long Order, double Zoom = double.NaN);
    private readonly List<Sample> samples = [];
    private int width, height, rejected; private long order;
    /// <summary>The zoom level Zeus has set (0 = its base zoom).</summary>
    public int Level { get; private set; }
    public CameraMemory Memory { get; private set; }
    public int SampleCount => samples.Count;
    /// <summary>Raised when something new was learned (worth saving).</summary>
    public event Action<CameraMemory>? Learned;

    public CameraModel(CameraMemory? memory = null) => Memory = memory ?? new();

    public void SetLevel(int level) => Level = level;
    /// <summary>The game's own zoom value read from its memory (NaN when not known). When it is known,
    /// scale = ZoomK × zoom ^ ZoomP, learned once per window height and kept between runs.</summary>
    public double Zoom { get; private set; } = double.NaN;
    public void SetZoom(double? zoom) => Zoom = zoom is double z && double.IsFinite(z) && z > 0 ? z : double.NaN;
    public void Reset() { samples.Clear(); rejected = 0; }

    private static double Camera(double player, double halfView, int worldSize)
    {
        if (worldSize <= 0) return player;
        if (halfView * 2 >= worldSize) return worldSize / 2.0;
        return Math.Clamp(player, halfView, worldSize - halfView);
    }

    private (double X, double Y) Offset(Sample v, double scale)
    {
        double halfX = scale > 0 ? width / 2.0 / scale : 0, halfY = scale > 0 ? height / 2.0 / scale : 0;
        return (v.TileX - Camera(v.PlayerX, halfX, v.WorldWidth), v.TileY - Camera(v.PlayerY, halfY, v.WorldHeight));
    }

    /// <param name="playerX">Player centre in tiles (pixels / 32).</param>
    /// <param name="worldWidth">World size in tiles (0 = unknown, no edge handling).</param>
    public void AddSample(int clientWidth, int clientHeight, double mouseX, double mouseY, double playerX, double playerY, int tileX, int tileY,
        int worldWidth = 0, int worldHeight = 0)
    {
        if (clientWidth != width || clientHeight != height) { samples.Clear(); width = clientWidth; height = clientHeight; }
        if (Math.Abs(tileX + 0.5 - playerX) < 0.75 && Math.Abs(tileY + 0.5 - playerY) < 0.75) return; // own tile: says nothing about the zoom
        var sample = new Sample(mouseX - clientWidth / 2.0, mouseY - clientHeight / 2.0, tileX + 0.5, tileY + 0.5,
            playerX, playerY, worldWidth, worldHeight, Level, ++order, Zoom);
        // Checked against what is known for this level: a click that disagrees (camera still moving,
        // mis-paired click) is dropped; three in a row mean the known value is wrong and is relearned.
        var local = samples.Where(v => v.Level == sample.Level).ToList();
        bool known = local.Count >= 2 ? Solve(local, out double s, out double ox, out double oy, out _) : Current(out s, out ox, out oy);
        if (known)
        {
            var (tx, ty) = Offset(sample, s);
            double error = Math.Max(Math.Abs(sample.Dx - s * (tx + ox)), Math.Abs(sample.Dy - s * (ty + oy)));
            if (error > s * 0.85)
            {
                if (++rejected < 3) return;
                samples.RemoveAll(v => v.Level == sample.Level);
                if (local.Count < 2) Memory = new CameraMemory(); // the remembered values were wrong
            }
        }
        rejected = 0;
        samples.Add(sample);
        if (samples.Count(v => v.Level == sample.Level) > 12) samples.Remove(samples.First(v => v.Level == sample.Level));
        if (samples.Count > 80) samples.RemoveAt(0);
        Remember();
    }

    /// <summary>
    /// Scale and offsets for one level from its own clicks. The camera clamp depends on the scale, so
    /// the scale is searched directly (coarse then fine, on a log scale); the offsets have a closed
    /// form (learned from 3+ clicks, otherwise the remembered ones). A result the clicks do not pin
    /// down (e.g. a single click at the camera centre) is refused.
    /// </summary>
    private bool Solve(List<Sample> group, out double scale, out double offsetX, out double offsetY, out double error)
    {
        scale = offsetX = offsetY = 0; error = double.MaxValue;
        if (group.Count == 0) return false;
        bool learnOffset = group.Count >= 3;
        double Evaluate(double s, out double ox, out double oy)
        {
            double sx = 0, sy = 0;
            var rows = new (double Dx, double Dy, double Tx, double Ty)[group.Count];
            for (int i = 0; i < group.Count; i++)
            {
                var (tx, ty) = Offset(group[i], s);
                rows[i] = (group[i].Dx, group[i].Dy, tx, ty);
                sx += group[i].Dx - s * tx; sy += group[i].Dy - s * ty;
            }
            ox = learnOffset ? sx / group.Count / s : Memory.OffsetX;
            oy = learnOffset ? sy / group.Count / s : Memory.OffsetY;
            double sum = 0;
            foreach (var r in rows) sum += Math.Pow(r.Dx - s * (r.Tx + ox), 2) + Math.Pow(r.Dy - s * (r.Ty + oy), 2);
            return sum;
        }
        const int Steps = 360;
        double low = Math.Log(2), high = Math.Log(2000), step = (high - low) / Steps, best = double.MaxValue, bestLog = 0;
        for (int i = 0; i <= Steps; i++)
        {
            double e = Evaluate(Math.Exp(low + i * step), out _, out _);
            if (e < best) { best = e; bestLog = low + i * step; }
        }
        double a = bestLog - step, b = bestLog + step;
        for (int i = 0; i < 40; i++)
        {
            double m1 = a + (b - a) / 3, m2 = b - (b - a) / 3;
            if (Evaluate(Math.Exp(m1), out _, out _) < Evaluate(Math.Exp(m2), out _, out _)) b = m2; else a = m1;
        }
        scale = Math.Exp((a + b) / 2);
        error = Evaluate(scale, out offsetX, out offsetY);
        if (Math.Abs(offsetX) > 3 || Math.Abs(offsetY) > 3 || scale is < 2 or > 2000) return false;
        double worse = Math.Min(Evaluate(scale * 0.85, out _, out _), Evaluate(scale * 1.18, out _, out _));
        return worse - error >= 9;
    }

    /// <summary>Levels measured in this run, newest first.</summary>
    private List<(int Level, double Scale, double OffsetX, double OffsetY, int Count, long Order)> Levels()
    {
        var levels = new List<(int, double, double, double, int, long)>();
        foreach (var group in samples.Where(v => !double.IsFinite(Zoom) || !double.IsFinite(v.Zoom) || Math.Abs(v.Zoom - Zoom) < Zoom * 0.002).GroupBy(v => v.Level))
        {
            var list = group.ToList();
            if (Solve(list, out double s, out double ox, out double oy, out _))
                levels.Add((group.Key, s, ox, oy, list.Count, list.Max(v => v.Order)));
        }
        return levels.OrderByDescending(l => l.Item6).ToList();
    }

    /// <summary>Learns the per-level factor, the level-0 scale and the screen offset.</summary>
    private void Remember()
    {
        RememberZoom();
        var levels = Levels();
        if (levels.Count == 0) return;
        double factor = Memory.StepFactor, offsetX = Memory.OffsetX, offsetY = Memory.OffsetY;
        var ratios = new List<double>();
        for (int i = 0; i < levels.Count && i < 8; i++)
            for (int j = i + 1; j < levels.Count && j < 8; j++)
            {
                int apart = levels[i].Level - levels[j].Level;
                if (apart == 0 || levels[i].Count < 1 || levels[j].Count < 1) continue;
                double f = Math.Pow(levels[i].Scale / levels[j].Scale, 1.0 / apart);
                if (f is > 0.5 and < 2) ratios.Add(f);
            }
        if (ratios.Count > 0) { ratios.Sort(); factor = ratios[ratios.Count / 2]; }
        var steady = levels.Where(l => l.Count >= 3).ToList();
        if (steady.Count > 0) { offsetX = steady.Average(l => l.OffsetX); offsetY = steady.Average(l => l.OffsetY); }
        // The level-0 scale, from the levels measured now (bridged by the factor when needed).
        var usable = levels.Where(l => l.Level == 0 || factor > 0).ToList();
        double scale0 = usable.Count > 0 ? usable.Average(l => l.Scale / Math.Pow(factor > 0 ? factor : 1, l.Level)) : Memory.Scale0;
        if (scale0 <= 0) scale0 = height > 0 ? height / 16.0 : (Memory.Scale0 > 0 ? Memory.Scale0 : 48.0);
        if (factor > 0 && factor is < 1.01 or > 1.30) factor = 1.09;
        var learned = new CameraMemory(height, scale0, factor, offsetX, offsetY);
        if (Math.Abs(learned.StepFactor - Memory.StepFactor) < 0.0005 && Math.Abs(learned.Scale0 - Memory.Scale0) < 0.05
            && Math.Abs(learned.OffsetX - Memory.OffsetX) < 0.01 && Math.Abs(learned.OffsetY - Memory.OffsetY) < 0.01
            && learned.Height == Memory.Height) return;
        Memory = learned;
        Learned?.Invoke(learned);
    }

    /// <summary>
    /// With the game's zoom value known for each click, every single click gives (zoom, scale). The
    /// relation scale = K × zoom ^ P is fitted on a log scale (P stays 1 until two zooms are seen).
    /// </summary>
    private void RememberZoom()
    {
        var points = new List<(double LogZoom, double LogScale)>();
        foreach (var v in samples.Where(v => double.IsFinite(v.Zoom)))
            if (Solve([v], out double s, out _, out _, out _)) points.Add((Math.Log(v.Zoom), Math.Log(s)));
        if (points.Count == 0) return;
        double p = Memory.ZoomP > 0 || Memory.ZoomP < 0 ? Memory.ZoomP : 1;
        double spread = points.Max(q => q.LogZoom) - points.Min(q => q.LogZoom);
        if (points.Count >= 2 && spread > 0.05)
        {
            double mx = points.Average(q => q.LogZoom), my = points.Average(q => q.LogScale);
            double cov = points.Sum(q => (q.LogZoom - mx) * (q.LogScale - my)), varx = points.Sum(q => (q.LogZoom - mx) * (q.LogZoom - mx));
            if (varx > 0 && Math.Abs(cov / varx) is > 0.2 and < 5) p = cov / varx;
        }
        var logK = points.Select(q => q.LogScale - p * q.LogZoom).OrderBy(x => x).ToList();
        double k = Math.Exp(logK[logK.Count / 2]);
        var learned = Memory with { ZoomHeight = height, ZoomK = k, ZoomP = p };
        if (learned.ZoomHeight == Memory.ZoomHeight && Math.Abs(learned.ZoomK - Memory.ZoomK) < Math.Max(0.01, Memory.ZoomK * 0.002)
            && Math.Abs(learned.ZoomP - Memory.ZoomP) < 0.005) return;
        Memory = learned;
        Learned?.Invoke(learned);
    }

    /// <summary>The scale at the current level: measured now, else computed from the remembered (or now measured) level-0 scale and factor.</summary>
    private bool Current(out double scale, out double offsetX, out double offsetY)
    {
        scale = offsetX = offsetY = 0;
        // The game's own zoom value: exact whenever the zoom-to-scale relation is known.
        if (double.IsFinite(Zoom) && Memory.ZoomK > 0 && Memory.ZoomHeight == height)
        {
            scale = Memory.ZoomK * Math.Pow(Zoom, Memory.ZoomP);
            offsetX = Memory.OffsetX; offsetY = Memory.OffsetY;
            if (scale is >= 2 and <= 2000) return true;
        }
        var levels = Levels();
        var here = levels.FirstOrDefault(l => l.Level == Level);
        if (here.Count > 0) { (scale, offsetX, offsetY) = (here.Scale, here.OffsetX, here.OffsetY); return true; }
        offsetX = Memory.OffsetX; offsetY = Memory.OffsetY;
        double stepFactor = Memory.StepFactor is > 1.01 and < 1.3 ? Memory.StepFactor : 1.09;
        if (levels.Count > 0 && Memory.StepFactor > 0)
        {
            var anchor = levels[0];
            scale = anchor.Scale * Math.Pow(stepFactor, Level - anchor.Level);
        }
        else if (Memory.Scale0 > 0 && Memory.Height == height && (Level == 0 || Memory.StepFactor > 0))
            scale = Memory.Scale0 * Math.Pow(stepFactor, Level);
        else return false;
        return scale is >= 2 and <= 2000;
    }

    /// <summary>Pixels per tile at the current level, for the diagnostic log (null when not known).</summary>
    public double? CurrentScale => height > 0 && Current(out double s, out _, out _) ? s : null;

    /// <summary>The tile under the cursor, or false when the current level's scale is not known yet.</summary>
    public bool TryTile(int clientWidth, int clientHeight, double mouseX, double mouseY, double playerX, double playerY, out int tileX, out int tileY,
        int worldWidth = 0, int worldHeight = 0)
    {
        tileX = tileY = 0;
        if (clientWidth != width || clientHeight != height) { samples.Clear(); width = clientWidth; height = clientHeight; }
        if (!Current(out double scale, out double ox, out double oy)) return false;
        double cameraX = Camera(playerX, clientWidth / 2.0 / scale, worldWidth), cameraY = Camera(playerY, clientHeight / 2.0 / scale, worldHeight);
        tileX = (int)Math.Floor(cameraX - ox + (mouseX - clientWidth / 2.0) / scale);
        tileY = (int)Math.Floor(cameraY - oy + (mouseY - clientHeight / 2.0) / scale);
        return true;
    }
}
