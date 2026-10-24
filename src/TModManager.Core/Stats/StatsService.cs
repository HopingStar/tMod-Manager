// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using TModManager.Core.Models;
using TModManager.Core.Storage;

namespace TModManager.Core.Stats;

/// <summary>
/// 游玩统计：每次启动记数并开一个「进行中会话」，tModLoader 退出时结算时长。
/// 会话期间由监控服务定期打心跳（LastSeenAt），这样即使程序被强杀，
/// 也能用心跳时刻算出真实时长，而不会把「关机期间」也算进去。
/// </summary>
public sealed class StatsService
{
    /// <summary>最近记录保留条数。</summary>
    private const int RecentLimit = 50;

    private readonly JsonStore _store;

    public StatsService(JsonStore store) => _store = store;

    /// <summary>记录一次启动：版本/整合包计数 +1，并开始一个进行中会话。</summary>
    public async Task RecordLaunchAsync(string versionId, string? packId)
    {
        if (string.IsNullOrEmpty(versionId)) return;
        await _store.UpdateAsync(data =>
        {
            var s = data.Stats;
            s.VersionLaunches[versionId] = s.VersionLaunches.GetValueOrDefault(versionId) + 1;
            if (!string.IsNullOrEmpty(packId))
                s.PackLaunches[packId!] = s.PackLaunches.GetValueOrDefault(packId!) + 1;

            var now = DateTimeOffset.UtcNow;
            s.Active = new ActiveSession
            {
                VersionId = versionId,
                PackId = string.IsNullOrEmpty(packId) ? null : packId,
                StartedAt = now,
                LastSeenAt = now,
            };
            return true;
        });
    }

    /// <summary>心跳：声明「游戏此刻仍在运行」。由监控服务定期调用（落盘频率已节流）。</summary>
    public async Task HeartbeatAsync()
    {
        await _store.UpdateAsync(data =>
        {
            if (data.Stats.Active is { } a) a.LastSeenAt = DateTimeOffset.UtcNow;
            return true;
        });
    }

    /// <summary>当前是否有进行中的会话（不落盘，只读）。</summary>
    public async Task<bool> HasActiveAsync()
    {
        var data = await _store.ReadAsync();
        return data.Stats.Active != null;
    }

    /// <summary>结算进行中的会话并落盘。返回本次游玩秒数。</summary>
    public async Task<double> CloseActiveAsync()
    {
        return await _store.UpdateAsync(data =>
        {
            var s = data.Stats;
            var a = s.Active;
            if (a == null) return 0d;

            // 结束时刻取「最后一次心跳」而非当前时间：程序被强杀时不会把停机时间算成游玩时长
            var end = a.LastSeenAt > a.StartedAt ? a.LastSeenAt : a.StartedAt;
            var secs = (end - a.StartedAt).TotalSeconds;
            if (secs < 0) secs = 0;

            if (secs > 0)
            {
                s.VersionSeconds[a.VersionId] = s.VersionSeconds.GetValueOrDefault(a.VersionId) + secs;
                if (!string.IsNullOrEmpty(a.PackId))
                    s.PackSeconds[a.PackId!] = s.PackSeconds.GetValueOrDefault(a.PackId!) + secs;
                s.TotalSeconds += secs;
            }

            s.Recent.Insert(0, new PlayRecord
            {
                VersionId = a.VersionId,
                PackId = a.PackId,
                StartedAt = a.StartedAt,
                Seconds = secs,
            });
            if (s.Recent.Count > RecentLimit)
                s.Recent.RemoveRange(RecentLimit, s.Recent.Count - RecentLimit);

            s.Active = null;
            return secs;
        });
    }

    /// <summary>清空全部统计（不影响版本 / 整合包本身）。</summary>
    public async Task ResetAsync()
    {
        await _store.UpdateAsync(data =>
        {
            data.Stats = new PlayStats();
            return true;
        });
    }
}
