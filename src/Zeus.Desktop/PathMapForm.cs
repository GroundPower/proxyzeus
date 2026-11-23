using System.Drawing.Drawing2D;
using Zeus.Core;

namespace Zeus.Desktop;

internal sealed class PathMapForm : Form
{
    public PathMapForm(PathMapView view, Func<int, int, bool> select)
    {
        Text = $"{view.World} · Path haritası";
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        ClientSize = new Size(920, 620);
        MinimumSize = new Size(440, 300);
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };

        var heading = Theme.Label("Hedef karoyu seç", 15, bold: true);
        heading.Dock = DockStyle.Top;
        heading.Height = 36;
        const string hint = "Mavi işaret konumunu gösterir. Açık renkli bir karoya tıkla.";
        var status = Theme.Label(hint, 10, Theme.Muted);
        status.Dock = DockStyle.Bottom;
        status.Height = 30;
        var grid = new TileGrid(view) { Dock = DockStyle.Fill };
        grid.Hovered += tile => status.Text = tile is { } p
            ? $"Karo {p.X}, {p.Y} · {(view.OpenTiles[p.Y * view.Width + p.X] ? "Açık" : "Kapalı")}" : hint;
        grid.Selected += tile =>
        {
            if (select(tile.X, tile.Y)) Close();
            else status.Text = "Hedef gönderilemedi. Oyuna dönüp /path map ile tekrar aç.";
        };
        Padding = new Padding(16, 10, 16, 5);
        Controls.Add(grid);
        Controls.Add(status);
        Controls.Add(heading);
    }

    private sealed class TileGrid : Control
    {
        private readonly PathMapView view;
        private readonly Bitmap tiles;
        private Point? hover;
        public event Action<Point?>? Hovered;
        public event Action<Point>? Selected;

        public TileGrid(PathMapView view)
        {
            this.view = view;
            BackColor = Theme.Nav;
            DoubleBuffered = true;
            ResizeRedraw = true;
            tiles = new Bitmap(view.Width, view.Height);
            var open = Color.FromArgb(111, 128, 137);
            var blocked = Color.FromArgb(42, 50, 61);
            for (int y = 0; y < view.Height; y++)
                for (int x = 0; x < view.Width; x++)
                    tiles.SetPixel(x, y, view.OpenTiles[y * view.Width + x] ? open : blocked);
        }

        private RectangleF MapBounds()
        {
            float scale = Math.Min(ClientSize.Width / (float)view.Width, ClientSize.Height / (float)view.Height);
            float width = scale * view.Width, height = scale * view.Height;
            return new((ClientSize.Width - width) / 2, (ClientSize.Height - height) / 2, width, height);
        }

        private Point? Hit(Point location)
        {
            var bounds = MapBounds();
            if (!bounds.Contains(location.X, location.Y)) return null;
            int x = (int)((location.X - bounds.Left) / bounds.Width * view.Width);
            int y = (int)((location.Y - bounds.Top) / bounds.Height * view.Height);
            return x < view.Width && y < view.Height ? new Point(x, y) : null;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var bounds = MapBounds();
            if (bounds.Width < 1 || bounds.Height < 1) return;
            e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            e.Graphics.DrawImage(tiles, bounds);
            float sizeX = bounds.Width / view.Width, sizeY = bounds.Height / view.Height;
            RectangleF Cell(Point p) => new(bounds.X + p.X * sizeX, bounds.Y + p.Y * sizeY, sizeX, sizeY);
            if (view.PlayerX >= 0 && view.PlayerX < view.Width && view.PlayerY >= 0 && view.PlayerY < view.Height)
            {
                using var player = new SolidBrush(Color.DeepSkyBlue);
                e.Graphics.FillRectangle(player, Cell(new(view.PlayerX, view.PlayerY)));
            }
            if (hover is { } position) e.Graphics.DrawRectangle(Pens.White, Cell(position));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            SetHover(Hit(e.Location));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            SetHover(null);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Left && Hit(e.Location) is { } tile && view.OpenTiles[tile.Y * view.Width + tile.X])
                Selected?.Invoke(tile);
        }

        private void SetHover(Point? position)
        {
            if (hover == position) return;
            hover = position;
            Cursor = position is { } tile && view.OpenTiles[tile.Y * view.Width + tile.X] ? Cursors.Hand : Cursors.Default;
            Hovered?.Invoke(position);
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tiles.Dispose();
            base.Dispose(disposing);
        }
    }
}
