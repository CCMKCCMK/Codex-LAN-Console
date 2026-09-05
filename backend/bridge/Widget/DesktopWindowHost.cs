using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Point = System.Windows.Point;

namespace CodexLanBridge;

/// <summary>
/// Hosts the widget as a real Explorer desktop child. WPF per-pixel transparency stops rendering
/// after cross-process reparenting on current Windows 11 builds, so this host uses native color-key
/// transparency with a normally rendered WPF child HWND.
/// </summary>
internal sealed class DesktopWindowHost
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const uint WsChild = 0x40000000;
    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExAppWindow = 0x00040000;
    private const uint WsExNoActivate = 0x08000000;
    private const uint WsExLayered = 0x00080000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const uint SpawnWorkerW = 0x052C;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint LwaColorKey = 0x00000001;
    private const uint TransparentColorKey = 0x00030201;

    private IntPtr _window;

    internal bool Attach(Window window) => Attach(new WindowInteropHelper(window).Handle);

    internal bool Attach(IntPtr window)
    {
        _window = window;
        if (_window == IntPtr.Zero || !IsWindow(_window)) return false;

        var desktop = FindDesktopHost();
        if (desktop == IntPtr.Zero || !IsWindow(desktop)) return false;

        if (GetParent(_window) == desktop)
        {
            SetLayeredWindowAttributes(_window, TransparentColorKey, 255, LwaColorKey);
            SetWindowPos(_window, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
            return true;
        }

        if (!GetWindowRect(_window, out var bounds)) return false;

        var style = unchecked((uint)GetWindowLongPtr(_window, GwlStyle).ToInt64());
        style = (style & ~WsPopup) | WsChild;
        SetWindowLongPtr(_window, GwlStyle, new IntPtr(unchecked((int)style)));

        var extendedStyle = unchecked((uint)GetWindowLongPtr(_window, GwlExStyle).ToInt64());
        extendedStyle = (extendedStyle & ~WsExAppWindow) |
                        WsExToolWindow | WsExNoActivate | WsExLayered;
        SetWindowLongPtr(_window, GwlExStyle, new IntPtr(unchecked((int)extendedStyle)));

        SetParent(_window, desktop);
        if (GetParent(_window) != desktop) return false;
        if (!SetLayeredWindowAttributes(_window, TransparentColorKey, 255, LwaColorKey)) return false;

        var origin = new NativePoint(bounds.Left, bounds.Top);
        ScreenToClient(desktop, ref origin);
        return SetWindowPos(
            _window,
            IntPtr.Zero,
            origin.X,
            origin.Y,
            Math.Max(1, bounds.Right - bounds.Left),
            Math.Max(1, bounds.Bottom - bounds.Top),
            SwpNoActivate | SwpFrameChanged | SwpShowWindow);
    }

    internal bool EnsureAttached(Window window) =>
        EnsureAttached(new WindowInteropHelper(window).Handle);

    internal bool EnsureAttached(IntPtr window)
    {
        if (_window == IntPtr.Zero || !IsWindow(_window))
            _window = window;
        if (_window == IntPtr.Zero || !IsWindow(_window)) return false;

        var currentDesktop = FindDesktopHost();
        if (currentDesktop == IntPtr.Zero) return false;
        if (GetParent(_window) != currentDesktop) return Attach(window);

        SetWindowPos(_window, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        return true;
    }

    internal bool MoveToScreenPixels(int left, int top)
    {
        if (_window == IntPtr.Zero || !IsWindow(_window)) return false;
        var origin = new NativePoint(left, top);
        var parent = GetParent(_window);
        if (parent != IntPtr.Zero) ScreenToClient(parent, ref origin);
        return SetWindowPos(_window, IntPtr.Zero, origin.X, origin.Y, 0, 0,
            SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    internal bool MoveToScreenDips(Window window, double left, double top)
    {
        var source = PresentationSource.FromVisual(window);
        var transform = source?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
        var pixels = transform.Transform(new Point(left, top));
        return MoveToScreenPixels((int)Math.Round(pixels.X), (int)Math.Round(pixels.Y));
    }

    internal bool TryGetScreenPositionDips(Window window, out double left, out double top)
    {
        left = top = 0;
        if (_window == IntPtr.Zero || !GetWindowRect(_window, out var bounds)) return false;
        var source = PresentationSource.FromVisual(window);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var dips = transform.Transform(new Point(bounds.Left, bounds.Top));
        left = dips.X;
        top = dips.Y;
        return true;
    }

    internal bool TryGetWindowScreenPixels(out int left, out int top)
    {
        left = top = 0;
        if (_window == IntPtr.Zero || !GetWindowRect(_window, out var bounds)) return false;
        left = bounds.Left;
        top = bounds.Top;
        return true;
    }

    internal static bool TryGetCursorScreenPixels(out int left, out int top)
    {
        if (GetCursorPos(out var point))
        {
            left = point.X;
            top = point.Y;
            return true;
        }
        left = top = 0;
        return false;
    }

    private static IntPtr FindDesktopHost()
    {
        var existing = FindDesktopHostCore();
        if (existing != IntPtr.Zero) return existing;

        // This undocumented shell message is only a fallback for an Explorer state that has
        // not created its WorkerW hierarchy yet. Normal health checks never send it.
        var progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
            SendMessageTimeout(progman, SpawnWorkerW, new IntPtr(0xD), new IntPtr(1),
                SmtoAbortIfHung, 1000, out _);
        return FindDesktopHostCore();
    }

    private static IntPtr FindDesktopHostCore()
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            if (FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero) return true;
            result = window;
            return false;
        }, IntPtr.Zero);

        if (result != IntPtr.Zero) return result;
        var progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero && FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            return progman;
        return IntPtr.Zero;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr state);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;

        internal NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr state);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(
        IntPtr window,
        uint colorKey,
        byte alpha,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr window, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    private static IntPtr GetWindowLongPtr(IntPtr window, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(window, index) : new IntPtr(GetWindowLong32(window, index));

    private static IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(window, index, value)
            : new IntPtr(SetWindowLong32(window, index, value.ToInt32()));
}
