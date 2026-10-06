using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace JacaDownloader;

// Loading screen shown over the web view: the logo, large and centered, pulsing until the interface is ready.
public class Splash : Control
{
    const double PulsePeriodSeconds = 1.6;
    const float LogoFraction = 0.42f;
    const double BarPeriodSeconds = 1.3;
    const float BarHeight = 4f;

    readonly Image logo;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 16 };
    readonly DateTime started = DateTime.UtcNow;

    public Splash(Color background)
    {
        BackColor = background;
        Dock = DockStyle.Fill;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        using var stream = typeof(Splash).Assembly.GetManifestResourceStream("logo.png")!;
        logo = Image.FromStream(stream);
        timer.Tick += (_, _) => Invalidate();
        timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        // 0..1 eased wave: the logo breathes in size and opacity.
        var t = (DateTime.UtcNow - started).TotalSeconds / PulsePeriodSeconds * 2 * Math.PI;
        var wave = (float)((1 - Math.Cos(t)) / 2);
        var scale = 0.9f + 0.1f * wave;
        var opacity = 0.55f + 0.45f * wave;

        var side = Math.Min(Width, Height) * LogoFraction * scale;
        var rect = new RectangleF((Width - side) / 2, (Height - side) / 2, side, side);

        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = opacity });
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(logo, Rectangle.Round(rect), 0, 0, logo.Width, logo.Height, GraphicsUnit.Pixel, attributes);

        DrawLoadingBar(g);
    }

    // Indeterminate bar near the bottom: a short segment sliding left to right on a faint track.
    void DrawLoadingBar(Graphics g)
    {
        var width = Math.Min(Width * 0.4f, 280f);
        var track = new RectangleF((Width - width) / 2, Height * 0.82f, width, BarHeight);
        var progress = (float)((DateTime.UtcNow - started).TotalSeconds % BarPeriodSeconds / BarPeriodSeconds);
        var segment = width * 0.35f;
        var x = track.Left - segment + (width + segment) * progress;
        var left = Math.Max(x, track.Left);
        var right = Math.Min(x + segment, track.Right);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Rounded(track))
        using (var brush = new SolidBrush(Color.FromArgb(40, 255, 255, 255)))
            g.FillPath(brush, path);
        if (right <= left) return;
        using var fill = Rounded(new RectangleF(left, track.Top, right - left, BarHeight));
        using var accent = new SolidBrush(Color.FromArgb(230, 255, 255, 255));
        g.FillPath(accent, fill);
    }

    static GraphicsPath Rounded(RectangleF r)
    {
        var d = Math.Min(r.Height, r.Width);
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 180);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            logo.Dispose();
        }
        base.Dispose(disposing);
    }
}
