using Zeus.Core;

/// <summary>
/// Synthetic game frames for the screen locator: a world drawn from per-block textures at a given
/// tile size and camera, over a sky backdrop with mountains, with interface panels, name tags and a
/// player drawn on top. The true origin and tile size are known, so every click can be checked.
/// </summary>
internal static class ScreenFixtures
{
    public sealed record Case(string Name, int Width, int Height, double Scale, double OriginX, double OriginY, int[] Keys, int MapWidth, int MapHeight,
        double PlayerX, double PlayerY, ScreenFrame Frame, List<(int X, int Y, int W, int H)> Covers);

    public static int[] World(int seed, int mw = 100, int mh = 60)
    {
        var random = new Random(seed);
        var keys = new int[mw * mh];
        for (int y = 0; y < mh; y++)
            for (int x = 0; x < mw; x++)
            {
                int k = 0;
                if (y >= mh - 5) k = 5;                                        // bedrock
                else if (y >= mh - 10) k = random.NextDouble() < 0.35 ? 4 : 1; // lava in dirt
                else if (y >= 24) k = random.NextDouble() < 0.07 ? 2 : 1;      // dirt with rocks
                keys[y * mw + x] = k;
            }
        // Dug caves (cave background) and buildings / platforms in the air.
        for (int i = 0; i < 8; i++)
        {
            int cx = random.Next(mw - 8), cy = random.Next(25, mh - 12), w = random.Next(2, 8), h = random.Next(1, 4);
            for (int y = cy; y < cy + h; y++) for (int x = cx; x < cx + w; x++) keys[y * mw + x] = 3;
        }
        for (int i = 0; i < 14; i++)
        {
            int k = 6 + random.Next(7), cx = random.Next(mw - 10), cy = random.Next(4, 23), w = random.Next(1, 10), h = random.Next(1, 4);
            for (int y = cy; y < cy + h && y < 24; y++) for (int x = cx; x < cx + w; x++) keys[y * mw + x] = k;
        }
        return keys;
    }

    private static byte[][] Textures(int seed)
    {
        var random = new Random(seed * 31 + 7);
        var textures = new byte[16][];
        for (int k = 1; k < 16; k++)
        {
            var t = textures[k] = new byte[32 * 32 * 3];
            int br = random.Next(40, 220), bg = random.Next(40, 220), bb = random.Next(40, 220);
            if (k == 1) (br, bg, bb) = (87, 62, 39);      // dirt
            if (k == 2) (br, bg, bb) = (87, 62, 39);      // rock: dirt with a stone in the middle
            if (k == 3) (br, bg, bb) = (60, 42, 30);      // cave background
            if (k == 4) (br, bg, bb) = (200, 50, 20);     // lava
            if (k == 5) (br, bg, bb) = (120, 105, 125);   // bedrock
            bool outline = k >= 6 && k % 2 == 0;
            // Pixel-art textures: blotches of 4x4 texels plus a little fine noise.
            var blotch = new double[8, 8];
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) blotch[x, y] = random.NextDouble() * 30 - 15;
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                {
                    double n = blotch[x / 4, y / 4] + random.NextDouble() * 8 - 4;
                    if (k == 1 || k == 2) n += ((x + y) / 4 % 2 == 0) ? -10 : 8; // diagonal stripes
                    if (k == 5) n += random.NextDouble() < 0.4 ? 40 : -20;
                    int r = br, g = bg, b = bb;
                    if (k == 2 && Math.Abs(x - 16) < 6 && Math.Abs(y - 18) < 6) (r, g, b) = (150, 150, 140);
                    if (outline && (x == 0 || y == 0 || x == 31 || y == 31)) (r, g, b) = (r / 3, g / 3, b / 3);
                    int i = (y * 32 + x) * 3;
                    t[i] = (byte)Math.Clamp(r + n, 0, 255); t[i + 1] = (byte)Math.Clamp(g + n, 0, 255); t[i + 2] = (byte)Math.Clamp(b + n, 0, 255);
                }
        }
        return textures;
    }

    /// <summary>The camera as the game moves it: centred on the player (plus lag), clamped to the
    /// world; a world smaller than the view sits against the right / bottom edge.</summary>
    public static double Camera(int view, double scale, int worldTiles, double player, double lag)
    {
        double world = worldTiles * scale;
        double origin = view / 2.0 - (player + lag) * scale;
        origin = Math.Min(origin, 0);
        origin = Math.Max(origin, view - world);
        return origin;
    }

    public static Case Make(string name, int seed, int width, int height, double scale, double playerX, double playerY, double lagX, double lagY)
    {
        const int mw = 100, mh = 60;
        var keys = World(seed, mw, mh);
        var textures = Textures(seed);
        double ox = Camera(width, scale, mw, playerX, lagX), oy = Camera(height, scale, mh, playerY, lagY);
        var pixels = new byte[width * height * 4];
        var random = new Random(seed + 99);
        int samples = Math.Clamp((int)Math.Ceiling(40 / scale), 1, 6); // small tiles are drawn filtered (several texels per pixel)
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double r = 0, g = 0, b = 0;
                for (int sy = 0; sy < samples; sy++)
                    for (int sx = 0; sx < samples; sx++)
                    {
                        double px = x + (sx + 0.5) / samples, py = y + (sy + 0.5) / samples;
                        double u = (px - ox) / scale, v = (py - oy) / scale;
                        int tx = (int)Math.Floor(u), ty = (int)Math.Floor(v);
                        int k = tx >= 0 && ty >= 0 && tx < mw && ty < mh ? keys[ty * mw + tx] : 0;
                        if (k == 0)
                        {
                            // Sky gradient, a mountain and a sun, fixed to the screen like the game's backdrop.
                            double cr = 96 + py * 0.06, cg = 200 + py * 0.02, cb = 242;
                            double ridge = height * 0.35 + Math.Sin(px / width * 5.0) * height * 0.12;
                            if (py > ridge) (cr, cg, cb) = (150, 155, 225);
                            if (Math.Pow(px - width * 0.75, 2) + Math.Pow(py - height * 0.18, 2) < Math.Pow(height * 0.09, 2)) (cr, cg, cb) = (250, 220, 40);
                            r += cr; g += cg; b += cb;
                        }
                        else
                        {
                            var t = textures[k];
                            int ix = Math.Clamp((int)((u - tx) * 32), 0, 31), iy = Math.Clamp((int)((v - ty) * 32), 0, 31);
                            int i = (iy * 32 + ix) * 3;
                            r += t[i]; g += t[i + 1]; b += t[i + 2];
                        }
                    }
                int n = samples * samples, o = (y * width + x) * 4;
                pixels[o] = (byte)(b / n); pixels[o + 1] = (byte)(g / n); pixels[o + 2] = (byte)(r / n); pixels[o + 3] = 255;
            }
        var covers = new List<(int X, int Y, int W, int H)>();
        void Fill(int x0, int y0, int w, int h, byte r, byte g, byte b, bool border)
        {
            covers.Add((x0, y0, w, h));
            for (int y = Math.Max(0, y0); y < Math.Min(height, y0 + h); y++)
                for (int x = Math.Max(0, x0); x < Math.Min(width, x0 + w); x++)
                {
                    bool edge = border && (x - x0 < 3 || y - y0 < 3 || x0 + w - x <= 3 || y0 + h - y <= 3);
                    int o = (y * width + x) * 4;
                    pixels[o] = edge ? (byte)30 : b; pixels[o + 1] = edge ? (byte)40 : g; pixels[o + 2] = edge ? (byte)50 : r;
                }
        }
        // Interface: left panel, chat box, right buttons, bottom handle.
        Fill(8, 40, 92, 450, 40, 120, 150, true);
        Fill(width / 4, 30, width / 2, 125, 50, 130, 170, true);
        for (int i = 0; i < 5; i++) Fill(width - 80, 60 + i * 90, 60, 60, 230, 240, 250, true);
        Fill(width / 2 - 25, height - 40, 50, 40, 160, 210, 230, true);
        // The player and its name tag.
        int pxs = (int)(ox + playerX * scale), pys = (int)(oy + playerY * scale);
        Fill(pxs - (int)(scale * 0.4), pys - (int)(scale * 0.5), (int)Math.Max(3, scale * 0.8), (int)Math.Max(4, scale), 250, 200, 60, false);
        Fill(pxs - 90, pys - (int)scale - 22, 180, 16, 60, 220, 60, false);
        // A few dropped items / particles.
        for (int i = 0; i < 25; i++) Fill(random.Next(width), random.Next(height), random.Next(4, 14), random.Next(4, 14), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), false);
        return new Case(name, width, height, scale, ox, oy, keys, mw, mh, playerX, playerY, new ScreenFrame(width, height, pixels), covers);
    }

    /// <summary>Checks a fit against the truth: every click that is not right on a tile edge or under an interface panel.</summary>
    public static string? Check(Case c, ScreenFit fit)
    {
        var random = new Random(5);
        int checkedClicks = 0;
        for (int i = 0; i < 3000; i++)
        {
            int x = random.Next(c.Width), y = random.Next(c.Height);
            double u = (x - c.OriginX) / c.Scale, v = (y - c.OriginY) / c.Scale;
            if (Math.Abs(u - Math.Round(u)) * c.Scale < 0.6 || Math.Abs(v - Math.Round(v)) * c.Scale < 0.6) continue;
            int tx = (int)Math.Floor(u), ty = (int)Math.Floor(v);
            if (tx < 0 || ty < 0 || tx >= c.MapWidth || ty >= c.MapHeight) continue;
            checkedClicks++;
            var got = fit.Tile(x, y);
            if (got != (tx, ty)) return $"tıklama {x},{y}: beklenen {tx},{ty}, bulunan {got.X},{got.Y}";
        }
        return checkedClicks < 50 ? "yeterli tıklama yok" : null;
    }
}
