// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using System.Diagnostics;
using System.Text.RegularExpressions;
using TModManager.Core.Models;

namespace TModManager.Core.Versions;

/// <summary>tModLoader 版本号检测（FileVersionInfo + _dll_mtime 缓存，对齐 Python _auto_detect_version）。</summary>
public static class TmlVersionService
{
    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>从 .bat 路径推导 tModLoader.dll（同目录）。</summary>
    public static string? FindTmlDll(string batPath)
    {
        if (string.IsNullOrEmpty(batPath)) return null;
        var dir = Path.GetDirectoryName(batPath);
        return dir == null ? null : Path.Combine(dir, "tModLoader.dll");
    }

    /// <summary>读 DLL ProductVersion，提取 tML 版本（如 "2026.06.3.6"）；失败返回 null。</summary>
    public static string? ReadTmlVersion(string dllPath)
    {
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(dllPath);
            var m = Regex.Match(fvi.ProductVersion ?? "", @"\+(\d+(?:\.\d+)+)");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>文件修改时间（Unix 秒，对齐 os.path.getmtime）。</summary>
    public static double UnixMtime(string path)
    {
        try
        {
            return (File.GetLastWriteTimeUtc(path) - UnixEpoch).TotalSeconds;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>自动识别版本号并写回（DLL 未变且已有缓存则跳过）。</summary>
    public static void AutoDetect(VersionEntry version)
    {
        var dll = FindTmlDll(version.BatPath);
        if (dll == null || !File.Exists(dll)) return;
        var mtime = UnixMtime(dll);
        if (Math.Abs(version.DllMtime - mtime) < 1e-6 && !string.IsNullOrEmpty(version.VersionInfo))
            return; // 缓存命中
        var raw = ReadTmlVersion(dll);
        if (raw != null)
        {
            version.VersionInfo = "v" + raw;
            version.DllMtime = mtime;
        }
    }
}
