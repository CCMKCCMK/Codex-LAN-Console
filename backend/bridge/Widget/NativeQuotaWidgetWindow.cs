using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;

namespace CodexLanBridge;

/// <summary>
/// A true Explorer desktop child. The HWND is created with Progman/WorkerW as its parent from
/// the beginning, so Win+D cannot hide it and DWM does not lose a reparented managed surface.
/// </summary>
internal sealed class NativeQuotaWidgetWindow : IDisposable
{
    private const string ControlClassName = "CodexLanConsole.QuotaWidget.Control.1";
    private const string WidgetClassName = "CodexLanConsole.QuotaWidget.Desktop.1";

    private const uint CsHRedraw = 0x0002;
    private const uint CsVRedraw = 0x0001;
    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsClipSiblings = 0x04000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SpawnWorkerW = 0x052C;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint WmDestroy = 0x0002;
    private const uint WmPaint = 0x000F;
    private const uint WmClose = 0x0010;
    private const uint WmEraseBackground = 0x0014;
    private const uint WmContextMenu = 0x007B;
    private const uint WmNcCreate = 0x0081;
    private const uint WmNcDestroy = 0x0082;
    private const uint WmTimer = 0x0113;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLeftButtonDown = 0x0201;
    private const uint WmLeftButtonUp = 0x0202;
    private const uint WmCaptureChanged = 0x0215;
    private const uint WmApp = 0x8000;
    private const uint WmSnapshotChanged = WmApp + 1;
    private const uint WmShutdown = WmApp + 2;
    private const uint HeartbeatTimer = 1;
    private const uint ClockTimer = 2;
    private const uint MfString = 0x0000;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCommand = 0x0100;
    private const uint TpmNoNotify = 0x0080;
    private const int RgnOr = 2;
    private const uint SrcCopy = 0x00CC0020;
    private const int WidgetSize = 176;

    private static readonly IntPtr HwndMessage = new(-3);
    private static readonly IntPtr DpiAwarenessPerMonitorV2 = new(-4);
    private static readonly Drawing.Color ColorKey = Drawing.Color.FromArgb(1, 2, 3);
    private static readonly Drawing.Color Healthy = Drawing.Color.FromArgb(112, 239, 188);
    private static readonly Drawing.Color Warning = Drawing.Color.FromArgb(255, 196, 92);
    private static readonly Drawing.Color Critical = Drawing.Color.FromArgb(255, 112, 120);
    private static readonly Drawing.Color Offline = Drawing.Color.FromArgb(150, 158, 156);
    private static readonly WindowProcedure ControlWindowProcedure = StaticControlWindowProcedure;
    private static readonly WindowProcedure WidgetWindowProcedure = StaticWidgetWindowProcedure;
    private static readonly object ClassRegistrationGate = new();
    private static bool _classesRegistered;

    private readonly QuotaWidgetViewModel _viewModel = new();
    private readonly WindowsQuotaWidgetSettingsStore _settings;
    private readonly ILogger _logger;
    private readonly Drawing.Font _windowFont = CreateFont("Microsoft YaHei UI", 10, Drawing.FontStyle.Bold);
    private readonly Drawing.Font _remainingFont = CreateFont("Segoe UI", 36, Drawing.FontStyle.Bold);
    private readonly Drawing.Font _smallFont = CreateFont("Segoe UI", 11, Drawing.FontStyle.Regular);
    private readonly Drawing.Font _etaFont = CreateFont("Microsoft YaHei UI", 12, Drawing.FontStyle.Bold);
    private readonly Drawing.Font _estimatorsFont = CreateFont("Microsoft YaHei UI", 9, Drawing.FontStyle.Regular);

    private QuotaWidgetSnapshot _latestSnapshot;
    private GCHandle _selfHandle;
    private IntPtr _module;
    private IntPtr _controlWindow;
    private IntPtr _widgetWindow;
    private IntPtr _desktopHost;
    private IntPtr _renderBitmap;
    private uint _threadId;
    private bool _disposed;
    private bool _dragging;
    private int _dragCursorLeft;
    private int _dragCursorTop;
    private int _dragWindowLeft;
    private int _dragWindowTop;

    internal NativeQuotaWidgetWindow(
        QuotaWidgetSnapshot snapshot,
        WindowsQuotaWidgetSettingsStore settings,
        ILogger logger)
    {
        _latestSnapshot = snapshot;
        _settings = settings;
        _logger = logger;
        _viewModel.Apply(snapshot);
    }

    internal void Run(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _threadId = GetCurrentThreadId();
        SetThreadDpiAwarenessContext(DpiAwarenessPerMonitorV2);
        _module = GetModuleHandle(null);
        EnsureWindowClassesRegistered(_module);

        _selfHandle = GCHandle.Alloc(this);
        var context = GCHandle.ToIntPtr(_selfHandle);
        try
        {
            _controlWindow = CreateWindowEx(
                0,
                ControlClassName,
                string.Empty,
                0,
                0,
                0,
                0,
                0,
                HwndMessage,
                IntPtr.Zero,
                _module,
                context);
            if (_controlWindow == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the widget coordinator window.");

            using var cancellationRegistration = cancellationToken.Register(RequestShutdown);
            EnsureDesktopChild();
            SetTimer(_controlWindow, HeartbeatTimer, 5_000, IntPtr.Zero);
            SetTimer(_controlWindow, ClockTimer, 60_000, IntPtr.Zero);
            if (cancellationToken.IsCancellationRequested) RequestShutdown();

            while (true)
            {
                var result = GetMessage(out var message, IntPtr.Zero, 0, 0);
                if (result == 0) break;
                if (result < 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The widget message loop failed.");
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            if (_widgetWindow != IntPtr.Zero && IsWindow(_widgetWindow)) DestroyWindow(_widgetWindow);
            _widgetWindow = IntPtr.Zero;
            if (_controlWindow != IntPtr.Zero && IsWindow(_controlWindow))
            {
                KillTimer(_controlWindow, HeartbeatTimer);
                KillTimer(_controlWindow, ClockTimer);
                DestroyWindow(_controlWindow);
            }
            _controlWindow = IntPtr.Zero;
            if (_renderBitmap != IntPtr.Zero)
            {
                DeleteObject(_renderBitmap);
                _renderBitmap = IntPtr.Zero;
            }
            if (_selfHandle.IsAllocated) _selfHandle.Free();
        }
    }

    internal void ApplySnapshot(QuotaWidgetSnapshot snapshot)
    {
        Volatile.Write(ref _latestSnapshot, snapshot);
        var control = _controlWindow;
        if (control != IntPtr.Zero && IsWindow(control))
            PostMessage(control, WmSnapshotChanged, IntPtr.Zero, IntPtr.Zero);
    }

    internal void RequestShutdown()
    {
        var control = _controlWindow;
        if (control != IntPtr.Zero && IsWindow(control))
            PostMessage(control, WmShutdown, IntPtr.Zero, IntPtr.Zero);
        else if (_threadId != 0)
            PostThreadMessage(_threadId, WmClose, IntPtr.Zero, IntPtr.Zero);
    }

    private IntPtr ControlWndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmSnapshotChanged:
                _viewModel.Apply(Volatile.Read(ref _latestSnapshot));
                RebuildRendering();
                return IntPtr.Zero;
            case WmShutdown:
                PostQuitMessage(0);
                return IntPtr.Zero;
            case WmTimer:
                if (unchecked((uint)wParam.ToInt64()) == HeartbeatTimer)
                    EnsureDesktopChild();
                else if (unchecked((uint)wParam.ToInt64()) == ClockTimer)
                {
                    _viewModel.RefreshClock();
                    RebuildRendering();
                }
                return IntPtr.Zero;
            case WmDestroy:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    private IntPtr WidgetWndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmEraseBackground:
                return new IntPtr(1);
            case WmPaint:
                PaintWidget(window);
                return IntPtr.Zero;
            case WmLeftButtonDown:
                StartDrag(window);
                return IntPtr.Zero;
            case WmMouseMove:
                ContinueDrag(window, wParam);
                return IntPtr.Zero;
            case WmLeftButtonUp:
                FinishDrag(window);
                return IntPtr.Zero;
            case WmCaptureChanged:
                if (_dragging) FinishDrag(window);
                return IntPtr.Zero;
            case WmContextMenu:
                ShowContextMenu(window, lParam);
                return IntPtr.Zero;
            case WmNcDestroy:
                if (_widgetWindow == window) _widgetWindow = IntPtr.Zero;
                return DefWindowProc(window, message, wParam, lParam);
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    private void EnsureDesktopChild()
    {
        var desktop = FindDesktopHost();
        if (desktop == IntPtr.Zero || !IsWindow(desktop)) return;

        if (_widgetWindow == IntPtr.Zero || !IsWindow(_widgetWindow) || GetParent(_widgetWindow) != desktop)
        {
            if (_widgetWindow != IntPtr.Zero && IsWindow(_widgetWindow)) DestroyWindow(_widgetWindow);
            _widgetWindow = IntPtr.Zero;
            _desktopHost = desktop;
            CreateDesktopChild(desktop);
            return;
        }

        SetWindowPos(_widgetWindow, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    private void CreateDesktopChild(IntPtr desktop)
    {
        var screenPosition = SavedOrDefaultPosition();
        var clientPosition = screenPosition;
        ScreenToClient(desktop, ref clientPosition);

        var context = GCHandle.ToIntPtr(_selfHandle);
        var window = CreateWindowEx(
            WsExToolWindow | WsExNoActivate,
            WidgetClassName,
            "Codex Quota",
            WsChild | WsVisible | WsClipSiblings,
            clientPosition.X,
            clientPosition.Y,
            WidgetSize,
            WidgetSize,
            desktop,
            IntPtr.Zero,
            _module,
            context);
        if (window == IntPtr.Zero)
        {
            _logger.LogWarning("Could not create the desktop quota widget: Win32 error {Error}.", Marshal.GetLastWin32Error());
            return;
        }

        _widgetWindow = window;
        SetWindowPos(window, IntPtr.Zero, clientPosition.X, clientPosition.Y, WidgetSize, WidgetSize,
            SwpNoActivate | SwpShowWindow);
        RebuildRendering();
    }

    private NativePoint SavedOrDefaultPosition()
    {
        var settings = _settings.Get();
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        var workingArea = screen?.WorkingArea ?? System.Windows.Forms.SystemInformation.WorkingArea;
        var left = settings.Left is { } savedLeft
            ? (int)Math.Round(savedLeft)
            : Math.Max(workingArea.Left, workingArea.Right - WidgetSize - 24);
        var top = settings.Top is { } savedTop
            ? (int)Math.Round(savedTop)
            : workingArea.Top + 24;
        left = Math.Clamp(left, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - WidgetSize));
        top = Math.Clamp(top, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - WidgetSize));
        return new NativePoint(left, top);
    }

    private void RebuildRendering()
    {
        var window = _widgetWindow;
        if (window == IntPtr.Zero || !IsWindow(window)) return;

        using var bitmap = new Drawing.Bitmap(WidgetSize, WidgetSize, PixelFormat.Format32bppArgb);
        using (var graphics = Drawing.Graphics.FromImage(bitmap)) DrawWidget(graphics);

        var bitmapHandle = bitmap.GetHbitmap(ColorKey);
        var region = CreateVisiblePixelRegion(bitmap);
        if (region == IntPtr.Zero)
        {
            DeleteObject(bitmapHandle);
            return;
        }

        if (SetWindowRgn(window, region, true) == 0)
        {
            DeleteObject(region);
            DeleteObject(bitmapHandle);
            return;
        }

        var oldBitmap = _renderBitmap;
        _renderBitmap = bitmapHandle;
        if (oldBitmap != IntPtr.Zero) DeleteObject(oldBitmap);
        InvalidateRect(window, IntPtr.Zero, false);
        UpdateWindow(window);

        // Paint immediately as well as through WM_PAINT. Explorer's desktop child hierarchy can
        // defer invalidations while the icon view is idle, but a direct client-DC blit is visible
        // at once and the normal WM_PAINT path keeps it durable after later redraws.
        var deviceContext = GetDC(window);
        if (deviceContext != IntPtr.Zero)
        {
            BlitRendering(deviceContext);
            ReleaseDC(window, deviceContext);
        }
    }

    private void DrawWidget(Drawing.Graphics graphics)
    {
        graphics.Clear(ColorKey);
        graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;

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

        DrawShadowedCentered(graphics, _viewModel.WindowText, _windowFont,
            Drawing.Color.FromArgb(230, 247, 250, 249), new Drawing.RectangleF(29, 27, 118, 16));
        DrawShadowedCentered(graphics, _viewModel.RemainingText, _remainingFont,
            Drawing.Color.White, new Drawing.RectangleF(20, 40, 136, 48));
        DrawShadowedCentered(graphics, _viewModel.RateText, _smallFont,
            Drawing.Color.FromArgb(225, 247, 250, 249), new Drawing.RectangleF(20, 87, 136, 18));
        DrawShadowedCentered(graphics, _viewModel.EtaText, _etaFont,
            accent, new Drawing.RectangleF(15, 105, 146, 22));
        DrawShadowedCentered(graphics, _viewModel.EstimatorsText, _estimatorsFont,
            Drawing.Color.FromArgb(235, 247, 250, 249), new Drawing.RectangleF(5, 150, 166, 20));
    }

    private static void DrawShadowedCentered(
        Drawing.Graphics graphics,
        string text,
        Drawing.Font font,
        Drawing.Color color,
        Drawing.RectangleF bounds)
    {
        using var format = new Drawing.StringFormat
        {
            Alignment = Drawing.StringAlignment.Center,
            LineAlignment = Drawing.StringAlignment.Center,
            Trimming = Drawing.StringTrimming.EllipsisCharacter,
            FormatFlags = Drawing.StringFormatFlags.NoWrap
        };
        using var shadow = new Drawing.SolidBrush(Drawing.Color.FromArgb(14, 18, 17));
        using var brush = new Drawing.SolidBrush(color);
        var shadowBounds = bounds;
        shadowBounds.Offset(1, 1);
        graphics.DrawString(text, font, shadow, shadowBounds, format);
        graphics.DrawString(text, font, brush, bounds, format);
    }

    private static IntPtr CreateVisiblePixelRegion(Drawing.Bitmap bitmap)
    {
        var bounds = new Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowPixels = Math.Abs(data.Stride) / sizeof(int);
            var pixels = new int[rowPixels * bitmap.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            var key = ColorKey.ToArgb() & 0x00FFFFFF;
            var aggregate = CreateRectRgn(0, 0, 0, 0);
            if (aggregate == IntPtr.Zero) return IntPtr.Zero;

            for (var y = 0; y < bitmap.Height; y++)
            {
                var row = data.Stride >= 0 ? y : bitmap.Height - 1 - y;
                var offset = row * rowPixels;
                var x = 0;
                while (x < bitmap.Width)
                {
                    while (x < bitmap.Width && (pixels[offset + x] & 0x00FFFFFF) == key) x++;
                    if (x >= bitmap.Width) break;
                    var start = x;
                    while (x < bitmap.Width && (pixels[offset + x] & 0x00FFFFFF) != key) x++;
                    var run = CreateRectRgn(start, y, x, y + 1);
                    if (run == IntPtr.Zero) continue;
                    CombineRgn(aggregate, aggregate, run, RgnOr);
                    DeleteObject(run);
                }
            }
            return aggregate;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private void PaintWidget(IntPtr window)
    {
        var paint = new PaintStruct();
        var targetDc = BeginPaint(window, ref paint);
        try
        {
            if (targetDc != IntPtr.Zero) BlitRendering(targetDc);
        }
        finally
        {
            EndPaint(window, ref paint);
        }
    }

    private void BlitRendering(IntPtr targetDc)
    {
        using var graphics = Drawing.Graphics.FromHdc(targetDc);
        DrawWidget(graphics);
    }

    private void StartDrag(IntPtr window)
    {
        if (!GetCursorPos(out var cursor) || !GetWindowRect(window, out var bounds)) return;
        _dragging = true;
        _dragCursorLeft = cursor.X;
        _dragCursorTop = cursor.Y;
        _dragWindowLeft = bounds.Left;
        _dragWindowTop = bounds.Top;
        SetCapture(window);
    }

    private void ContinueDrag(IntPtr window, IntPtr wParam)
    {
        const long leftButtonMask = 0x0001;
        if (!_dragging || (wParam.ToInt64() & leftButtonMask) == 0 || !GetCursorPos(out var cursor)) return;
        MoveToScreenPixels(
            window,
            _dragWindowLeft + cursor.X - _dragCursorLeft,
            _dragWindowTop + cursor.Y - _dragCursorTop);
    }

    private void FinishDrag(IntPtr window)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseCapture();
        if (GetWindowRect(window, out var bounds)) _settings.SavePosition(bounds.Left, bounds.Top);
    }

    private void MoveToScreenPixels(IntPtr window, int left, int top)
    {
        var origin = new NativePoint(left, top);
        var parent = GetParent(window);
        if (parent != IntPtr.Zero) ScreenToClient(parent, ref origin);
        SetWindowPos(window, IntPtr.Zero, origin.X, origin.Y, 0, 0,
            SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    private void ShowContextMenu(IntPtr window, IntPtr lParam)
    {
        var point = new NativePoint(
            unchecked((short)(lParam.ToInt64() & 0xFFFF)),
            unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF)));
        if (point.X == -1 && point.Y == -1 && GetCursorPos(out var cursor)) point = cursor;
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, MfString, new UIntPtr(1), "重置位置");
            var command = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCommand | TpmNoNotify,
                point.X, point.Y, window, IntPtr.Zero);
            if (command == 1)
            {
                _settings.ResetPosition();
                var position = SavedOrDefaultPosition();
                MoveToScreenPixels(window, position.X, position.Y);
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static void EnsureWindowClassesRegistered(IntPtr module)
    {
        lock (ClassRegistrationGate)
        {
            if (_classesRegistered) return;
            RegisterWindowClass(module, ControlClassName, ControlWindowProcedure);
            RegisterWindowClass(module, WidgetClassName, WidgetWindowProcedure);
            _classesRegistered = true;
        }
    }

    private static void RegisterWindowClass(IntPtr module, string className, WindowProcedure procedure)
    {
        var definition = new WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            Style = CsHRedraw | CsVRedraw,
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(procedure),
            Instance = module,
            Cursor = LoadCursor(IntPtr.Zero, new IntPtr(32512)),
            ClassName = className
        };
        if (RegisterClassEx(ref definition) != 0) return;
        var error = Marshal.GetLastWin32Error();
        if (error != 1410) throw new Win32Exception(error, $"Could not register {className}.");
    }

    private static NativeQuotaWidgetWindow? InstanceFromWindow(IntPtr window, uint message, IntPtr lParam)
    {
        var context = GetWindowLongPtr(window, -21);
        if (message == WmNcCreate)
        {
            var creation = Marshal.PtrToStructure<CreateStruct>(lParam);
            context = creation.CreateParameters;
            SetWindowLongPtr(window, -21, context);
        }
        if (context == IntPtr.Zero) return null;
        try { return GCHandle.FromIntPtr(context).Target as NativeQuotaWidgetWindow; }
        catch { return null; }
    }

    private static IntPtr StaticControlWindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        var instance = InstanceFromWindow(window, message, lParam);
        try
        {
            var result = instance?.ControlWndProc(window, message, wParam, lParam)
                         ?? DefWindowProc(window, message, wParam, lParam);
            if (message == WmNcDestroy) SetWindowLongPtr(window, -21, IntPtr.Zero);
            return result;
        }
        catch (Exception ex)
        {
            instance?._logger.LogError(ex, "The native widget coordinator recovered from an error.");
            return DefWindowProc(window, message, wParam, lParam);
        }
    }

    private static IntPtr StaticWidgetWindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        var instance = InstanceFromWindow(window, message, lParam);
        try
        {
            var result = instance?.WidgetWndProc(window, message, wParam, lParam)
                         ?? DefWindowProc(window, message, wParam, lParam);
            if (message == WmNcDestroy) SetWindowLongPtr(window, -21, IntPtr.Zero);
            return result;
        }
        catch (Exception ex)
        {
            instance?._logger.LogError(ex, "The native desktop widget recovered from an error.");
            return DefWindowProc(window, message, wParam, lParam);
        }
    }

    private static IntPtr FindDesktopHost()
    {
        var existing = FindDesktopHostCore();
        if (existing != IntPtr.Zero) return existing;
        var progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
            SendMessageTimeout(progman, SpawnWorkerW, new IntPtr(0xD), new IntPtr(1),
                SmtoAbortIfHung, 1_000, out _);
        return FindDesktopHostCore();
    }

    private static IntPtr FindDesktopHostCore()
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            var desktopView = FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (desktopView == IntPtr.Zero) return true;
            // On current Windows 11 builds, third-party children of Progman are retained but
            // composited at near-zero opacity. A direct child of the desktop view is fully
            // rendered and still remains part of the desktop across Win+D.
            result = desktopView;
            return false;
        }, IntPtr.Zero);
        if (result != IntPtr.Zero) return result;

        var progman = FindWindow("Progman", null);
        return progman == IntPtr.Zero
            ? IntPtr.Zero
            : FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RequestShutdown();
        _windowFont.Dispose();
        _remainingFont.Dispose();
        _smallFont.Dispose();
        _etaFont.Dispose();
        _estimatorsFont.Dispose();
    }

    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr state);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        internal uint Size;
        internal uint Style;
        internal IntPtr WindowProcedure;
        internal int ClassExtra;
        internal int WindowExtra;
        internal IntPtr Instance;
        internal IntPtr Icon;
        internal IntPtr Cursor;
        internal IntPtr Background;
        internal string? MenuName;
        internal string ClassName;
        internal IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CreateStruct
    {
        internal IntPtr CreateParameters;
        internal IntPtr Instance;
        internal IntPtr Menu;
        internal IntPtr Parent;
        internal int Height;
        internal int Width;
        internal int Y;
        internal int X;
        internal int Style;
        internal IntPtr Name;
        internal IntPtr Class;
        internal uint ExtendedStyle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
        internal NativePoint(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        internal IntPtr Window;
        internal uint Message;
        internal IntPtr WParam;
        internal IntPtr LParam;
        internal uint Time;
        internal NativePoint Point;
        internal uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        internal IntPtr DeviceContext;
        internal int Erase;
        internal NativeRect Paint;
        internal int Restore;
        internal int IncrementalUpdate;
        internal int Reserved0;
        internal int Reserved1;
        internal int Reserved2;
        internal int Reserved3;
        internal int Reserved4;
        internal int Reserved5;
        internal int Reserved6;
        internal int Reserved7;
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx definition);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu,
        IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern UIntPtr SetTimer(IntPtr window, uint id, uint milliseconds, IntPtr callback);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr window, uint id);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr window, ref PaintStruct paint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr window, ref PaintStruct paint);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr window, IntPtr rectangle, bool erase);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr state);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr awarenessContext);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr window, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr value);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr value);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr target, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint operation);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr destination, IntPtr source1, IntPtr source2, int mode);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string value);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(
        IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    private static IntPtr GetWindowLongPtr(IntPtr window, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(window, index) : new IntPtr(GetWindowLong32(window, index));

    private static IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(window, index, value)
            : new IntPtr(SetWindowLong32(window, index, value.ToInt32()));
}
