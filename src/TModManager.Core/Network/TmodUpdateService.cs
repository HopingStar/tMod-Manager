// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using System.Net.Sockets;
using System.Text.Json.Nodes;
using TModManager.Core.Logging;

namespace TModManager.Core.Network;

/// <summary>tModLoader 版本更新检测：GitHub Releases 拉取 + 缓存（成功 6h / 失败 60s 冷却）+ 离线判定。
/// 返回 Python 兼容 JSON：{ok, latest_stable:{version,days,url}, latest_preview} 或 {ok:false, reason}。</summary>
public sealed class TmodUpdateService
{
    private const double SuccessTtlSeconds = 6 * 3600;   // 成功缓存 6 小时
    private const double FailureCooldownSeconds = 60;     // 失败后 60 秒再重试

    private readonly object _lock = new();
    private readonly GitHubService _github;
    private JsonObject? _cache;
    private bool _success;
    private DateTime _fetchedAt;
    private bool _checking;

    public TmodUpdateService(GitHubService github) => _github = github;

    /// <summary>返回当前缓存（无缓存且已在后台拉取 → checking）。后台拉取完成前不阻塞。</summary>
    public Task<JsonObject> GetAsync()
    {
        bool needFetch;
        lock (_lock)
        {
            needFetch = IsStale() && !_checking;
            if (needFetch) _checking = true;
        }

        if (needFetch)
        {
            _ = Task.Run(async () =>
            {
                JsonObject result;
                try
                {
                    var r = await _github.FetchReleasesAsync();
                    result = r.Ok
                        ? new JsonObject
                        {
                            ["ok"] = true,
                            ["latest_stable"] = r.LatestStable ?? new JsonObject(),
                            ["latest_preview"] = r.LatestPreview ?? new JsonObject(),
                        }
                        : new JsonObject { ["ok"] = false, ["reason"] = r.Reason };
                }
                catch (Exception ex)
                {
                    AppLog.Warn("tModLoader 更新检测失败: " + ex.Message);
                    // 区分「离线」（无网络）与「GitHub 不可达」（有网但拉取失败）
                    result = await HasInternetAsync()
                        ? new JsonObject { ["ok"] = false, ["reason"] = "fetch_failed" }
                        : new JsonObject { ["ok"] = false, ["reason"] = "offline" };
                }
                lock (_lock)
                {
                    _cache = result;
                    _success = result["ok"]?.GetValue<bool>() ?? false;
                    _fetchedAt = DateTime.UtcNow;
                    _checking = false;
                }
            });
        }

        lock (_lock)
        {
            if (_cache == null)
                return Task.FromResult<JsonObject>(new JsonObject { ["ok"] = false, ["reason"] = "checking" });
            return Task.FromResult((JsonObject)_cache.DeepClone());
        }
    }

    private bool IsStale()
    {
        if (_cache == null) return true;
        var age = (DateTime.UtcNow - _fetchedAt).TotalSeconds;
        return _success ? age > SuccessTtlSeconds : age > FailureCooldownSeconds;
    }

    /// <summary>粗略联网探测（TCP 连国内稳定站点），用于区分离线 vs GitHub 不可达。</summary>
    private static async Task<bool> HasInternetAsync()
    {
        foreach (var host in new[] { "www.baidu.com", "www.qq.com" })
        {
            try
            {
                using var client = new TcpClient();
                var connect = client.ConnectAsync(host, 443);
                var done = await Task.WhenAny(connect, Task.Delay(3000));
                if (done == connect && client.Connected) return true;
            }
            catch
            {
            }
        }
        return false;
    }
}
