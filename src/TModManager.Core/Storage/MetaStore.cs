// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using TModManager.Core.Configuration;

namespace TModManager.Core.Storage;

/// <summary>mod_meta.json 读写（全局门锁），结构对齐 Python：{meta, steam_api_key, updated, workshop_ids, local_meta}。</summary>
public sealed class MetaStore
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public MetaStore(AppPaths paths) => _paths = paths;

    public async Task<JsonObject> ReadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(_paths.MetaFile)) return CreateEmpty();
            try
            {
                var json = await File.ReadAllTextAsync(_paths.MetaFile);
                return JsonNode.Parse(json) as JsonObject ?? CreateEmpty();
            }
            catch
            {
                return CreateEmpty();
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task WriteAsync(JsonObject cache)
    {
        await _lock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_paths.MetaFile)!);
            // JsonNode.ToJsonString 不自动补 TypeInfoResolver（与强类型 Serialize 不同），必须显式指定，否则 .NET 8 抛异常
            var options = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            };
            await File.WriteAllTextAsync(_paths.MetaFile, cache.ToJsonString(options));
        }
        finally
        {
            _lock.Release();
        }
    }

    public static JsonObject CreateEmpty() => new()
    {
        ["meta"] = new JsonObject(),
        ["steam_api_key"] = "",
        ["updated"] = 0,
        ["workshop_ids"] = new JsonObject(),
        ["local_meta"] = new JsonObject(),
    };
}
