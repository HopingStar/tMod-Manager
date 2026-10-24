// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

namespace TModManager.Core.Mods;

/// <summary>刷新模组元数据的进度快照（供 GET /api/mod_meta/progress 轮询）。</summary>
public sealed record ModMetaProgressSnapshot(bool Running, string Phase, int Current, int Total, long ElapsedMs);

/// <summary>
/// 刷新模组元数据的进度状态。ModsService 是单例，所以这里做成进程内共享；
/// 各字段用 volatile 保证轮询线程能及时看到写入（写的是 32 位量或引用，不会撕裂）。
/// </summary>
public sealed class ModMetaProgress
{
    private volatile bool _running;
    private volatile string _phase = "";
    private volatile int _current;
    private volatile int _total;
    private long _startedAt;

    public bool Running => _running;

    public void Start(string phase = "准备中")
    {
        _phase = phase;
        _current = 0;
        _total = 0;
        _startedAt = Environment.TickCount64;
        _running = true;
    }

    /// <summary>只更新阶段文字（总量未知时用）。</summary>
    public void Phase(string phase) => _phase = phase;

    /// <summary>更新阶段与进度计数。</summary>
    public void Report(string phase, int current, int total)
    {
        _phase = phase;
        _current = current;
        _total = total;
    }

    public void Finish()
    {
        _running = false;
        _phase = "";
    }

    public ModMetaProgressSnapshot Snapshot() => new(
        _running,
        _phase,
        _current,
        _total,
        _running ? Environment.TickCount64 - _startedAt : 0);
}
