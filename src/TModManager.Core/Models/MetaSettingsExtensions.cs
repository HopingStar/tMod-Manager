// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using System.Text.Json;

namespace TModManager.Core.Models;

/// <summary>
/// <see cref="AppData.MetaSettings"/> 是 <c>Dictionary&lt;string, JsonElement&gt;</c> 的自由字典：
/// 前端写入什么类型就存什么类型。这里把「读一个布尔 / 字符串」的样板收拢，避免每个端点各写一遍。
/// </summary>
public static class MetaSettingsExtensions
{
    public static bool GetBool(this AppData? data, string key, bool fallback = false)
    {
        if (data?.MetaSettings == null || !data.MetaSettings.TryGetValue(key, out var v)) return fallback;
        try
        {
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => v.TryGetInt32(out var n) ? n != 0 : fallback,
                JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : fallback,
                _ => fallback,
            };
        }
        catch
        {
            return fallback;
        }
    }

    public static string GetString(this AppData? data, string key, string fallback = "")
    {
        if (data?.MetaSettings == null || !data.MetaSettings.TryGetValue(key, out var v)) return fallback;
        try
        {
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? fallback,
                JsonValueKind.Number => v.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => fallback,
            };
        }
        catch
        {
            return fallback;
        }
    }
}
