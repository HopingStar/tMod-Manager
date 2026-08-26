using System.Text.Json;
using System.Text.Json.Serialization;

namespace TModManager.Core.Models;

/// <summary>data.json 顶层结构（字段名 snake_case，与 Python 版一致）。</summary>
public sealed class AppData
{
    public List<VersionEntry> Versions { get; set; } = new();
    public LastLaunch? LastLaunch { get; set; }
    public Dictionary<string, string> PackDescs { get; set; } = new();
    public GlobalSpecial GlobalSpecial { get; set; } = new();
    public Dictionary<string, SpecialLoad> SpecialLoads { get; set; } = new();
    public Dictionary<string, JsonElement> MetaSettings { get; set; } = new();
    public Favorites Favorites { get; set; } = new();

    /// <summary>窗口缩放尺寸持久化（DIP 整数宽高）；null = 从未保存，用默认尺寸。</summary>
    public WindowSizeInfo? WindowSize { get; set; }
}

/// <summary>窗口尺寸（DIP 像素），由 WPF 窗口 SizeChanged 持久化、启动时恢复。</summary>
public sealed class WindowSizeInfo
{
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class VersionEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string? VersionInfo { get; set; }
    public string BatPath { get; set; } = "";
    public string? ModsPath { get; set; }
    public List<string> Packs { get; set; } = new();

    [JsonPropertyName("_dll_mtime")]
    public double DllMtime { get; set; }
}

public sealed class LastLaunch
{
    public string VersionId { get; set; } = "";
    public string? PackId { get; set; }

    /// <summary>最近一次启动实际生效的禁用集合；null 表示旧数据（无此字段）→ launch_current 回退按配置计算。</summary>
    public List<string>? Disabled { get; set; }
}

public sealed class GlobalSpecial
{
    public bool Active { get; set; }
    public List<string> Mods { get; set; } = new();
}

public sealed class SpecialLoad
{
    public bool Independent { get; set; }

    /// <summary>兼容旧字段 active（旧数据可能用 active 而非 independent）。</summary>
    [JsonPropertyName("active")]
    public bool Active { get; set; }

    public List<string> Mods { get; set; } = new();
}

public sealed class Favorites
{
    public List<string> Versions { get; set; } = new();
    public List<string> Packs { get; set; } = new();

    [JsonPropertyName("_seq")]
    public long? Seq { get; set; }
}
