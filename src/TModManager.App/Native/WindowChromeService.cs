using System.Windows;
using System.Windows.Interop;

namespace TModManager.App.Native;

/// <summary>自绘标题栏的窗口行为：原生拖动 / 工作区最大化切换 / 是否最大化判定。</summary>
internal static class WindowChromeService
{
    // 进入工作区最大化前的窗口位置大小；null 表示当前未处于工作区最大化（对齐 Python max_state['saved']）
    private static NativeMethods.RECT? _saved;

    /// <summary>全屏（工作区最大化）状态变化回调（参数 = 是否全屏），在 UI 线程触发，供宿主同步 WebView2 边距等视觉。</summary>
    public static Action<bool>? MaximizedStateChanged;

    /// <summary>发起原生拖动（WM_NCLBUTTONDOWN + HTCAPTION）。全屏拖动时先退出全屏并让窗口跟手。</summary>
    public static void StartDrag(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (IsMaximized(window))
        {
            // 全屏拖动：退出全屏并还原到鼠标位置，再平滑衔接拖动（不拦截 WM_NCHITTEST，缩放保持 WindowChrome 标准）
            NativeMethods.GetCursorPos(out var pt);
            RestoreMaximizedToCursor(window, pt.X, pt.Y);
        }
        NativeMethods.ReleaseCapture();
        _ = NativeMethods.SendMessage(hwnd, NativeMethods.WM_NCLBUTTONDOWN, (IntPtr)NativeMethods.HTCAPTION, IntPtr.Zero);
    }

    /// <summary>是否处于「工作区最大化」状态（以 saved 状态为准，与 Python is_fullscreen 一致）。</summary>
    public static bool IsMaximized(Window window) => _saved is not null;

    /// <summary>切换工作区最大化（保存原 rect → 铺满工作区）/ 还原（SetWindowPos 恢复原 rect）。返回切换后是否最大化。</summary>
    public static bool ToggleWorkAreaMaximize(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;

        if (_saved is { } saved)
        {
            // 还原到最大化前的位置大小
            _ = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, saved.Left, saved.Top,
                saved.Right - saved.Left, saved.Bottom - saved.Top,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            _saved = null;
            MaximizedStateChanged?.Invoke(false);
            return false;
        }

        // 记录当前窗口位置大小，再铺满工作区（SPI_GETWORKAREA，不含任务栏）
        NativeMethods.GetWindowRect(hwnd, out var r);
        _saved = r;
        var wa = GetWorkArea();
        _ = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, wa.Left, wa.Top,
            wa.Right - wa.Left, wa.Bottom - wa.Top,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        MaximizedStateChanged?.Invoke(true);
        return true;
    }

    /// <summary>退出全屏并让窗口跟手：以鼠标位置为中心还原窗口（标题栏落在鼠标附近，位置钳制在工作区内），随后可平滑衔接拖动。</summary>
    public static void RestoreMaximizedToCursor(Window window, int cursorX, int cursorY)
    {
        if (_saved is not { } saved) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        int w = saved.Right - saved.Left, h = saved.Bottom - saved.Top;
        var wa = GetWorkArea();
        int left = Math.Clamp(cursorX - w / 2, wa.Left, wa.Right - w);
        int top = Math.Clamp(cursorY - Math.Min(20, h / 2), wa.Top, wa.Bottom - h);
        _ = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, left, top, w, h,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        _saved = null;
        MaximizedStateChanged?.Invoke(false);
    }

    private static NativeMethods.RECT GetWorkArea()
    {
        var rect = new NativeMethods.RECT();
        _ = NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETWORKAREA, 0, ref rect, 0);
        return rect;
    }
}
