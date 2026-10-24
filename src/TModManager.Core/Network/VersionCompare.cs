// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using System.Text.RegularExpressions;

namespace TModManager.Core.Network;

/// <summary>版本号逐段数值比较（对齐 Python compare_versions：提取第一段 x.y.z 数字串，容忍 v 前缀 / + 后缀）。</summary>
public static class VersionCompare
{
    private static readonly Regex VersionPart = new(@"\d+(?:\.\d+)+", RegexOptions.Compiled);

    /// <summary>返回 1(a&gt;b) / -1(a&lt;b) / 0(a==b)。</summary>
    public static int Compare(string? a, string? b)
    {
        var pa = Parts(a);
        var pb = Parts(b);
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x > y ? 1 : -1;
        }
        return 0;
    }

    /// <summary>从任意字符串提取第一段连续数字串（如 'v2026.06.3.6-preview' → '2026.06.3.6'）。无则空串。</summary>
    public static string Extract(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var m = VersionPart.Match(s);
        return m.Success ? m.Value : "";
    }

    private static int[] Parts(string? s)
    {
        var seg = Extract(s);
        if (seg.Length == 0) return new[] { 0 };
        return seg.Split('.').Select(int.Parse).ToArray();
    }
}
