// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Hosting;
using TModManager.App.Wpf;
using TModManager.Core.Logs;
using TModManager.Core.Models;
using TModManager.Core.Mods;
using TModManager.Core.Processes;
using TModManager.Core.Stats;
using TModManager.Core.Storage;

namespace TModManager.App.Hosting;

/// <summary>
/// 后台监控：每 3s 检测 tModLoader 运行→退出边沿，触发 .tmod.disabled 还原（对齐 Python _monitor_tmodloader）。
/// 同时负责：
/// <list type="bullet">
///   <item>游玩统计：运行期间定期打心跳，退出边沿结算本次会话。</item>
///   <item>「启动后隐藏」：确认游戏进程真的起来之后才把启动器窗口藏起来。</item>
///   <item>游戏退出：恢复窗口、分析日志做崩溃归因、弹右下角通知、把报告透给前端。</item>
/// </list>
/// </summary>
public sealed class TmlMonitorService : IHostedService, IDisposable
{
    /// <summary>心跳落盘间隔（tick 数）。3s × 20 = 60s，避免每分钟重写多次 data.json。</summary>
    private const int HeartbeatEveryTicks = 20;

    private readonly TmlProcessService _procs;
    private readonly SpecialLoadService _special;
    private readonly StatsService _stats;
    private readonly JsonStore _store;
    private readonly CrashAttributionService _crash;
    private readonly WindowControlService _window;
    private readonly NotificationService _notify;
    private readonly ExitReportService _reports;
    private CancellationTokenSource? _cts;

    public TmlMonitorService(
        TmlProcessService procs,
        SpecialLoadService special,
        StatsService stats,
        JsonStore store,
        CrashAttributionService crash,
        WindowControlService window,
        NotificationService notify,
        ExitReportService reports)
    {
        _procs = procs;
        _special = special;
        _stats = stats;
        _store = store;
        _crash = crash;
        _window = window;
        _notify = notify;
        _reports = reports;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => LoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var wasRunning = _procs.IsRunning();

        // 启动自检：上次异常退出（程序被强杀 / 断电）可能留下未结算的会话。
        // 此刻游戏没在跑 ⇒ 那次游玩已经结束，就地结算掉（时长按最后一次心跳算，不会把停机时间算进来）。
        if (!wasRunning)
        {
            try { await _stats.CloseActiveAsync(); } catch { }
        }

        var ticks = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { break; }
            try
            {
                var running = _procs.IsRunning();

                if (running && !wasRunning)
                {
                    // 启动接口只登记「想隐藏」，真正隐藏要等确认游戏进程起来 —— 启动失败时窗口不会消失
                    if (_window.ConsumePendingHide()) _window.HideForGame();
                }

                if (wasRunning && !running)
                {
                    await HandleExitAsync();
                }
                else if (running && ++ticks >= HeartbeatEveryTicks)
                {
                    ticks = 0;
                    await _stats.HeartbeatAsync();        // 声明「还在玩」，供异常退出后估算时长
                }

                wasRunning = running;
            }
            catch
            {
            }
        }
    }

    /// <summary>游戏退出边沿：还原特殊加载、结算时长、归因、恢复窗口、通知。</summary>
    private async Task HandleExitAsync()
    {
        _special.RestoreAllDisabled();

        // 先取会话信息（CloseActive 会清掉 Active），用于挑「本次会话」的日志
        var before = await _store.ReadAsync();
        var active = before.Stats.Active;

        var seconds = await _stats.CloseActiveAsync();
        var data = await _store.ReadAsync();

        ExitAnalysis? analysis = null;
        try { analysis = _crash.AnalyzeLatest(data, active?.StartedAt); } catch { }

        _window.ShowAfterGame();

        var crashed = analysis?.Crashed == true;
        var title = crashed ? "游戏异常退出" : "游戏已退出";
        var message = crashed
            ? BuildCrashMessage(analysis!)
            : $"本次游玩 {ExitReportService.FormatDuration(seconds)}";

        _reports.Publish(new ExitReport
        {
            Kind = crashed ? "crash" : "exit",
            Title = title,
            Message = message,
            Summary = analysis?.Summary,
            Advice = analysis?.Advice,
            ModName = analysis?.ModName,
            Evidence = analysis?.Evidence ?? new List<string>(),
            LogPath = analysis?.LogPath,
            LogName = analysis?.LogName,
            Seconds = seconds,
        });

        if (!data.GetBool("exit_notify", true)) return;

        Action? openLog = analysis?.LogPath is { } logPath
            ? () =>
            {
                try { Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true }); }
                catch { }
            }
            : null;

        _notify.Show(
            title,
            message,
            crashed ? NotificationKind.Warning : NotificationKind.Success,
            IsDarkTheme(data),
            crashed && openLog != null ? "查看日志" : null,
            openLog);
    }

    private static string BuildCrashMessage(ExitAnalysis analysis)
    {
        var sb = new StringBuilder(analysis.Summary);
        if (!string.IsNullOrWhiteSpace(analysis.ModName))
            sb.Append("（涉及模组：").Append(analysis.ModName).Append('）');
        if (!string.IsNullOrWhiteSpace(analysis.Advice))
            sb.Append('\n').Append(analysis.Advice);
        if (!string.IsNullOrWhiteSpace(analysis.LogName))
            sb.Append("\n日志：").Append(analysis.LogName);
        return sb.ToString();
    }

    /// <summary>通知窗口跟随前端主题设置（light / dark / system）。</summary>
    private static bool IsDarkTheme(AppData data)
    {
        return data.GetString("theme", "dark") switch
        {
            "light" => false,
            "system" => !SystemUsesLightTheme(),
            _ => true,
        };
    }

    private static bool SystemUsesLightTheme()
    {
        try
        {
            var value = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return value is int i && i != 0;
        }
        catch
        {
            return false;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose() => _cts?.Dispose();
}
