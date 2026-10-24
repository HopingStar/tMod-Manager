// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

namespace TModManager.Core.Processes;

/// <summary>精确判断进程是否为 tModLoader（对齐 Python _is_tmodloader_proc，避免误杀）。</summary>
public static class TmlProcessMatcher
{
    public static bool IsTmodLoaderProc(string? name, string? cmdline)
    {
        var n = (name ?? "").ToLowerInvariant();
        var c = (cmdline ?? "").ToLowerInvariant();
        if (n is "tmodloader.exe" or "tmodloader") return true;
        if (n is "dotnet.exe" or "dotnet") return c.Contains("tmodloader.dll", StringComparison.Ordinal);
        return false;
    }
}
