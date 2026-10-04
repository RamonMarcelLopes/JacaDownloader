using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace JacaDownloader;

// Borderless main window: hosts the interface in an embedded WebView2 and exposes its window controls.
public class MainForm : Form
{
    const int ResizeBorder = 6;
    const int WM_NCCALCSIZE = 0x83;
    const int WM_NCHITTEST = 0x84;
    const int WM_NCPAINT = 0x85;
    const int WM_NCACTIVATE = 0x86;
    const int DWMWA_NCRENDERING_POLICY = 2;
    const int DWMNCRP_DISABLED = 1;
    const int WM_MOVING = 0x216;
    const int WS_MINIMIZEBOX = 0x20000;
    const int WS_MAXIMIZEBOX = 0x10000;
    const int WS_THICKFRAME = 0x40000;

    static readonly Size StartSize = new(1020, 860);
    static readonly string StatePath = Path.Combine(Tools.AppDataDir, "window.json");
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // The frame blends with the page background; its side and bottom strips are the grab area for resizing.
    static readonly Color DarkFrame = Color.FromArgb(9, 12, 13);
    static readonly Color LightFrame = Color.FromArgb(243, 246, 242);

    readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(9, 12, 13) };
    readonly string url;
    readonly bool startMaximized;
    FormWindowState lastState = FormWindowState.Normal;
    Rectangle normalBounds;
    DateTime restoreGuardUntil;

    record SavedWindow(int X, int Y, int Width, int Height, bool Maximized);

    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }

    public MainForm(string url)
    {
        this.url = url;
        Text = "Jaca Downloader";
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(4, 0, 4, 4);
        BackColor = DarkFrame;
        MinimumSize = new Size(480, 600);
        using (var icon = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico")!)
            Icon = new Icon(icon);
        Controls.Add(web);

        var saved = LoadState();
        StartPosition = FormStartPosition.Manual;
        Bounds = InitialBounds(saved);
        normalBounds = Bounds;
        startMaximized = saved?.Maximized ?? false;

        Program.PickFolder = PickFolder;
        Load += async (_, _) => await InitAsync();
        ResizeEnd += (_, _) => { EnsureVisible(); SaveState(); };
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        FormClosing += (_, _) => SaveState();
        FormClosed += (_, _) => SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }

    // A borderless window needs the thick-frame and box styles to get Aero Snap and Win + arrow keys.
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= WS_THICKFRAME | WS_MAXIMIZEBOX | WS_MINIMIZEBOX;
            return cp;
        }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // Turns off the desktop compositor's frame rendering (shadow and light border) for this window.
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var policy = DWMNCRP_DISABLED;
        DwmSetWindowAttribute(Handle, DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (startMaximized) WindowState = FormWindowState.Maximized;
    }

    // Keeps the maximized window inside the work area (above the taskbar) and
    // makes the web view paint again after the window is restored from the taskbar.
    protected override void OnResize(EventArgs e)
    {
        MaximizedBounds = Screen.FromControl(this).WorkingArea;
        base.OnResize(e);

        var unminimized = lastState == FormWindowState.Minimized && WindowState != FormWindowState.Minimized;
        var unmaximized = lastState == FormWindowState.Maximized && WindowState == FormWindowState.Normal;
        lastState = WindowState;

        // The thick-frame style makes Windows grow the restored window by a few pixels right after
        // un-maximizing, so the pre-maximize size is enforced for a moment instead of being recorded.
        if (unmaximized) restoreGuardUntil = DateTime.UtcNow.AddMilliseconds(800);
        if (WindowState == FormWindowState.Normal)
        {
            if (DateTime.UtcNow < restoreGuardUntil)
            {
                if (Bounds != normalBounds)
                    BeginInvoke(() => { if (WindowState == FormWindowState.Normal) Bounds = normalBounds; });
            }
            else
            {
                normalBounds = Bounds;
            }
        }

        if (unminimized)
        {
            BeginInvoke(() =>
            {
                web.Visible = false;
                web.Visible = true;
            });
        }
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (WindowState == FormWindowState.Normal && lastState == FormWindowState.Normal && DateTime.UtcNow >= restoreGuardUntil)
            normalBounds = Bounds;
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            // The whole window rectangle is client area; the system frame stays invisible.
            case WM_NCCALCSIZE when m.WParam != IntPtr.Zero:
                m.Result = IntPtr.Zero;
                return;

            // Never let Windows paint its own frame (white strips and shadow) when focus changes.
            case WM_NCPAINT:
                m.Result = IntPtr.Zero;
                return;
            case WM_NCACTIVATE:
                m.LParam = (IntPtr)(-1);
                break;

            // Lets the user resize the borderless window from its edges.
            case WM_NCHITTEST when WindowState == FormWindowState.Normal:
                var hit = HitTestEdges(m.LParam);
                if (hit != 0)
                {
                    m.Result = hit;
                    return;
                }
                break;

            // While dragging, the window can never leave the screen under the cursor.
            case WM_MOVING:
                var rect = Marshal.PtrToStructure<Rect>(m.LParam);
                var area = Screen.FromPoint(Cursor.Position).WorkingArea;
                var width = rect.Right - rect.Left;
                var height = rect.Bottom - rect.Top;
                var x = Math.Max(area.Left, Math.Min(rect.Left, area.Right - width));
                var y = Math.Max(area.Top, Math.Min(rect.Top, area.Bottom - height));
                Marshal.StructureToPtr(new Rect { Left = x, Top = y, Right = x + width, Bottom = y + height }, m.LParam, false);
                m.Result = (IntPtr)1;
                return;
        }
        base.WndProc(ref m);
    }

    IntPtr HitTestEdges(IntPtr lParam)
    {
        var p = PointToClient(new Point((short)(lParam.ToInt64() & 0xFFFF), (short)((lParam.ToInt64() >> 16) & 0xFFFF)));
        var left = p.X < ResizeBorder;
        var right = p.X >= ClientSize.Width - ResizeBorder;
        var top = p.Y < ResizeBorder;
        var bottom = p.Y >= ClientSize.Height - ResizeBorder;

        return (IntPtr)((top, bottom, left, right) switch
        {
            (true, _, true, _) => 13,
            (true, _, _, true) => 14,
            (_, true, true, _) => 16,
            (_, true, _, true) => 17,
            (true, _, _, _) => 12,
            (_, true, _, _) => 15,
            (_, _, true, _) => 10,
            (_, _, _, true) => 11,
            _ => 0
        });
    }

    // Keeps the window fully inside one screen and shrinks it if that screen is smaller.
    static Rectangle Fit(Rectangle rect, Rectangle area)
    {
        var width = Math.Min(rect.Width, area.Width);
        var height = Math.Min(rect.Height, area.Height);
        var x = Math.Max(area.Left, Math.Min(rect.X, area.Right - width));
        var y = Math.Max(area.Top, Math.Min(rect.Y, area.Bottom - height));
        return new Rectangle(x, y, width, height);
    }

    // Opens where the window was last left, unless that place no longer exists on any screen.
    static Rectangle InitialBounds(SavedWindow? saved)
    {
        if (saved != null)
        {
            var rect = new Rectangle(saved.X, saved.Y, saved.Width, saved.Height);
            Screen? best = null;
            long bestArea = 0;
            foreach (var screen in Screen.AllScreens)
            {
                var overlap = Rectangle.Intersect(rect, screen.WorkingArea);
                var overlapArea = (long)overlap.Width * overlap.Height;
                if (overlapArea > bestArea) { bestArea = overlapArea; best = screen; }
            }
            if (best != null && bestArea > 0)
                return Fit(rect, best.WorkingArea);
        }

        var area = Screen.PrimaryScreen!.WorkingArea;
        var size = new Size(Math.Min(StartSize.Width, area.Width), Math.Min(StartSize.Height, area.Height));
        return new Rectangle(
            area.Left + (area.Width - size.Width) / 2,
            area.Top + (area.Height - size.Height) / 2,
            size.Width, size.Height);
    }

    void EnsureVisible()
    {
        if (WindowState != FormWindowState.Normal) return;
        var fitted = Fit(Bounds, Screen.FromRectangle(Bounds).WorkingArea);
        if (fitted != Bounds) Bounds = fitted;
    }

    // A monitor was plugged, unplugged or rearranged: pull the window back onto a real screen.
    void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (IsHandleCreated) BeginInvoke(EnsureVisible);
    }

    static SavedWindow? LoadState()
    {
        try { return File.Exists(StatePath) ? JsonSerializer.Deserialize<SavedWindow>(File.ReadAllText(StatePath), Json) : null; }
        catch { return null; }
    }

    void SaveState()
    {
        try
        {
            var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            var state = new SavedWindow(bounds.X, bounds.Y, bounds.Width, bounds.Height, WindowState == FormWindowState.Maximized);
            Directory.CreateDirectory(Tools.AppDataDir);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state, Json));
        }
        catch { }
    }

    async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Tools.AppDataDir, "webview"));
            await web.EnsureCoreWebView2Async(env);

            var settings = web.CoreWebView2.Settings;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.IsNonClientRegionSupportEnabled = true;

            web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            web.CoreWebView2.WebMessageReceived += (_, e) => OnWebMessage(e.TryGetWebMessageAsString());
            web.Source = new Uri(url);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Não foi possível iniciar a interface. Instale o Microsoft Edge WebView2 Runtime e tente de novo.\n\n" + ex.Message,
                "Jaca Downloader", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    void OnWebMessage(string message)
    {
        switch (message)
        {
            case "minimize": WindowState = FormWindowState.Minimized; break;
            case "maximize": WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; break;
            case "close": Close(); break;
            case "theme:light": BackColor = LightFrame; break;
            case "theme:dark": BackColor = DarkFrame; break;
        }
    }

    string? PickFolder(string? initial)
    {
        return (string?)Invoke(() =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Escolha a pasta para salvar os arquivos",
                UseDescriptionForTitle = true,
                InitialDirectory = !string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial) ? initial : ""
            };
            return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
        });
    }
}
