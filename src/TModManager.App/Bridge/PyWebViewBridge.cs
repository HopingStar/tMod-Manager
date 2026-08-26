using System.Runtime.InteropServices;
using System.Windows;
using TModManager.App.Native;

namespace TModManager.App.Bridge;

/// <summary>
/// WebView2 Host Object（ComVisible），暴露给前端的 window.pywebview.api 桥。
/// 与 Python 版 js_api=_WindowApi() 等价：start_drag / minimize / toggle_fullscreen / is_fullscreen / shutdown。
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDual)]
public sealed class PyWebViewBridge
{
    private readonly Window _window;

    public PyWebViewBridge(Window window) => _window = window;

    public void start_drag()
        => _window.Dispatcher.BeginInvoke(() => WindowChromeService.StartDrag(_window));

    public void minimize()
        => _window.Dispatcher.BeginInvoke(() => _window.WindowState = WindowState.Minimized);

    public int toggle_fullscreen()
    {
        var r = 0;
        _window.Dispatcher.Invoke(() => r = WindowChromeService.ToggleWorkAreaMaximize(_window) ? 1 : 0);
        return r;
    }

    public int is_fullscreen()
    {
        var r = 0;
        _window.Dispatcher.Invoke(() => r = WindowChromeService.IsMaximized(_window) ? 1 : 0);
        return r;
    }

    public void shutdown()
        => _window.Dispatcher.BeginInvoke(() => Application.Current?.Shutdown());
}
