// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using Microsoft.Extensions.Hosting;
using TModManager.Core.Mods;
using TModManager.Core.Processes;
using TModManager.Core.Stats;

namespace TModManager.App.Hosting;

/// <summary>
/// 后台监控：每 3s 检测 tModLoader 运行→退出边沿，触发 .tmod.disabled 还原（对齐 Python _monitor_tmodloader）。
/// 同时负责游玩统计：运行期间定期打心跳，退出边沿结算本次会话。
/// </summary>
public sealed class TmlMonitorService : IHostedService, IDisposable
{
    /// <summary>心跳落盘间隔（tick 数）。3s × 20 = 60s，避免每分钟重写多次 data.json。</summary>
    private const int HeartbeatEveryTicks = 20;

    private readonly TmlProcessService _procs;
    private readonly SpecialLoadService _special;
    private readonly StatsService _stats;
    private CancellationTokenSource? _cts;

    public TmlMonitorService(TmlProcessService procs, SpecialLoadService special, StatsService stats)
    {
        _procs = procs;
        _special = special;
        _stats = stats;
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
                if (wasRunning && !running)
                {
                    _special.RestoreAllDisabled();
                    await _stats.CloseActiveAsync();      // 游戏退出 → 结算本次游玩时长
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

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose() => _cts?.Dispose();
}
