using System.Text.Json;
using System.Text.Json.Nodes;
using TModManager.Core.Logging;

namespace TModManager.Core.Network;

/// <summary>Steam ISteamRemoteStorage/GetPublishedFileDetails 匿名批量查询（每批 50，对齐 Python steam_query_publish_details）。
/// 结果字段保留 Steam 原始类型（如 file_size 可能是字符串）。</summary>
public sealed class SteamService
{
    private readonly HttpClient _client;

    public SteamService() : this(SafeHttpClient.Instance) { }

    /// <summary>注入自定义 HttpClient（测试可传 mock handler）。</summary>
    public SteamService(HttpClient client) => _client = client;

    private const string SteamApi = "https://api.steampowered.com";

    public async Task<SteamQueryResult> QueryPublishDetailsAsync(IReadOnlyList<string> ids)
    {
        var results = new Dictionary<string, JsonObject>();
        var errors = new Dictionary<string, string>();
        if (ids.Count == 0) return new SteamQueryResult(results, errors);

        var url = $"{SteamApi}/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
        for (int i = 0; i < ids.Count; i += 50)
        {
            var batch = ids.Skip(i).Take(50).ToList();
            var form = new Dictionary<string, string> { ["itemcount"] = batch.Count.ToString() };
            for (int j = 0; j < batch.Count; j++) form[$"publishedfileids[{j}]"] = batch[j];
            try
            {
                using var resp = await _client.PostAsync(url, new FormUrlEncodedContent(form));
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync();
                var found = new HashSet<string>();
                using (var doc = JsonDocument.Parse(body))
                {
                    if (doc.RootElement.TryGetProperty("response", out var r) &&
                        r.TryGetProperty("publishedfiledetails", out var arr) &&
                        arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in arr.EnumerateArray())
                        {
                            var pid = GetString(item, "publishedfileid");
                            if (string.IsNullOrEmpty(pid)) continue;
                            results[pid] = BuildMeta(item);
                            found.Add(pid);
                        }
                    }
                }
                // batch 中未返回的 ID：记录具体原因
                foreach (var pid in batch)
                    if (!found.Contains(pid)) errors[pid] = "未找到该模组（可能已下架、私有或 ID 错误）";
            }
            catch (Exception e)
            {
                AppLog.Warn($"Steam 批量查询失败: {e.Message}");
                foreach (var pid in batch) errors[pid] = $"网络请求失败: {e.GetType().Name}";
            }
        }
        return new SteamQueryResult(results, errors);
    }

    private static JsonObject BuildMeta(JsonElement item) => new()
    {
        ["name"] = GetString(item, "title"),
        ["icon"] = GetString(item, "preview_url"),
        ["time_updated"] = GetNode(item, "time_updated"),
        ["tags"] = GetTags(item),
        ["description"] = GetString(item, "description"),
        ["subscriptions"] = GetNode(item, "subscriptions"),
        ["favorited"] = GetNode(item, "favorited"),
        ["views"] = GetNode(item, "views"),
        ["time_created"] = GetNode(item, "time_created"),
        ["file_size"] = GetNode(item, "file_size"),
        ["visibility"] = GetNode(item, "visibility"),
        ["banned"] = GetNode(item, "banned"),
    };

    private static JsonArray GetTags(JsonElement item)
    {
        var arr = new JsonArray();
        if (item.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            foreach (var t in tags.EnumerateArray())
                if (t.TryGetProperty("tag", out var tag) && tag.ValueKind == JsonValueKind.String)
                    arr.Add(tag.GetString() ?? "");
        return arr;
    }

    private static string GetString(JsonElement item, string prop)
        => item.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>原样保留数值/字符串类型（如 file_size 可能是字符串）。</summary>
    private static JsonNode? GetNode(JsonElement item, string prop)
    {
        if (item.TryGetProperty(prop, out var v) && v.ValueKind is JsonValueKind.Number or JsonValueKind.String)
        {
            try { return JsonNode.Parse(v.GetRawText()); } catch { }
        }
        return null;
    }
}

/// <summary>Steam 查询结果：pid → 元数据 JSON；pid → 失败原因。</summary>
public sealed class SteamQueryResult
{
    public Dictionary<string, JsonObject> Results { get; }
    public Dictionary<string, string> Errors { get; }

    public SteamQueryResult(Dictionary<string, JsonObject> results, Dictionary<string, string> errors)
    {
        Results = results;
        Errors = errors;
    }
}
