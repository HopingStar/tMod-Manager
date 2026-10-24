namespace TModManager.Core.Models;

/// <summary>
/// 游玩统计：按版本 / 整合包分别累计启动次数与游玩秒数。
/// 一并持久化在 data.json 的 stats 字段下。
/// </summary>
public sealed class PlayStats
{
    /// <summary>版本 id → 启动次数。</summary>
    public Dictionary<string, int> VersionLaunches { get; set; } = new();

    /// <summary>整合包 id → 启动次数。</summary>
    public Dictionary<string, int> PackLaunches { get; set; } = new();

    /// <summary>版本 id → 累计游玩秒数。</summary>
    public Dictionary<string, double> VersionSeconds { get; set; } = new();

    /// <summary>整合包 id → 累计游玩秒数。</summary>
    public Dictionary<string, double> PackSeconds { get; set; } = new();

    /// <summary>全部累计游玩秒数。</summary>
    public double TotalSeconds { get; set; }

    /// <summary>当前进行中的一次游玩（tModLoader 退出时结算）。null = 没有在玩。</summary>
    public ActiveSession? Active { get; set; }

    /// <summary>最近若干次游玩记录（新→旧，最多保留 50 条）。</summary>
    public List<PlayRecord> Recent { get; set; } = new();
}

/// <summary>进行中的一次游玩。</summary>
public sealed class ActiveSession
{
    public string VersionId { get; set; } = "";
    public string? PackId { get; set; }

    /// <summary>本次游玩开始时刻（UTC）。</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>心跳时刻（UTC）。监控服务运行期间定期刷新；程序异常退出后用它估算真实时长，
    /// 避免把「关机期间」也算成游玩时间。</summary>
    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>一条已结算的游玩记录。</summary>
public sealed class PlayRecord
{
    public string VersionId { get; set; } = "";
    public string? PackId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public double Seconds { get; set; }
}
