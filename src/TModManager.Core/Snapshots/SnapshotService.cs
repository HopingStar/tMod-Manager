// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using System.Text.Json;
using System.Text.Json.Nodes;
using TModManager.Core.Configuration;
using TModManager.Core.Storage;

namespace TModManager.Core.Snapshots;

/// <summary>一条 enabled.json 快照。</summary>
public sealed class SnapshotEntry
{
    /// <summary>快照目录名（= 时间戳），作为对外 id。</summary>
    public string Id { get; set; } = "";

    /// <summary>快照目录的绝对路径。</summary>
    public string Path { get; set; } = "";

    public DateTime Created { get; set; }

    /// <summary>人类可读的来源描述（如「切换前（Calamity Only / PackA）」）。</summary>
    public string Label { get; set; } = "";

    public string VersionName { get; set; } = "";
    public string PackId { get; set; } = "";

    /// <summary>这份快照是从哪个 Mods 目录取的（还原时若未指定版本就用它）。</summary>
    public string ModsDir { get; set; } = "";

    /// <summary>启用的模组数量；-1 表示当时那份 enabled.json 无法解析。</summary>
    public int Count { get; set; }
}

/// <summary>
/// 切换整合包<b>之前</b>对当前 <c>enabled.json</c> 做的时间戳快照。
///
/// 存在**程序目录**下的 <c>snapshots/&lt;时间戳&gt;/</c>（不写游戏目录、也不会混进整合包列表）：
/// <code>
/// &lt;程序目录&gt;/snapshots/20261011-153045/
///     enabled.json    ← 切换前那一刻的完整内容
///     meta.json       ← 时间 / 来源 / 数量
/// </code>
///
/// 取代原先那个「只有一代、且每次切换都被无条件覆盖」的 <c>enabled.json.bk</c> ——
/// 那个设计下，连续切两个包就会把用户**原本的配置永久覆盖掉**。
/// </summary>
public sealed class SnapshotService
{
    /// <summary>最多保留多少份快照（按时间从新到旧）。</summary>
    public const int KeepCount = 10;

    private const string MetaName = "meta.json";
    private const string PayloadName = "enabled.json";

    private readonly AppPaths _paths;

    public SnapshotService(AppPaths paths) => _paths = paths;

    /// <summary>快照父目录（程序目录/snapshots），按需创建。</summary>
    public string SnapshotRoot => Path.Combine(_paths.BaseDir, "snapshots");

    // ---------------- enabled.json 内容校验 ----------------

    /// <summary>解析 tModLoader 的 enabled.json：必须是**非空字符串数组**。空数组等于「全禁用」，调用方须拒绝。</summary>
    public static bool TryParseEnabledJson(string text, out List<string> names, out string? error)
    {
        names = new List<string>();
        error = null;

        JsonNode? node;
        try { node = JsonNode.Parse(text); }
        catch (Exception e) { error = "不是合法的 JSON（" + e.Message + "）"; return false; }

        if (node is not JsonArray array) { error = "顶层不是 JSON 数组"; return false; }

        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name))
            {
                error = "数组里存在非字符串项或空字符串";
                return false;
            }
            names.Add(name);
        }
        return true;
    }

    /// <summary>读文件并校验（用于整合包启动前的准入检查）。</summary>
    public static bool TryReadEnabledList(string file, out List<string> names, out string? error)
    {
        names = new List<string>();
        error = null;
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) { error = "文件不存在"; return false; }
        string text;
        try { text = File.ReadAllText(file); }
        catch (Exception e) { error = "读取失败：" + e.Message; return false; }
        return TryParseEnabledJson(text, out names, out error);
    }

    // ---------------- 快照 ----------------

    /// <summary>把 modsDir 下当前的 enabled.json 存成一份快照。失败返回 null。</summary>
    public SnapshotEntry? Capture(string modsDir, string label, string versionName = "", string packId = "")
    {
        if (string.IsNullOrWhiteSpace(modsDir)) return null;
        var source = Path.Combine(Path.GetFullPath(modsDir), "enabled.json");
        if (!File.Exists(source)) return null;

        string text;
        try { text = File.ReadAllText(source); }
        catch { return null; }

        int count = TryParseEnabledJson(text, out var parsed, out _) ? parsed.Count : -1;

        try
        {
            Directory.CreateDirectory(SnapshotRoot);

            var created = DateTime.Now;
            var baseName = created.ToString("yyyyMMdd-HHmmss");
            var dir = Path.Combine(SnapshotRoot, baseName);
            for (var i = 2; Directory.Exists(dir); i++)     // 同一秒内连切两次
                dir = Path.Combine(SnapshotRoot, baseName + "-" + i);
            Directory.CreateDirectory(dir);

            File.WriteAllText(Path.Combine(dir, PayloadName), text);

            var entry = new SnapshotEntry
            {
                Id = Path.GetFileName(dir),
                Path = dir,
                Created = created,
                Label = label,
                VersionName = versionName,
                PackId = packId,
                ModsDir = Path.GetFullPath(modsDir),
                Count = count,
            };
            File.WriteAllText(Path.Combine(dir, MetaName), JsonSerializer.Serialize(entry, JsonOpts.Write));

            Prune();
            return entry;
        }
        catch { return null; }
    }

    /// <summary>按时间从新到旧列出全部快照。</summary>
    public List<SnapshotEntry> List()
    {
        var list = new List<SnapshotEntry>();
        if (!Directory.Exists(SnapshotRoot)) return list;

        foreach (var dir in Directory.GetDirectories(SnapshotRoot))
        {
            var id = Path.GetFileName(dir);
            var entry = new SnapshotEntry { Id = id, Path = dir };

            var metaPath = Path.Combine(dir, MetaName);
            if (File.Exists(metaPath))
            {
                try
                {
                    var loaded = JsonSerializer.Deserialize<SnapshotEntry>(File.ReadAllText(metaPath), JsonOpts.Read);
                    if (loaded != null) entry = loaded;
                }
                catch { /* meta 坏了就用目录信息兜底 */ }
            }

            entry.Id = id;
            entry.Path = dir;
            if (entry.Created == default) entry.Created = Directory.GetLastWriteTime(dir);
            if (entry.Count == 0)
            {
                var payload = Path.Combine(dir, PayloadName);
                if (File.Exists(payload) &&
                    TryParseEnabledJson(File.ReadAllText(payload), out var parsed, out _))
                    entry.Count = parsed.Count;
            }
            list.Add(entry);
        }

        return list.OrderByDescending(x => x.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>把某份快照写回 modsDir 的 enabled.json。成功返回写入的路径。</summary>
    public string? Restore(string id, string? modsDir)
    {
        var dir = ResolveDir(id);
        if (dir == null) return null;

        var payload = Path.Combine(dir, PayloadName);
        if (!File.Exists(payload)) return null;

        if (string.IsNullOrWhiteSpace(modsDir) || !Directory.Exists(modsDir)) return null;

        var target = Path.Combine(Path.GetFullPath(modsDir), "enabled.json");
        File.Copy(payload, target, true);
        return target;
    }

    /// <summary>把快照目录名解析成绝对路径；拒绝任何目录穿越。</summary>
    public string? ResolveDir(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (id.Contains("..") || id.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) return null;
        var dir = Path.Combine(SnapshotRoot, id);
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>只保留最近的 KeepCount 份（目录名是时间戳，字典序即时间序）。</summary>
    private void Prune()
    {
        if (!Directory.Exists(SnapshotRoot)) return;
        var stale = Directory.GetDirectories(SnapshotRoot)
            .OrderByDescending(x => Path.GetFileName(x), StringComparer.Ordinal)
            .Skip(KeepCount);
        foreach (var dir in stale)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
