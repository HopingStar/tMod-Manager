using System.Text.Json;
using System.Text.Json.Nodes;
using TModManager.Core.Configuration;
using TModManager.Core.Models;
using TModManager.Core.Network;
using TModManager.Core.Parsing;
using TModManager.Core.Storage;

namespace TModManager.Core.Mods;

/// <summary>模组/整合包/目录相关服务：全局整合包扫描、本地 Mods 扫描、模组列表构建（pack_mods / version_mods）。</summary>
public sealed class ModsService
{
    private readonly AppPaths _paths;
    private readonly JsonStore _store;
    private readonly MetaStore _meta;
    private readonly WorkshopScanner _workshop;
    private readonly SteamService _steam;

    public ModsService(AppPaths paths, JsonStore store, MetaStore meta, WorkshopScanner workshop, SteamService steam)
    {
        _paths = paths;
        _store = store;
        _meta = meta;
        _workshop = workshop;
        _steam = steam;
    }

    public string GlobalModpacksPath => _paths.GlobalModpacksPath;
    public string DefaultModsPath => _paths.DefaultModsPath;

    /// <summary>扫 ModPacks 下含 Mods/enabled.json 的目录，按名称排序。</summary>
    public List<PackInfo> GetGlobalPacks(AppData data)
    {
        var result = new List<PackInfo>();
        if (Directory.Exists(_paths.GlobalModpacksPath))
        {
            foreach (var dir in Directory.EnumerateDirectories(_paths.GlobalModpacksPath))
            {
                var id = Path.GetFileName(dir);
                var enabled = Path.Combine(dir, "Mods", "enabled.json");
                if (!File.Exists(enabled)) continue;
                data.PackDescs.TryGetValue(id, out var desc);
                result.Add(new PackInfo { Id = id, Name = id, Desc = desc ?? "" });
            }
        }
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        return result;
    }

    public bool PackExists(string packId)
        => !string.IsNullOrEmpty(packId) && Directory.Exists(Path.Combine(_paths.GlobalModpacksPath, packId));

    /// <summary>本地 Mods 目录下的模组（含已禁用 .tmod.disabled），按名称排序。</summary>
    public List<LocalModInfo> ListLocalMods()
    {
        var result = new List<LocalModInfo>();
        if (!Directory.Exists(_paths.DefaultModsPath)) return result;
        foreach (var fn in Directory.EnumerateFiles(_paths.DefaultModsPath))
        {
            var lower = Path.GetFileName(fn).ToLowerInvariant();
            if (lower.EndsWith(".tmod.disabled"))
                result.Add(new LocalModInfo { Name = Path.GetFileName(fn)[..^".tmod.disabled".Length], Disabled = true });
            else if (lower.EndsWith(".tmod"))
                result.Add(new LocalModInfo { Name = Path.GetFileNameWithoutExtension(fn), Disabled = false });
        }
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>整合包模组列表（整合包文件夹 ∪ 本地；对照整合包 enabled.json）。目录不存在返回 (null,404)。</summary>
    public async Task<(ModsListResult? Result, int Status, string Error)> PackModsAsync(string packId, string? versionId)
    {
        var modsDir = Path.Combine(_paths.GlobalModpacksPath, packId, "Mods");
        if (!Directory.Exists(modsDir))
            return (null, 404, "整合包 Mods 目录不存在");

        var enabled = await ReadEnabledNamesAsync(Path.Combine(modsDir, "enabled.json"));
        var enabledSet = enabled.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var data = await _store.ReadAsync();
        var batPaths = GetVersionBatPaths(data, versionId, out var versionName);
        var localNames = ListLocalMods().Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ws = _workshop.Scan(batPaths);
        var wsNames = ws.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var packNames = Directory.EnumerateFiles(modsDir)
            .Where(f => f.EndsWith(".tmod", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var allSet = packNames.Union(localNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var enabledExist = enabledSet.Intersect(allSet).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var disabled = allSet.Except(enabledSet).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var missing = enabledSet.Except(allSet).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        var cache = await _meta.ReadAsync();
        var idMap = BuildIdMap(ws, cache);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return (new ModsListResult
        {
            PackId = packId,
            Source = "folder",
            VersionId = versionId ?? "",
            VersionName = versionName,
            Enabled = Build(enabledExist, localNames, wsNames, packNames, cache, idMap, now),
            Disabled = Build(disabled, localNames, wsNames, packNames, cache, idMap, now),
            Missing = Build(missing, localNames, wsNames, packNames, cache, idMap, now),
        }, 200, "");
    }

    /// <summary>版本模组列表（Workshop ∪ 本地；对照该版本共享 Mods 目录的 enabled.json）。版本不存在 (null,404)，目录缺失 (null,400)。</summary>
    public async Task<(ModsListResult? Result, int Status, string Error)> VersionModsAsync(string versionId)
    {
        var data = await _store.ReadAsync();
        var version = data.Versions.FirstOrDefault(x => x.Id == versionId);
        if (version == null) return (null, 404, "版本不存在");

        var modsDir = version.ModsPath;
        if (string.IsNullOrEmpty(modsDir) || !Directory.Exists(modsDir))
            return (null, 400, "Mods 目录不存在");

        var enabled = await ReadEnabledNamesAsync(Path.Combine(modsDir, "enabled.json"));
        var enabledSet = enabled.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var batPaths = GetVersionBatPaths(data, versionId, out var versionName);
        var ws = _workshop.Scan(batPaths);
        var wsNames = ws.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var localNames = ListLocalMods().Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var allSet = wsNames.Union(localNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var enabledExist = enabledSet.Intersect(allSet).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var disabled = allSet.Except(enabledSet).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var missing = enabledSet.Except(allSet).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        var cache = await _meta.ReadAsync();
        var idMap = BuildIdMap(ws, cache);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var emptyPack = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // version 模式 in_pack 恒 false
        return (new ModsListResult
        {
            PackId = "",
            Source = "workshop",
            VersionId = versionId,
            VersionName = versionName,
            Enabled = Build(enabledExist, localNames, wsNames, emptyPack, cache, idMap, now),
            Disabled = Build(disabled, localNames, wsNames, emptyPack, cache, idMap, now),
            Missing = Build(missing, localNames, wsNames, emptyPack, cache, idMap, now),
        }, 200, "");
    }

    /// <summary>构建 /api/mod_meta 响应：本地 .tmod 逆向优先，Steam 缓存兜底；版本号缺失现场解析并回写。</summary>
    public async Task<ModMetaResponse> BuildModMetaAsync()
    {
        var cache = await _meta.ReadAsync();
        var data = await _store.ReadAsync();
        var batPaths = data.Versions.Where(x => !string.IsNullOrEmpty(x.BatPath)).Select(x => x.BatPath).ToList();
        var ws = _workshop.Scan(batPaths);

        cache["workshop_ids"] = ToJsonObject(ws.Select(x => new KeyValuePair<string, JsonNode>(x.Key, x.Value.Id)));

        var publishIds = GetPublishIds();
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in ws) merged[kv.Key] = kv.Value.Id;
        foreach (var kv in publishIds) merged[kv.Key] = kv.Value;

        var allNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in merged.Keys) allNames.Add(k);
        foreach (var m in ListLocalMods()) allNames.Add(m.Name);
        foreach (var n in ScanPackModNames()) allNames.Add(n);

        // 本地逆向优先：解析每个 .tmod 的 icon.png + Info
        var localMeta = cache["local_meta"] as JsonObject ?? new JsonObject();
        var patched = false;
        foreach (var name in allNames)
        {
            var tmod = TmodFinder.Find(name, _paths.DefaultModsPath, _paths.GlobalModpacksPath);
            if (tmod == null) continue;
            var meta = TmodParser.Parse(tmod);
            if (meta == null || (meta.IconBytes is not { Length: > 0 } && meta.DisplayName.Length == 0)) continue;
            localMeta[name] = new JsonObject
            {
                ["display_name"] = meta.DisplayName,
                ["author"] = meta.Author,
                ["icon"] = IconSaver.Save(name, meta.IconBytes, _paths.IconsDir) ?? "",
                ["version"] = meta.Version,
            };
            patched = true;
        }
        if (patched) { cache["local_meta"] = localMeta; await _meta.WriteAsync(cache); }

        var metaObj = cache["meta"] as JsonObject ?? new JsonObject();
        var updated = GetDouble(cache["updated"]);
        var localDisabled = ListLocalMods().ToDictionary(x => x.Name, x => x.Disabled, StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var mods = new List<ModMetaItem>();
        foreach (var name in allNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var lm = localMeta[name] as JsonObject ?? new JsonObject();
            var pid = merged.GetValueOrDefault(name);
            JsonNode? sm = null;
            if (!string.IsNullOrEmpty(pid)) sm = metaObj[pid];

            var displayName = lm["display_name"]?.ToString() ?? "";
            if (displayName.Length == 0) displayName = sm?["name"]?.ToString() ?? "";
            var icon = lm["icon"]?.ToString() ?? "";
            if (icon.Length == 0) icon = sm?["icon"]?.ToString() ?? "";
            var version = lm["version"]?.ToString() ?? "";
            if (version.Length == 0)
            {
                var tmod = TmodFinder.Find(name, _paths.DefaultModsPath, _paths.GlobalModpacksPath);
                if (tmod != null && TmodParser.Parse(tmod) is { } m2 && m2.Version.Length > 0)
                {
                    version = m2.Version;
                    lm["version"] = version;
                    localMeta[name] = lm;
                    patched = true;
                }
            }

            var steamUpdated = GetLong(sm?["time_updated"]);
            int? updDays = steamUpdated > 0 ? (int)((now - steamUpdated) / 86400) : null;
            mods.Add(new ModMetaItem
            {
                Name = name,
                Disabled = localDisabled.GetValueOrDefault(name),
                WorkshopId = string.IsNullOrEmpty(pid) ? null : pid,
                DisplayName = displayName,
                Icon = icon,
                Version = version,
                UpdateDaysAgo = updDays,
                Tags = (sm?["tags"] as JsonArray)?.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() ?? new(),
                Description = sm?["description"]?.ToString() ?? "",
                Subscriptions = sm?["subscriptions"],
                Favorited = sm?["favorited"],
                Views = sm?["views"],
                TimeCreated = sm?["time_created"],
                FileSize = sm?["file_size"],
                Visibility = sm?["visibility"],
                Banned = sm?["banned"],
            });
        }
        if (patched) { cache["local_meta"] = localMeta; await _meta.WriteAsync(cache); }

        var needsRefresh = mods.Any(m => m.WorkshopId != null && m.DisplayName.Length == 0 && m.Icon.Length == 0);
        return new ModMetaResponse { Mods = mods, NeedsRefresh = needsRefresh, Updated = updated };
    }

    /// <summary>刷新模组元数据：本地逆向优先 → Steam GetPublishedFileDetails 补全缺失 → 写缓存。
    /// 返回 (写盘后的缓存, Steam 失败明细 pid→原因)，对齐 Python refresh_mod_meta。</summary>
    public async Task<(JsonObject Cache, Dictionary<string, string> Errors)> RefreshModMetaAsync(bool force)
    {
        // 1) 本地逆向优先（回写 workshop_ids / local_meta / 缺失版本号）
        _ = await BuildModMetaAsync();
        var cache = await _meta.ReadAsync();

        // 2) Steam 补全：确保每个 workshop 模组都有元数据（含 time_updated / tags / file_size）
        var errors = new Dictionary<string, string>();
        var ws = _workshop.Scan(await GetAllBatPathsAsync());
        cache["workshop_ids"] = ToJsonObject(ws.Select(x => new KeyValuePair<string, JsonNode>(x.Key, x.Value.Id)));

        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in ws) merged[kv.Key] = kv.Value.Id;
        foreach (var kv in GetPublishIds()) merged[kv.Key] = kv.Value;

        var localMeta = cache["local_meta"] as JsonObject ?? new JsonObject();
        var metaObj = cache["meta"] as JsonObject ?? new JsonObject();

        // 本地逆向已覆盖本名/图标的模组不再需要 Steam
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, lm) in localMeta.ToDictionary(x => x.Key, x => x.Value))
        {
            var obj = lm as JsonObject;
            if (obj != null && ((obj["display_name"]?.ToString() ?? "").Length > 0 || (obj["icon"]?.ToString() ?? "").Length > 0))
                covered.Add(name);
        }

        var need = new List<string>();
        foreach (var (name, pid) in merged)
        {
            if (covered.Contains(name)) continue;
            if (force || !MetaComplete(metaObj[pid] as JsonObject)) need.Add(pid);
        }
        foreach (var v in ws.Values)
        {
            var pid = v.Id;
            if (force || !MetaComplete(metaObj[pid] as JsonObject))
                if (!need.Contains(pid)) need.Add(pid);
        }

        if (need.Count > 0)
        {
            var result = await _steam.QueryPublishDetailsAsync(need.Distinct().ToList());
            foreach (var kv in result.Results) metaObj[kv.Key] = kv.Value;
            foreach (var kv in result.Errors) errors[kv.Key] = kv.Value;
            cache["meta"] = metaObj;
        }

        cache["updated"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        await _meta.WriteAsync(cache);
        return (cache, errors);
    }

    /// <summary>Steam 元数据是否完整：有 time_updated 且有 tags 且有 file_size（对齐 Python 缺失判定）。</summary>
    private static bool MetaComplete(JsonObject? entry)
    {
        if (entry == null) return false;
        return GetLong(entry["time_updated"]) > 0
            && (entry["tags"] as JsonArray)?.Count > 0
            && GetLong(entry["file_size"]) > 0;
    }

    private async Task<List<string>> GetAllBatPathsAsync()
    {
        var data = await _store.ReadAsync();
        return data.Versions.Where(x => !string.IsNullOrEmpty(x.BatPath)).Select(x => x.BatPath).ToList();
    }

    /// <summary>定位模组 .tmod 文件，严格按当前列表上下文（不扫无关目录）。
    /// pack 模式（整合包列表 = 当前整合包 Mods ∪ 本地）：当前整合包 → 本地 → Workshop；
    /// version 模式（版本列表 = 该版本 Workshop ∪ 本地）：该版本 Workshop → 本地 → 版本 ModsPath。
    /// 本地永远最后兜底：若 Workshop / 整合包也有同名模组，优先打开它们。</summary>
    public async Task<string?> FindModFileAsync(string name, string? versionId = null, string? packId = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        if (!string.IsNullOrEmpty(packId))
        {
            // 当前整合包 Mods 目录优先（列表的整合包来源），本地次之，workshop 兜底
            var packMods = Path.Combine(_paths.GlobalModpacksPath, packId, "Mods");
            if (Directory.Exists(packMods) && TmodFinder.Find(name, packMods, "") is { } inPack)
                return inPack;
            if (TmodFinder.Find(name, _paths.DefaultModsPath, "") is { } locPack)
                return locPack;
            var wsPack = _workshop.Scan(await GetBatPathsForContextAsync(versionId));
            return wsPack.TryGetValue(name, out var wp) ? wp.Path : null;
        }

        if (!string.IsNullOrEmpty(versionId))
        {
            // 该版本 Workshop 优先（列表的 workshop 来源），本地兜底（默认 Mods → 版本 ModsPath）
            var ws = _workshop.Scan(await GetBatPathsForContextAsync(versionId));
            if (ws.TryGetValue(name, out var wv)) return wv.Path;
            if (TmodFinder.Find(name, _paths.DefaultModsPath, "") is { } locVer) return locVer;
            var data = await _store.ReadAsync();
            var v = data.Versions.FirstOrDefault(x => x.Id == versionId);
            if (v != null && !string.IsNullOrEmpty(v.ModsPath) && Directory.Exists(v.ModsPath))
                return TmodFinder.Find(name, v.ModsPath!, "");
            return null;
        }

        // 无上下文：本地 Mods + 全局整合包兜底，再 Workshop
        var local = TmodFinder.Find(name, _paths.DefaultModsPath, _paths.GlobalModpacksPath);
        if (local != null) return local;
        var wsAll = _workshop.Scan(await GetAllBatPathsAsync());
        return wsAll.TryGetValue(name, out var w) ? w.Path : null;
    }

    /// <summary>version 模式下限定该版本 bat 的 Workshop 扫描范围；否则全部版本。</summary>
    private async Task<List<string>> GetBatPathsForContextAsync(string? versionId)
    {
        if (string.IsNullOrEmpty(versionId)) return await GetAllBatPathsAsync();
        var data = await _store.ReadAsync();
        var v = data.Versions.FirstOrDefault(x => x.Id == versionId);
        return v != null && !string.IsNullOrEmpty(v.BatPath) ? new List<string> { v.BatPath } : new();
    }

    private IEnumerable<string> ScanPackModNames()
    {
        if (!Directory.Exists(_paths.GlobalModpacksPath)) yield break;
        foreach (var pack in Directory.EnumerateDirectories(_paths.GlobalModpacksPath))
        {
            var pm = Path.Combine(pack, "Mods");
            if (!Directory.Exists(pm)) continue;
            foreach (var f in Directory.EnumerateFiles(pm))
                if (f.EndsWith(".tmod", StringComparison.OrdinalIgnoreCase))
                    yield return Path.GetFileNameWithoutExtension(f);
        }
    }

    private static JsonObject ToJsonObject(IEnumerable<KeyValuePair<string, JsonNode>> pairs)
    {
        var obj = new JsonObject();
        foreach (var kv in pairs) obj[kv.Key] = kv.Value;
        return obj;
    }

    private static double GetDouble(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<long>(out var l)) return l;
        }
        return 0;
    }

    /// <summary>共享 Mods 目录的 enabled.json 里的模组名列表。</summary>
    public async Task<List<string>> ReadSharedEnabled()
        => await ReadEnabledNamesAsync(Path.Combine(_paths.DefaultModsPath, "enabled.json"));

    private static async Task<List<string>> ReadEnabledNamesAsync(string enabledFile)
    {
        if (!File.Exists(enabledFile)) return new();
        try
        {
            var json = await File.ReadAllTextAsync(enabledFile);
            return JsonSerializer.Deserialize<List<string>>(json, JsonOpts.Read) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private List<string> GetVersionBatPaths(AppData data, string? versionId, out string? versionName)
    {
        versionName = null;
        var paths = new List<string>();
        if (!string.IsNullOrEmpty(versionId))
        {
            var v = data.Versions.FirstOrDefault(x => x.Id == versionId);
            if (v != null)
            {
                versionName = v.Name;
                if (!string.IsNullOrEmpty(v.BatPath)) paths.Add(v.BatPath);
            }
        }
        if (paths.Count == 0)
            paths.AddRange(data.Versions.Where(x => !string.IsNullOrEmpty(x.BatPath)).Select(x => x.BatPath));
        return paths;
    }

    private Dictionary<string, string> GetPublishIds()
    {
        var mfd = Path.Combine(_paths.DefaultModsPath, "ModFolderData.json");
        if (!File.Exists(mfd)) return new();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(mfd));
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("PublishIds", out var pub))
                foreach (var p in pub.EnumerateObject()) map[p.Name] = p.Value.ToString();
            return map;
        }
        catch
        {
            return new();
        }
    }

    /// <summary>workshop 扫描结果 + mod_meta 缓存的 workshop_ids + PublishIds 合并成 模组名→workshop_id。</summary>
    private static Dictionary<string, string> BuildIdMap(Dictionary<string, WorkshopMod> ws, JsonObject cache)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ws.Count > 0)
        {
            foreach (var kv in ws) merged[kv.Key] = kv.Value.Id;
        }
        else if (cache["workshop_ids"] is JsonObject wids)
        {
            foreach (var p in wids) merged[p.Key] = p.Value?.ToString() ?? "";
        }
        return merged;
    }

    private List<ModEntry> Build(IEnumerable<string> names, HashSet<string> localNames, HashSet<string> wsNames,
        HashSet<string> packNames, JsonObject cache, Dictionary<string, string> idMap, double nowTs)
    {
        var meta = cache["meta"] as JsonObject ?? new JsonObject();
        var result = new List<ModEntry>();
        foreach (var n in names)
        {
            var pid = idMap.GetValueOrDefault(n);
            JsonNode? sm = null;
            if (!string.IsNullOrEmpty(pid)) sm = meta[pid];
            var steamUpdated = GetLong(sm?["time_updated"]);
            int? updDays = steamUpdated > 0 ? (int)((nowTs - steamUpdated) / 86400) : null;
            result.Add(new ModEntry
            {
                Name = n,
                HasLocal = localNames.Contains(n),
                HasWorkshop = wsNames.Contains(n),
                InPack = packNames.Contains(n),
                Conflict = localNames.Contains(n) && wsNames.Contains(n),
                UpdateDaysAgo = updDays,
                Tags = (sm?["tags"] as JsonArray)?.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() ?? new(),
                Description = sm?["description"]?.ToString() ?? "",
                Subscriptions = sm?["subscriptions"],
                Favorited = sm?["favorited"],
                Views = sm?["views"],
                TimeCreated = sm?["time_created"],
                FileSize = sm?["file_size"],
                Visibility = sm?["visibility"],
                Banned = sm?["banned"],
            });
        }
        return result;
    }

    private static long GetLong(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<double>(out var d)) return (long)d;
            if (v.TryGetValue<string>(out var s) && long.TryParse(s, out var sl)) return sl;
        }
        return 0;
    }
}
