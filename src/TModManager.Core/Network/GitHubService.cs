// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using System.Text.Json;
using System.Text.Json.Nodes;

namespace TModManager.Core.Network;

/// <summary>GitHub Releases API：拉取 tModLoader 最新 stable / preview 版本。</summary>
public sealed class GitHubService
{
    private readonly HttpClient _client;

    public GitHubService() : this(SafeHttpClient.Instance) { }

    /// <summary>注入自定义 HttpClient（测试可传 mock handler）。</summary>
    public GitHubService(HttpClient client) => _client = client;

    private const string ReleasesUrl = "https://api.github.com/repos/tModLoader/tModLoader/releases?per_page=20";

    /// <summary>拉取 releases 并按序取第一个 stable / preview。失败抛异常（由调用方判定离线/fetch_failed）。</summary>
    public async Task<GitHubReleaseResult> FetchReleasesAsync()
    {
        using var resp = await _client.GetAsync(ReleasesUrl);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub API status {(int)resp.StatusCode}");
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        JsonObject? stable = null, preview = null;
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            var tag = TryGetString(rel, "tag_name");
            var ver = VersionCompare.Extract(tag);
            if (ver.Length == 0) continue;

            var item = new JsonObject
            {
                ["version"] = ver,
                ["days"] = DaysSince(TryGetString(rel, "published_at")),
                ["url"] = TryGetString(rel, "html_url"),
            };
            var prerelease = rel.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True;
            if (prerelease) preview ??= item;
            else stable ??= item;
            if (stable != null && preview != null) break;
        }

        if (stable == null && preview == null)
            return new GitHubReleaseResult { Ok = false, Reason = "no_release" };
        return new GitHubReleaseResult { Ok = true, LatestStable = stable, LatestPreview = preview };
    }

    /// <summary>ISO 时间串（UTC）→ 距今天数。解析失败返回 null。</summary>
    private static int? DaysSince(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return null;
        if (DateTimeOffset.TryParse(iso, out var dto))
            return (int)Math.Floor((DateTimeOffset.UtcNow - dto).TotalDays);
        return null;
    }

    private static string TryGetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

/// <summary>GitHub Releases 拉取结果。</summary>
public sealed class GitHubReleaseResult
{
    public bool Ok { get; set; }
    public string Reason { get; set; } = "";
    public JsonObject? LatestStable { get; set; }
    public JsonObject? LatestPreview { get; set; }
}
