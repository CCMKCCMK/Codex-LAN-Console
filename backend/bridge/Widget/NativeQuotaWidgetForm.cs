using System.ComponentModel;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace CodexLanBridge;

/// <summary>
/// GDI-rendered desktop child used because WPF's redirected surface becomes transparent after
/// cross-process reparenting to Explorer on current Windows 11 builds.
/// </summary>
internal sealed class NativeQuotaWidgetForm : Forms.Form
{
    private static readonly Drawing.Color ColorKey = Drawing.Color.FromArgb(1, 2, 3);
    private static readonly Drawing.Color Healthy = Drawing.Color.FromArgb(112, 239, 188);
    private static readonly Drawing.Color Warning = Drawing.Color.FromArgb(255, 196, 92);
    private static readonly Drawing.Color Critical = Drawing.Color.FromArgb(255, 112, 120);
    private static readonly Drawing.Color Offline = Drawing.Color.FromArgb(150, 158, 156);

    private readonly QuotaWidgetViewModel _viewModel = new();
    private readonly WindowsQuotaWidgetSettingsStore _settings;
    private readonly DesktopWindowHost _desktopHost = new();
    private readonly Forms.Timer _clock = new() { Interval = 60_000 };
    private readonly Forms.Timer _desktopHeartbeat = new() { Interval = 5_000 };
    private readonly Forms.ToolTip _toolTip = new();
    private readonly Drawing.Font _windowFont = CreateFont("Microsoft YaHei UI", 10, Drawing.FontStyle.Bold);
    private readonly Drawing.Font _remainingFont = CreateFont("Segoe UI", 36, Drawing.FontStyle.Bold);
    private readonly Drawing.Font _smallFont = CreateFont("Segoe UI", 11, Drawing.FontStyle.Regular);
    private readonly Drawing.Font _etaFont = CreateFont("Microsoft YaHei UI", 12, Drawing.FontStyle.Bold);
    private readonly Drawing.Font _estimatorsFont = CreateFont("Microsoft YaHei UI", 9, Drawing.FontStyle.Regular);

    private bool _shutdownRequested;
    private bool _dragging;
    private int _dragCursorLeft;
    private int _dragCursorTop;
    private int _dragWindowLeft;
    private int _dragWindowTop;

    internal NativeQuotaWidgetForm(
        QuotaWidgetSnapshot snapshot,
        WindowsQuotaWidgetSettingsStore settings)
    {
        _settings = settings;
        _viewModel.Apply(snapshot);

        Text = "Codex Quota";
        Name = "CodexQuotaDesktopWidget";
        FormBorderStyle = Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.Manual;
        ClientSize = new Drawing.Size(176, 176);
        MinimumSize = MaximumSize = Size;
        BackColor = ColorKey;
        TransparencyKey = ColorKey;
        AutoScaleMode = Forms.AutoScaleMode.None;
        DoubleBuffered = true;
        SetStyle(
            Forms.ControlStyles.AllPaintingInWmPaint |
            Forms.ControlStyles.OptimizedDoubleBuffer |
            Forms.ControlStyles.UserPaint,
            true);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("重置位置", null, (_, _) => ResetPosition());
        ContextMenuStrip = menu;

        _viewModel.PropertyChanged += ViewModelPropertyChanged;
        _clock.Tick += (_, _) =>
        {
            _viewModel.RefreshClock();
            Invalidate();
        };
        _desktopHeartbeat.Tick += (_, _) => _desktopHost.EnsureAttached(Handle);

        MouseDown += WidgetMouseDown;
        MouseMove += WidgetMouseMove;
        MouseUp += WidgetMouseUp;
        MouseCaptureChanged += (_, _) =>
        {
            if (_dragging && !Capture) FinishDrag();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override Forms.CreateParams CreateParams
    {
        get
        {
            const int wsExToolWindow = 0x00000080;
            const int wsExNoActivate = 0x08000000;
            var parameters = base.CreateParams;
            parameters.ExStyle |= wsExToolWindow | wsExNoActivate;
            return parameters;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplySavedPosition();
        _desktopHost.Attach(Handle);
        _toolTip.SetToolTip(this, _viewModel.ToolTipText);
        _clock.Start();
        _desktopHeartbeat.Start();
        Invalidate();
    }

    protected override void OnFormClosing(Forms.FormClosingEventArgs e)
    {
        if (!_shutdownRequested)
        {
            e.Cancel = true;
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnPaintBackground(Forms.PaintEventArgs e) => e.Graphics.Clear(ColorKey);

    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.Clear(ColorKey);
        graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        var accent = AccentColor(_viewModel.RemainingPercent);
        using var basePen = new Drawing.Pen(Drawing.Color.FromArgb(88, 116, 130), 7)
        {
            StartCap = Drawing2D.LineCap.Round,
            EndCap = Drawing2D.LineCap.Round
        };
        using var accentPen = new Drawing.Pen(accent, 7)
        {
            StartCap = Drawing2D.LineCap.Round,
            EndCap = Drawing2D.LineCap.Round
        };
        var ring = new Drawing.RectangleF(22, 8, 132, 132);
        graphics.DrawEllipse(basePen, ring);
        if (_viewModel.RemainingPercent >= 100)
            graphics.DrawEllipse(accentPen, ring);
        else if (_viewModel.RemainingPercent > 0)
            graphics.DrawArc(accentPen, ring, -90, _viewModel.RemainingPercent * 3.6f);

        DrawCentered(graphics, _viewModel.WindowText, _windowFont,
            Drawing.Color.FromArgb(230, 247, 250, 249), new Drawing.RectangleF(29, 27, 118, 16));
        DrawCentered(graphics, _viewModel.RemainingText, _remainingFont,
            Drawing.Color.White, new Drawing.RectangleF(20, 40, 136, 48));
        DrawCentered(graphics, _viewModel.RateText, _smallFont,
            Drawing.Color.FromArgb(225, 247, 250, 249), new Drawing.RectangleF(20, 87, 136, 18));
        DrawCentered(graphics, _viewModel.EtaText, _etaFont,
            accent, new Drawing.RectangleF(15, 105, 146, 22));
        DrawCentered(graphics, _viewModel.EstimatorsText, _estimatorsFont,
            Drawing.Color.FromArgb(235, 247, 250, 249), new Drawing.RectangleF(5, 150, 166, 20));
    }

    internal void ApplySnapshot(QuotaWidgetSnapshot snapshot)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(() => ApplySnapshot(snapshot)); }
            catch (InvalidOperationException) { }
            return;
        }
        _viewModel.Apply(snapshot);
        Invalidate();
    }

    internal void RequestShutdown()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(RequestShutdown); }
            catch (InvalidOperationException) { }
            return;
        }
        _shutdownRequested = true;
        Close();
    }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuotaWidgetViewModel.ToolTipText))
            _toolTip.SetToolTip(this, _viewModel.ToolTipText);
        Invalidate();
    }

    private void WidgetMouseDown(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Left ||
            !DesktopWindowHost.TryGetCursorScreenPixels(out _dragCursorLeft, out _dragCursorTop) ||
            !_desktopHost.TryGetWindowScreenPixels(out _dragWindowLeft, out _dragWindowTop)) return;
        _dragging = true;
        Capture = true;
    }

    private void WidgetMouseMove(object? sender, Forms.MouseEventArgs e)
    {
        if (!_dragging || e.Button != Forms.MouseButtons.Left ||
            !DesktopWindowHost.TryGetCursorScreenPixels(out var cursorLeft, out var cursorTop)) return;
        _desktopHost.MoveToScreenPixels(
            _dragWindowLeft + cursorLeft - _dragCursorLeft,
            _dragWindowTop + cursorTop - _dragCursorTop);
    }

    private void WidgetMouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (_dragging && e.Button == Forms.MouseButtons.Left) FinishDrag();
    }

    private void FinishDrag()
    {
        _dragging = false;
        Capture = false;
        if (_desktopHost.TryGetWindowScreenPixels(out var left, out var top))
            _settings.SavePosition(left, top);
    }

    private void ResetPosition()
    {
        _settings.ResetPosition();
        var location = DefaultLocation();
        _desktopHost.MoveToScreenPixels(location.X, location.Y);
    }

    private void ApplySavedPosition()
    {
        var virtualScreen = Forms.SystemInformation.VirtualScreen;
        var settings = _settings.Get();
        var location = settings.Left is { } left && settings.Top is { } top
            ? new Drawing.Point((int)Math.Round(left), (int)Math.Round(top))
            : DefaultLocation();
        location.X = Math.Clamp(location.X, virtualScreen.Left, virtualScreen.Right - Width);
        location.Y = Math.Clamp(location.Y, virtualScreen.Top, virtualScreen.Bottom - Height);
        Location = location;
    }

    private Drawing.Point DefaultLocation()
    {
        var area = Forms.Screen.PrimaryScreen?.WorkingArea ?? Forms.SystemInformation.WorkingArea;
        return new Drawing.Point(Math.Max(area.Left, area.Right - Width - 24), area.Top + 24);
    }

    private static void DrawCentered(
        Drawing.Graphics graphics,
        string text,
        Drawing.Font font,
        Drawing.Color color,
        Drawing.RectangleF bounds)
    {
        using var brush = new Drawing.SolidBrush(color);
        using var format = new Drawing.StringFormat
        {
            Alignment = Drawing.StringAlignment.Center,
            LineAlignment = Drawing.StringAlignment.Center,
            Trimming = Drawing.StringTrimming.EllipsisCharacter,
            FormatFlags = Drawing.StringFormatFlags.NoWrap
        };
        graphics.DrawString(text, font, brush, bounds, format);
    }

    private static Drawing.Color AccentColor(int remainingPercent) => remainingPercent switch
    {
        <= 0 => Offline,
        <= 15 => Critical,
        <= 35 => Warning,
        _ => Healthy
    };

    private static Drawing.Font CreateFont(string family, float size, Drawing.FontStyle style)
    {
        try { return new Drawing.Font(family, size, style, Drawing.GraphicsUnit.Pixel); }
        catch { return new Drawing.Font(Drawing.FontFamily.GenericSansSerif, size, style, Drawing.GraphicsUnit.Pixel); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _clock.Stop();
            _desktopHeartbeat.Stop();
            _viewModel.PropertyChanged -= ViewModelPropertyChanged;
            _toolTip.Dispose();
            _clock.Dispose();
            _desktopHeartbeat.Dispose();
            _windowFont.Dispose();
            _remainingFont.Dispose();
            _smallFont.Dispose();
            _etaFont.Dispose();
            _estimatorsFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
