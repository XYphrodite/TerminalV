using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TerminalV;

internal static class WindowFrame
{
    private const int WmGetMinMaxInfoMessage = 0x0024;
    private const int WmNcHitTestMessage = 0x0084;
    private const int MonitorDefaultToNearest = 2;

    // HitTest values
    private const int HtClient = 1;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    private const int ResizeBorder = 6;
    private const int CaptionHeight = 36;

    private static Window? s_window;

    public static void Hook(Window window)
    {
        s_window = window;
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        };
    }

    internal static (int Width, int Height) ComputeMinTrackSize(double minWidthDip, double minHeightDip, double dpiScale)
    {
        var scale = double.IsFinite(dpiScale) && dpiScale > 0.0 ? dpiScale : 1.0;
        var w = double.IsFinite(minWidthDip) ? minWidthDip : 0.0;
        var h = double.IsFinite(minHeightDip) ? minHeightDip : 0.0;
        return (Math.Max(0, (int)Math.Round(w * scale)), Math.Max(0, (int)Math.Round(h * scale)));
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfoMessage)
        {
            WmGetMinMaxInfo(hwnd, lParam);
            handled = true;
            return IntPtr.Zero;
        }
        if (msg == WmNcHitTestMessage)
        {
            var hit = HitTest(hwnd, lParam);
            if (hit != HtClient)
            {
                handled = true;
                return (IntPtr)hit;
            }
        }

        return IntPtr.Zero;
    }

    internal static int HitTest(IntPtr hwnd, IntPtr lParam)
    {
        // lParam low/high = cursor x/y in screen coords
        var x = (short)(lParam.ToInt32() & 0xFFFF);
        var y = (short)((lParam.ToInt32() >> 16) & 0xFFFF);
        if (!GetWindowRect(hwnd, out var rect)) return HtClient;
        // Ignore maximized — no resize
        var style = GetWindowLong(hwnd, -16); // GWL_STYLE
        const int WsMaximize = 0x01000000;
        if ((style & WsMaximize) != 0) return HtClient;

        var left = rect.Left;
        var top = rect.Top;
        var right = rect.Right;
        var bottom = rect.Bottom;

        var onLeft = x - left < ResizeBorder;
        var onRight = right - x < ResizeBorder;
        var onTop = y - top < ResizeBorder;
        var onBottom = bottom - y < ResizeBorder;

        if (onTop && onLeft) return HtTopLeft;
        if (onTop && onRight) return HtTopRight;
        if (onBottom && onLeft) return HtBottomLeft;
        if (onBottom && onRight) return HtBottomRight;
        if (onLeft) return HtLeft;
        if (onRight) return HtRight;
        if (onTop) return HtTop;
        if (onBottom) return HtBottom;

        // Caption drag area (below resize border, above content) — exclude caption buttons (IsHitTestVisibleInChrome)
        // Buttons: 3 × 46 = 138px at top-right, WindowChrome needs HTCLIENT there to route to Button
        const int CaptionButtonsWidth = 138;
        var inCaptionButtons = (right - x) < CaptionButtonsWidth && (y - top) < CaptionHeight;
        if (inCaptionButtons) return HtClient;
        if (y - top < CaptionHeight && !onLeft && !onRight && !onBottom)
            return HtCaption;

        return HtClient;
    }

    private static void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var monitorInfo = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        var work = monitorInfo.rcWork;
        var display = monitorInfo.rcMonitor;
        info.ptMaxPosition.X = Math.Abs(work.Left - display.Left);
        info.ptMaxPosition.Y = Math.Abs(work.Top - display.Top);
        info.ptMaxSize.X = Math.Abs(work.Right - work.Left);
        info.ptMaxSize.Y = Math.Abs(work.Bottom - work.Top);
        // Handling WM_GETMINMAXINFO ourselves would otherwise drop the WPF
        // MinWidth/MinHeight and let the window shrink below usable layout
        // (fixed sidebar + terminal grid). Scale is read fresh on every call
        // so monitor moves keep working.
        var scale = HwndSource.FromHwnd(hwnd)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var min = ComputeMinTrackSize(s_window?.MinWidth ?? 0.0, s_window?.MinHeight ?? 0.0, scale);
        info.ptMinTrackSize.X = min.Width;
        info.ptMinTrackSize.Y = min.Height;
        Marshal.StructureToPtr(info, lParam, true);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point ptReserved;
        public Point ptMaxSize;
        public Point ptMaxPosition;
        public Point ptMinTrackSize;
        public Point ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }
}
