// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using System.Windows;

namespace TModManager.App.Wpf;

public enum NotificationKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>
/// 右下角浮层通知。**不依赖 WinForms / 第三方 Toast 库** —— 用普通 WPF 窗口自绘，
/// 因此主窗口被隐藏时也能照常弹出，并且能跟随深色 / 浅色主题。
/// 同一时间只保留一条：新通知会先关掉上一条。
/// </summary>
public sealed class NotificationService
{
    private NotificationWindow? _current;

    public void Show(
        string title,
        string message,
        NotificationKind kind = NotificationKind.Info,
        bool dark = true,
        string? actionLabel = null,
        Action? action = null)
    {
        var app = Application.Current;
        if (app == null) return;

        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                _current?.Close();

                var window = new NotificationWindow(title, message, kind, dark, actionLabel, action);
                _current = window;
                window.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_current, window)) _current = null;
                };
                window.Show();
            }
            catch
            {
                // 通知只是锦上添花：失败不应影响游戏退出处理
            }
        }));
    }
}
