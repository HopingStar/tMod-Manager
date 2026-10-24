// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

namespace TModManager.App.Hosting;

/// <summary>最近一次游戏退出的报告（前端轮询 /api/status 时取走并提示一次）。</summary>
public sealed class ExitReport
{
    /// <summary>自增序号：前端据此判断「这是一条新报告」。</summary>
    public long Id { get; set; }

    /// <summary>exit = 正常退出；crash = 疑似异常退出。</summary>
    public string Kind { get; set; } = "exit";

    public string Title { get; set; } = "";

    public string Message { get; set; } = "";

    /// <summary>归因结论（仅 crash）。</summary>
    public string? Summary { get; set; }

    public string? Advice { get; set; }

    public string? ModName { get; set; }

    public List<string> Evidence { get; set; } = new();

    public string? LogPath { get; set; }
    public string? LogName { get; set; }

    /// <summary>本次游玩时长（秒）。</summary>
    public double Seconds { get; set; }

    public DateTime At { get; set; }
}

/// <summary>保存最近一次退出报告，供 /api/status 透出。</summary>
public sealed class ExitReportService
{
    private readonly object _gate = new();
    private long _seq;
    private ExitReport? _last;

    public ExitReport? Last
    {
        get { lock (_gate) return _last; }
    }

    public ExitReport Publish(ExitReport report)
    {
        lock (_gate)
        {
            report.Id = ++_seq;
            report.At = DateTime.Now;
            _last = report;
            return report;
        }
    }

    /// <summary>把秒数格式化成「1 小时 23 分 / 12 分 5 秒 / 45 秒」。</summary>
    public static string FormatDuration(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var total = (long)Math.Round(seconds);
        if (total < 60) return $"{total} 秒";

        var h = total / 3600;
        var m = total % 3600 / 60;
        var s = total % 60;

        if (h > 0) return m > 0 ? $"{h} 小时 {m} 分" : $"{h} 小时";
        return s > 0 ? $"{m} 分 {s} 秒" : $"{m} 分钟";
    }
}
