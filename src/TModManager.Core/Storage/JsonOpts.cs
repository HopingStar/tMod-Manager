// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using System.Text.Encodings.Web;
using System.Text.Json;

namespace TModManager.Core.Storage;

/// <summary>全局 JSON 序列化选项：snake_case 命名，写入缩进 + 中文字面量（对齐 Python ensure_ascii=False, indent=2）。</summary>
public static class JsonOpts
{
    public static readonly JsonSerializerOptions Read = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static readonly JsonSerializerOptions Write = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>HTTP 响应的 JSON 选项（Kestrel 用）。</summary>
    public static readonly JsonSerializerOptions Api = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
