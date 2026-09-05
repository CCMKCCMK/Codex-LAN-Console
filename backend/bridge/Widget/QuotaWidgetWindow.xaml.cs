using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace CodexLanBridge;

public partial class QuotaWidgetWindow : Window
{
    private const double RingCenterX = 88;
    private const double RingCenterY = 74;
    private const double RingRadius = 66;

    private readonly QuotaWidgetViewModel _viewModel;
    private readonly WindowsQuotaWidgetSettingsStore _settings;
    private readonly DesktopWindowHost _desktopHost = new();
    private bool _shutdownRequested;
    private bool _dragging;
    private int _dragCursorLeft;
    private int _dragCursorTop;
    private int _dragWindowLeft;
    private int _dragWindowTop;

    internal QuotaWidgetWindow(
        QuotaWidgetViewModel viewModel,
        WindowsQuotaWidgetSettingsStore settings)
    {
        _viewModel = viewModel;
        _settings = settings;
        DataContext = viewModel;
        InitializeComponent();

        Loaded += WindowLoaded;
        Closing += WindowClosing;
        Closed += WindowClosed;
        MouseMove += WindowMouseMove;
        MouseLeftButtonUp += WindowMouseLeftButtonUp;
        LostMouseCapture += WindowLostMouseCapture;
        _viewModel.PropertyChanged += ViewModelPropertyChanged;
    }

    private void WindowLoaded(object sender, RoutedEventArgs e)
    {
        ApplySavedPosition();
        _desktopHost.Attach(this);
        UpdateProgressArc(_viewModel.RemainingPercent);
    }

    private void WindowClosed(object? sender, EventArgs e) =>
        _viewModel.PropertyChanged -= ViewModelPropertyChanged;

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        // The widget is intentionally persistent. Only the bridge shutdown path may close it.
        if (!_shutdownRequested) e.Cancel = true;
    }

    internal void RequestShutdown()
    {
        _shutdownRequested = true;
        Close();
    }

    internal void EnsureDesktopAttachment() => _desktopHost.EnsureAttached(this);

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuotaWidgetViewModel.RemainingPercent))
            UpdateProgressArc(_viewModel.RemainingPercent);
    }

    private void WindowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (!DesktopWindowHost.TryGetCursorScreenPixels(out _dragCursorLeft, out _dragCursorTop) ||
            !_desktopHost.TryGetWindowScreenPixels(out _dragWindowLeft, out _dragWindowTop)) return;
        _dragging = true;
        CaptureMouse();
        e.Handled = true;
    }

    private void WindowMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        if (!DesktopWindowHost.TryGetCursorScreenPixels(out var cursorLeft, out var cursorTop)) return;
        _desktopHost.MoveToScreenPixels(
            _dragWindowLeft + cursorLeft - _dragCursorLeft,
            _dragWindowTop + cursorTop - _dragCursorTop);
    }

    private void WindowMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        FinishDrag();
        e.Handled = true;
    }

    private void WindowLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_dragging) FinishDrag();
    }

    private void FinishDrag()
    {
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (_desktopHost.TryGetScreenPositionDips(this, out var left, out var top))
            _settings.SavePosition(left, top);
    }

    private void ResetPositionClick(object sender, RoutedEventArgs e)
    {
        _settings.ResetPosition();
        ApplyDefaultPosition();
    }

    private void ApplySavedPosition()
    {
        var settings = _settings.Get();
        if (settings.Left is not { } left || settings.Top is not { } top)
        {
            ApplyDefaultPosition();
            return;
        }

        Left = Clamp(left, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft +
            SystemParameters.VirtualScreenWidth - Width);
        Top = Clamp(top, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop +
            SystemParameters.VirtualScreenHeight - Height);
        if (IsLoaded) _desktopHost.MoveToScreenDips(this, Left, Top);
    }

    private void ApplyDefaultPosition()
    {
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left, workArea.Right - Width - 24);
        Top = workArea.Top + 24;
        if (IsLoaded) _desktopHost.MoveToScreenDips(this, Left, Top);
    }

    private void UpdateProgressArc(int percent)
    {
        var value = Math.Clamp(percent, 0, 100);
        if (value <= 0)
        {
            CompleteRing.Visibility = Visibility.Collapsed;
            ProgressPath.Data = null;
            return;
        }

        if (value >= 100)
        {
            ProgressPath.Data = null;
            CompleteRing.Visibility = Visibility.Visible;
            return;
        }

        CompleteRing.Visibility = Visibility.Collapsed;
        var angle = value / 100d * 360d;
        var start = new Point(RingCenterX, RingCenterY - RingRadius);
        var radians = (angle - 90d) * Math.PI / 180d;
        var end = new Point(
            RingCenterX + RingRadius * Math.Cos(radians),
            RingCenterY + RingRadius * Math.Sin(radians));
        var segment = new ArcSegment(
            end,
            new Size(RingRadius, RingRadius),
            0,
            angle > 180d,
            SweepDirection.Clockwise,
            true);
        var figure = new PathFigure(start, new PathSegment[] { segment }, false);
        ProgressPath.Data = new PathGeometry(new[] { figure });
    }

    private static double Clamp(double value, double minimum, double maximum) =>
        maximum < minimum ? minimum : Math.Clamp(value, minimum, maximum);
}
