// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using System.Windows;

namespace TModManager.App.Hosting;

/// <summary>窗口控制：/api/ready 显示主窗口 + /api/pick_bat 系统文件选择框（UI 线程）。</summary>
public sealed class WindowControlService
{
    private Action? _showRequested;

    public void RegisterShow(Action show) => _showRequested = show;

    public void RequestShow() => _showRequested?.Invoke();

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
}
