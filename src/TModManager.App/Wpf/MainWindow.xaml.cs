// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using TModManager.App.Bridge;
using TModManager.App.Hosting;
using TModManager.App.Native;
using TModManager.Core.Configuration;
using TModManager.Core.Models;
using TModManager.Core.Storage;

namespace TModManager.App.Wpf;

public partial class MainWindow : Window
{
    private readonly AppPaths _paths;
    private readonly int _port;
    private readonly PyWebViewBridge _bridge;
    private readonly JsonStore _store;
    private readonly DispatcherTimer _sizeSaveTimer;
    private bool _shown;
    private HwndSource? _source;

    public MainWindow(AppPaths paths, int port, WindowControlService windowControl, WindowSizeInfo? savedSize)
    {
        InitializeComponent();
        _paths = paths;
        _port = port;
        _store = new JsonStore(paths);
        _bridge = new PyWebViewBridge(this);
        windowControl.RegisterWindow(EnsureShown, HideForGame);

        // 全屏（工作区最大化）时 WebView2 顶到窗口边缘（界面贴满屏幕、无背景板、禁缩放）；
        // 还原后恢复 4px 内缩作为边缘缩放命中区。回调在 UI 线程触发。
        WindowChromeService.MaximizedStateChanged = isFull =>
            webView.Margin = isFull ? new Thickness(0) : new Thickness(4);

        // 恢复上次保存的缩放尺寸（DIP，最小不得低于 MinWidth/MinHeight）
        if (savedSize is { Width: > 200, Height: > 150 })
        {
            Width = Math.Max(savedSize.Width, MinWidth);
            Height = Math.Max(savedSize.Height, MinHeight);
        }

        // 缩放尺寸持久化：SizeChanged 后 500ms 去抖写入 data.json（全屏铺满工作区时不保存）
        _sizeSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _sizeSaveTimer.Tick += async (_, _) =>
        {
            _sizeSaveTimer.Stop();
            await SaveWindowSizeAsync();
        };
        SizeChanged += (_, _) =>
        {
            if (WindowChromeService.IsMaximized(this)) return;
            _sizeSaveTimer.Stop();
            _sizeSaveTimer.Start();
        };

        Loaded += async (_, _) =>
        {
            AttachWndProcHook();
            await InitWebViewAsync();
        };

        // 兜底显示（前端加载异常时也能出窗）
        var fallback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        fallback.Tick += (_, _) => { EnsureShown(); fallback.Stop(); };
        fallback.Start();
    }

    private void AttachWndProcHook()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
    }

    /// <summary>
    /// 窗口消息钩子。边缘缩放已由「WebView2 内缩 6px + WindowChrome 原生 resize」可靠处理
    /// （WebView2 的 Chrome_WidgetWin 子窗口铺满整窗时会吞掉边缘 WM_NCHITTEST 导致缩放失效，
    /// 内缩后边缘 6px 是纯 WPF 区域，命中直达顶层窗口）。
    /// 本钩子负责：全屏（工作区最大化）时强制 HTCLIENT 禁缩放；WM_SETCURSOR 设置缩放光标。
    /// client 区域放行（WebView2 正常交互）。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_NCHITTEST:
                if (WindowChromeService.IsMaximized(this))
                {
                    handled = true;
                    return (IntPtr)NativeMethods.HTCLIENT;   // 全屏禁缩放
                }
                int hit = ComputeResizeHit(hwnd, lParam);
                if (hit != 0)
                {
                    handled = true;
                    return (IntPtr)hit;
                }
                return IntPtr.Zero;   // client 放行给默认（WebView2）

            case NativeMethods.WM_SETCURSOR:
            {
                int cursorId = wParam.ToInt32() switch
                {
                    NativeMethods.HTLEFT or NativeMethods.HTRIGHT => NativeMethods.IDC_SIZEWE,
                    NativeMethods.HTTOP or NativeMethods.HTBOTTOM => NativeMethods.IDC_SIZENS,
                    NativeMethods.HTTOPLEFT or NativeMethods.HTBOTTOMRIGHT => NativeMethods.IDC_SIZENWSE,
                    NativeMethods.HTTOPRIGHT or NativeMethods.HTBOTTOMLEFT => NativeMethods.IDC_SIZENESW,
                    _ => 0
                };
                if (cursorId != 0)
                {
                    _ = NativeMethods.SetCursor(NativeMethods.LoadCursor(IntPtr.Zero, cursorId));
                    handled = true;
                    return IntPtr.Zero;
                }
                return IntPtr.Zero;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>窗口四周边缘 6px 命中缩放（四角优先）；client 区域返回 0。</summary>
    private int ComputeResizeHit(IntPtr hwnd, IntPtr lParam)
    {
        int sx = (short)(lParam.ToInt64() & 0xFFFF);
        int sy = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        NativeMethods.GetWindowRect(hwnd, out var r);
        int x = sx - r.Left, y = sy - r.Top;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;

        const int E = 6;
        bool left = x < E, right = x >= w - E, top = y < E, bottom = y >= h - E;
        if (left && top) return NativeMethods.HTTOPLEFT;
        if (right && top) return NativeMethods.HTTOPRIGHT;
        if (left && bottom) return NativeMethods.HTBOTTOMLEFT;
        if (right && bottom) return NativeMethods.HTBOTTOMRIGHT;
        if (left) return NativeMethods.HTLEFT;
        if (right) return NativeMethods.HTRIGHT;
        if (top) return NativeMethods.HTTOP;
        if (bottom) return NativeMethods.HTBOTTOM;
        return 0;
    }

    /// <summary>
    /// 显示窗口（幂等）。除了首次显示，也负责「启动后隐藏 → 游戏退出后恢复」这条路径 ——
    /// 所以不能因为 <c>_shown</c> 已为真就直接 return。
    /// </summary>
    public void EnsureShown()
    {
        if (!_shown)
        {
            _shown = true;
            Show();
        }
        else if (!IsVisible)
        {
            Show();
        }
        Activate();
    }

    /// <summary>「启动后隐藏」：游戏进程确认启动后由 WindowControlService 调到这里。</summary>
    private void HideForGame()
    {
        if (IsVisible) Hide();
    }

    /// <summary>把当前窗口尺寸写入 data.json（window_size），供下次启动恢复。</summary>
    private async Task SaveWindowSizeAsync()
    {
        try
        {
            var w = (int)Math.Round(ActualWidth);
            var h = (int)Math.Round(ActualHeight);
            if (w < 200 || h < 150) return;
            await _store.UpdateAsync(data =>
            {
                data.WindowSize = new WindowSizeInfo { Width = w, Height = h };
                return true;
            });
        }
        catch
        {
            // 保存失败不影响运行
        }
    }

    private async Task InitWebViewAsync()
    {
        var userDataFolder = Path.Combine(_paths.BaseDir, ".webview2");
        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
        await webView.EnsureCoreWebView2Async(env);
        webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        // 禁用界面缩放：Chromium 默认 Ctrl+滚轮 / Ctrl+加减小键盘 / Ctrl+0 都会缩放整个页面，
        // 会把固定像素布局与各弹窗的定位一起放大，且刷新后仍保留（缩放级别被记住）。
        // 触屏 / 触控板双指捏合缩放同理，一并关掉。
        webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
        webView.CoreWebView2.Settings.IsPinchZoomEnabled = false;
        // 缩放级别会被 WebView2 按「源」记在用户数据目录里 —— 关掉控件还不够，
        // 得把级别复位，否则之前被放大过的用户重启后仍是放大状态。
        // 复位要在每次文档加载完之后再做一次：per-origin 的缩放是随文档加载才生效的。
        webView.ZoomFactor = 1.0;
        webView.CoreWebView2.NavigationCompleted += (_, _) => webView.ZoomFactor = 1.0;
        // 禁用 Chromium 默认右键菜单（复制/打印/另存为…），右键菜单由前端自定义实现
        webView.CoreWebView2.ContextMenuRequested += (_, e) => e.Handled = true;
        webView.CoreWebView2.AddHostObjectToScript("pywebviewHost", _bridge);
        await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(WebView2Glue.Script);
        webView.CoreWebView2.Navigate($"http://127.0.0.1:{_port}/?desktop=1");
        WindowIconService.TrySetWindowIcon(this, Path.Combine(_paths.StaticRoot, "tmod_icon.dat"));
    }
}
