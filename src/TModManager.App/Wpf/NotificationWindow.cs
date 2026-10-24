// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace TModManager.App.Wpf;

/// <summary>右下角浮层通知窗口（无边框、置顶、不抢焦点、10 秒后自动淡出）。</summary>
public sealed class NotificationWindow : Window
{
    private static readonly TimeSpan AutoClose = TimeSpan.FromSeconds(10);

    private readonly DispatcherTimer _timer;
    private readonly Border _card;
    private bool _closing;

    public NotificationWindow(
        string title,
        string message,
        NotificationKind kind,
        bool dark,
        string? actionLabel,
        Action? action)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 400;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Opacity = 0;

        var accent = kind switch
        {
            NotificationKind.Success => Color.FromRgb(0x35, 0xD0, 0x7F),
            NotificationKind.Warning => Color.FromRgb(0xFF, 0xB0, 0x20),
            NotificationKind.Error => Color.FromRgb(0xFF, 0x5D, 0x5D),
            _ => Color.FromRgb(0x7C, 0x9C, 0xFF),
        };
        var glyph = kind switch
        {
            NotificationKind.Success => "✓",
            NotificationKind.Warning => "!",
            NotificationKind.Error => "×",
            _ => "i",
        };

        var fg = new SolidColorBrush(dark ? Color.FromRgb(0xE8, 0xEA, 0xF6) : Color.FromRgb(0x1F, 0x24, 0x30));
        var muted = new SolidColorBrush(dark ? Color.FromRgb(0x9D, 0xB0, 0xC8) : Color.FromRgb(0x5A, 0x64, 0x78));
        var bg = new SolidColorBrush(dark
            ? Color.FromArgb(0xF2, 0x1B, 0x25, 0x42)
            : Color.FromArgb(0xFA, 0xFF, 0xFF, 0xFF));
        var border = new SolidColorBrush(dark
            ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x22, 0x00, 0x00, 0x00));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 左侧圆形图标
        var icon = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Background = new SolidColorBrush(accent),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = glyph,
                Foreground = Brushes.White,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        // 右侧文字
        var textStack = new StackPanel();
        textStack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = fg,
            TextWrapping = TextWrapping.Wrap,
        });
        textStack.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 12.5,
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19,
            Margin = new Thickness(0, 4, 0, 0),
        });

        if (!string.IsNullOrWhiteSpace(actionLabel) && action != null)
        {
            var actionText = new TextBlock
            {
                Text = actionLabel,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(accent),
            };
            var actionButton = new Border
            {
                Child = actionText,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 9, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Color.FromArgb(0x1F, accent.R, accent.G, accent.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, accent.R, accent.G, accent.B)),
                BorderThickness = new Thickness(1),
            };
            actionButton.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                try { action(); } catch { }
                Dismiss();
            };
            textStack.Children.Add(actionButton);
        }

        Grid.SetColumn(textStack, 1);
        grid.Children.Add(textStack);

        _card = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = bg,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 12, 16, 12),
            Margin = new Thickness(10),
            Child = grid,
            Effect = new DropShadowEffect
            {
                BlurRadius = 22,
                ShadowDepth = 4,
                Direction = 270,
                Opacity = 0.42,
                Color = Colors.Black,
            },
        };

        // 点击卡片空白处关闭（按钮自己会先吃掉事件）
        _card.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Dismiss();
        };

        Content = _card;

        _timer = new DispatcherTimer { Interval = AutoClose };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Dismiss();
        };

        Loaded += (_, _) =>
        {
            PositionBottomRight();
            AnimateIn();
            _timer.Start();
        };
    }

    private void PositionBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 12;
        Top = area.Bottom - ActualHeight - 12;
    }

    private void AnimateIn()
    {
        var translate = new TranslateTransform(26, 0);
        _card.RenderTransform = translate;

        var storyboard = new Storyboard();
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, this);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));

        var slide = new DoubleAnimation(26, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(slide, translate);
        Storyboard.SetTargetProperty(slide, new PropertyPath(TranslateTransform.XProperty));

        storyboard.Children.Add(fade);
        storyboard.Children.Add(slide);
        storyboard.Begin();
    }

    private void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();

        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) => { try { Close(); } catch { } };
        BeginAnimation(OpacityProperty, fade);
    }
}
