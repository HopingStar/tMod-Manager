// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using TModManager.Core.Configuration;

namespace TModManager.Core.Captures;

/// <summary>一张游戏截图。</summary>
public sealed class CaptureEntry
{
    public string FileName { get; set; } = "";
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public DateTime Modified { get; set; }
}

/// <summary>
/// 游戏截图：tModLoader 存档目录下的 <c>Captures/</c>
/// （= 默认 Mods 目录的父目录，跟 Worlds / Players 同级）。
/// 只列**顶层**图片文件 —— 里面的 <c>CachedImages/</c> 是游戏自己的贴图缓存，不是玩家截图。
/// </summary>
public sealed class CapturesService
{
    private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    private readonly AppPaths _paths;

    public CapturesService(AppPaths paths) => _paths = paths;

    public string CapturesDir => Path.Combine(
        Path.GetDirectoryName(_paths.DefaultModsPath) ?? _paths.DefaultModsPath, "Captures");

    /// <summary>按修改时间从新到旧列出截图。</summary>
    public List<CaptureEntry> List()
    {
        var list = new List<CaptureEntry>();
        if (!Directory.Exists(CapturesDir)) return list;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(CapturesDir, "*", SearchOption.TopDirectoryOnly); }
        catch { return list; }

        foreach (var f in files)
        {
            if (!Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
            try
            {
                var fi = new FileInfo(f);
                list.Add(new CaptureEntry
                {
                    FileName = fi.Name,
                    Path = fi.FullName,
                    Size = fi.Length,
                    Modified = fi.LastWriteTime,
                });
            }
            catch { }
        }
        return list.OrderByDescending(x => x.Modified).ToList();
    }

    /// <summary>把请求里的 path 校验成「确实在截图目录内的图片文件」，返回绝对路径；非法返回 null。</summary>
    public string? ResolveImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        string full;
        try { full = Path.GetFullPath(path); }
        catch { return null; }

        var root = Path.GetFullPath(CapturesDir);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        if (!Extensions.Contains(Path.GetExtension(full).ToLowerInvariant())) return null;
        return File.Exists(full) ? full : null;
    }

    /// <summary>按扩展名给出 MIME 类型（只覆盖上面白名单里的几种）。</summary>
    public static string ContentTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".bmp" => "image/bmp",
        ".gif" => "image/gif",
        _ => "image/png",
    };
}
