// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using System.Windows;

namespace TModManager.App.Hosting;

/// <summary>
/// 窗口控制：
/// <list type="bullet">
///   <item>/api/ready 显示主窗口；/api/pick_bat 系统文件选择框（UI 线程）。</item>
///   <item>「启动后隐藏」：启动接口只登记 <see cref="RequestHideOnGameStart"/>，
///     由监控服务在**确认游戏进程真的起来之后**调用 <see cref="HideForGame"/> ——
///     这样游戏启动失败时窗口不会莫名其妙消失。</item>
///   <item>游戏退出时 <see cref="ShowAfterGame"/> 把窗口恢复出来。</item>
/// </list>
/// </summary>
public sealed class WindowControlService
{
    /// <summary>「待隐藏」登记的有效期：超过这个时间还没检测到游戏启动就作废。</summary>
    private static readonly TimeSpan PendingHideLifetime = TimeSpan.FromSeconds(120);

    private Action? _show;
    private Action? _hide;

    private DateTime? _pendingHideAt;
    private bool _hiddenForGame;

    /// <summary>注册主窗口的显示 / 隐藏动作（由 MainWindow 在构造时调用）。</summary>
    public void RegisterWindow(Action show, Action hide)
    {
        _show = show;
        _hide = hide;
    }

    /// <summary>兼容旧调用（只需要显示）。</summary>
    public void RegisterShow(Action show) => _show = show;

    /// <summary>请求显示主窗口（幂等）。</summary>
    public void RequestShow() => OnUi(_show);

    /// <summary>启动接口调用：登记「游戏起来后把窗口隐藏」，但**先不隐藏**。</summary>
    public void RequestHideOnGameStart() => _pendingHideAt = DateTime.UtcNow;

    /// <summary>启动失败时撤销登记（避免下一次无关启动误隐藏）。</summary>
    public void CancelPendingHide() => _pendingHideAt = null;

    /// <summary>监控服务检测到游戏进程起来时调用：消费登记并隐藏窗口。</summary>
    public bool ConsumePendingHide()
    {
        var at = _pendingHideAt;
        _pendingHideAt = null;
        if (at is null) return false;
        if (DateTime.UtcNow - at.Value > PendingHideLifetime) return false;
        return true;
    }

    /// <summary>把主窗口隐藏（由监控服务在游戏确认启动后调用）。</summary>
    public void HideForGame()
    {
        _hiddenForGame = true;
        OnUi(_hide);
    }

    /// <summary>游戏退出后把窗口恢复出来（只在确实由启动器隐藏过时才动作）。</summary>
    public void ShowAfterGame()
    {
        if (!_hiddenForGame) return;
        _hiddenForGame = false;
        OnUi(_show);
    }

    public bool IsHiddenForGame => _hiddenForGame;

    /// <summary>在 UI 线程弹系统文件框选择 .bat，返回路径（取消返回空串）。</summary>
    public async Task<string> PickBatAsync()
    {
        if (Application.Current?.Dispatcher is not { } disp)
            return "";
        try
        {
            return await disp.InvokeAsync(() =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "选择启动文件 (.bat)",
                    Filter = "启动脚本 (*.bat)|*.bat|所有文件 (*.*)|*.*",
                };
                return dlg.ShowDialog() == true ? dlg.FileName : "";
            });
        }
        catch
        {
            return "";
        }
    }

    /// <summary>WPF 窗口只能在 UI 线程操作；Kestrel / 监控线程一律切过去。</summary>
    private static void OnUi(Action? action)
    {
        if (action == null) return;
        var disp = Application.Current?.Dispatcher;
        if (disp == null) return;
        if (disp.CheckAccess()) action();
        else disp.BeginInvoke(action);
    }
}
