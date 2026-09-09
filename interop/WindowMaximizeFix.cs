using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MozartBrowser.Interop
{
    /// <summary>
    /// Fixes the classic WindowChrome + WindowStyle="None" + Maximized bug.
    ///
    /// Without this, WPF simply stretches a borderless maximized window to the
    /// *full monitor bounds*, which overlaps the taskbar and overflows past the
    /// screen edges by the amount the OS silently reserves for window borders —
    /// that's what produced the black gaps above the toolbar, the sliver on the
    /// left edge, and owned dialogs (Settings, Customize Background) rendering
    /// in the wrong place. A previous attempt patched this by adding a margin
    /// to the content (SystemParameters.WindowResizeBorderThickness +
    /// WindowNonClientFrameThickness), which is a known-fragile technique —
    /// it over-compensates on many DPI/monitor configurations, which is exactly
    /// what caused the black gaps to appear in the first place.
    ///
    /// This is the actually-correct, standard fix: hook WM_GETMINMAXINFO — the
    /// same Windows message native maximized windows use — so the OS itself
    /// sizes/positions the window to exactly the monitor's *work area* (screen
    /// minus taskbar). With this in place, no margin or padding hack is needed
    /// anywhere in XAML or code-behind; RootGrid.Margin can just stay 0 always.
    ///
    /// Usage: call WindowMaximizeFix.Apply(this) once from any
    /// WindowStyle="None" + WindowChrome window's constructor, right after
    /// InitializeComponent().
    ///
    /// NOTE: true F11-style full screen (covering the taskbar too) intentionally
    /// does NOT go through this class's Maximized handling — see
    /// MainWindow.ToggleFullScreen(). Windows still treats a truly-Maximized
    /// window as "maximized" at the shell/DWM level even when it's borderless,
    /// which on Windows 11 can draw its own hover-triggered restore affordance
    /// at the top edge — not something this app renders. Full screen instead
    /// keeps WindowState.Normal and manually sizes the window to GetMonitorBoundsInDips()
    /// below, so the OS never considers it "maximized" at all.
    /// </summary>
    public static class WindowMaximizeFix
    {
        public static void Apply(Window window)
        {
            window.SourceInitialized += (_, _) =>
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (HwndSource.FromHwnd(handle) is { } hwndSource)
                    hwndSource.AddHook(WindowProc);
            };
        }

        private const int WM_GETMINMAXINFO = 0x0024;
        private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

        private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                ApplyWorkAreaToMinMaxInfo(hwnd, lParam);
                handled = true;
            }
            return IntPtr.Zero;
        }

        private static void ApplyWorkAreaToMinMaxInfo(IntPtr hwnd, IntPtr lParam)
        {
            var minMaxInfo = Marshal.PtrToStructure<MINMAXINFO>(lParam);

            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                GetMonitorInfo(monitor, ref monitorInfo);

                var workArea = monitorInfo.rcWork;
                var monitorArea = monitorInfo.rcMonitor;

                // Position is relative to the monitor's own top-left, not the
                // virtual desktop's — this is what makes multi-monitor setups
                // maximize onto the correct screen instead of monitor 0.
                minMaxInfo.ptMaxPosition.X = workArea.Left - monitorArea.Left;
                minMaxInfo.ptMaxPosition.Y = workArea.Top - monitorArea.Top;
                minMaxInfo.ptMaxSize.X = workArea.Right - workArea.Left;
                minMaxInfo.ptMaxSize.Y = workArea.Bottom - workArea.Top;
            }

            Marshal.StructureToPtr(minMaxInfo, lParam, true);
        }

        /// <summary>
        /// Returns the *full* bounds (taskbar included) of whichever monitor the
        /// window currently sits on, converted from physical pixels to the
        /// device-independent units WPF's Left/Top/Width/Height expect — correct
        /// even on high-DPI or mixed-DPI multi-monitor setups. Used by
        /// MainWindow.ToggleFullScreen() to manually size the window for true
        /// full screen without ever touching WindowState.Maximized. Returns
        /// Rect.Empty if the monitor can't be resolved (shouldn't normally happen).
        /// </summary>
        public static Rect GetMonitorBoundsInDips(Window window)
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return Rect.Empty;

            var monitor = MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return Rect.Empty;

            var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(monitor, ref monitorInfo)) return Rect.Empty;

            var rc = monitorInfo.rcMonitor;
            var dpi = VisualTreeHelper.GetDpi(window);

            return new Rect(
                rc.Left / dpi.DpiScaleX,
                rc.Top / dpi.DpiScaleY,
                (rc.Right - rc.Left) / dpi.DpiScaleX,
                (rc.Bottom - rc.Top) / dpi.DpiScaleY);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }
    }
}
