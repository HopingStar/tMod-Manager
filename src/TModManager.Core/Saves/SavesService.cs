// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using System.IO.Compression;
using TModManager.Core.Configuration;

namespace TModManager.Core.Saves;

/// <summary>一条存档（世界或人物）。</summary>
public sealed class SaveEntry
{
    /// <summary>world | player</summary>
    public string Kind { get; set; } = "";

    /// <summary>不含扩展名的显示名。</summary>
    public string Name { get; set; } = "";

    public string FileName { get; set; } = "";
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public DateTime Modified { get; set; }

    /// <summary>该存档已有的备份数量。</summary>
    public int BackupCount { get; set; }
}

/// <summary>一份备份记录。</summary>
public sealed class BackupEntry
{
    public string FileName { get; set; } = "";
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public DateTime Created { get; set; }

    /// <summary>world | player</summary>
    public string Kind { get; set; } = "";

    /// <summary>被备份的原文件名（还原时用）。</summary>
    public string OriginalName { get; set; } = "";
}

/// <summary>
/// 世界（Worlds/*.wld）与人物（Players/*.plr、*.tplr）存档管理：列举、备份为 zip、还原、删除（进回收站）。
/// 存档根 = Mods 目录的父目录（即 tModLoader 存档目录）。
/// </summary>
public sealed class SavesService
{
    private readonly AppPaths _paths;

    public SavesService(AppPaths paths) => _paths = paths;

    /// <summary>
    /// tModLoader 存档根（Worlds / Players 的父目录）。
    /// ⚠️ tModLoader 用它**自己**的存档目录（…\My Games\Terraria\tModLoader），
    /// 与原版游戏（…\My Games\Terraria）**不是同一个**，所以这里由 Mods 目录
    /// （默认 …\Terraria\tModLoader\Mods）的父目录推导 —— 绝不能回退到原版目录。
    /// </summary>
    public string SaveRoot => Path.GetDirectoryName(_paths.DefaultModsPath) ?? _paths.DefaultModsPath;

    public string WorldsDir => Path.Combine(SaveRoot, "Worlds");
    public string PlayersDir => Path.Combine(SaveRoot, "Players");

    /// <summary>备份目录（放在程序目录下，不污染游戏目录）。</summary>
    public string BackupDir => Path.Combine(_paths.BaseDir, "backups", "saves");

    // ---------------- 列举 ----------------

    public List<SaveEntry> ListWorlds() =>
        BuildEntries(Collect(WorldsDir, new[] { ".wld" }, recursive: true), "world", SaveNameReader.TryReadWorldName);

    public List<SaveEntry> ListPlayers()
    {
        // .tplr 是 tModLoader 给人物写的伴生数据文件，本体是同名 .plr ⇒ 同名时不再单独列一条
        var plr = Collect(PlayersDir, new[] { ".plr" }, recursive: false).ToList();
        var stems = plr.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orphanTplr = Collect(PlayersDir, new[] { ".tplr" }, recursive: false)
            .Where(f => !stems.Contains(Path.GetFileNameWithoutExtension(f)));
        return BuildEntries(plr.Concat(orphanTplr), "player", SaveNameReader.TryReadPlayerName);
    }

    public List<BackupEntry> ListBackups()
    {
        var list = new List<BackupEntry>();
        if (!Directory.Exists(BackupDir)) return list;

        foreach (var f in Directory.EnumerateFiles(BackupDir, "*.zip"))
        {
            try
            {
                var fi = new FileInfo(f);
                var (kind, original) = ParseBackupName(fi.Name);
                list.Add(new BackupEntry
                {
                    FileName = fi.Name,
                    Path = fi.FullName,
                    Size = fi.Length,
                    Created = fi.LastWriteTime,
                    Kind = kind,
                    OriginalName = original,
                });
            }
            catch { }
        }
        return list.OrderByDescending(x => x.Created).ToList();
    }

    /// <summary>按扩展名收集文件（枚举失败时返回空序列）。</summary>
    private static IEnumerable<string> Collect(string dir, string[] exts, bool recursive)
    {
        if (!Directory.Exists(dir)) yield break;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
        }
        catch { yield break; }

        foreach (var f in files)
        {
            if (exts.Contains(Path.GetExtension(f).ToLowerInvariant()))   // .plr.bak 的扩展名是 .bak，自然被排除
                yield return f;
        }
    }

    private List<SaveEntry> BuildEntries(IEnumerable<string> files, string kind, Func<string, string?> nameReader)
    {
        var list = new List<SaveEntry>();
        foreach (var f in files)
        {
            try
            {
                var fi = new FileInfo(f);
                var fileStem = Path.GetFileNameWithoutExtension(f);
                var internalName = nameReader(f);
                list.Add(new SaveEntry
                {
                    Kind = kind,
                    // 游戏列表显示的是文件**内部**的名字（存档改过名就会和文件名不一致）；读不到才回退文件名
                    Name = string.IsNullOrWhiteSpace(internalName) ? fileStem : internalName!,
                    FileName = fi.Name,
                    Path = fi.FullName,
                    Size = fi.Length,
                    Modified = fi.LastWriteTime,
                });
            }
            catch { }
        }

        // 附带每个存档的备份数量
        var backups = ListBackups();
        foreach (var e in list)
            e.BackupCount = backups.Count(b => string.Equals(b.OriginalName, e.FileName, StringComparison.OrdinalIgnoreCase));

        return list.OrderByDescending(x => x.Modified).ToList();
    }

    // ---------------- 操作 ----------------

    /// <summary>把指定存档备份成 zip，返回备份文件路径。path 必须在存档目录内。</summary>
    public string? Backup(string kind, string path)
    {
        var full = ValidatePath(kind, path);
        if (full == null || !File.Exists(full)) return null;

        Directory.CreateDirectory(BackupDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var zipPath = Path.Combine(BackupDir, $"{kind}__{Path.GetFileName(full)}__{stamp}.zip");

        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(full, Path.GetFileName(full), CompressionLevel.Optimal);
        }
        return zipPath;
    }

    /// <summary>从备份 zip 还原到原位置（覆盖）。返回被还原到的路径。</summary>
    public string? Restore(string backupPath)
    {
        var full = Path.GetFullPath(backupPath);
        var root = Path.GetFullPath(BackupDir);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        if (!File.Exists(full)) return null;

        var (kind, _) = ParseBackupName(Path.GetFileName(full));
        var targetDir = kind == "world" ? WorldsDir : PlayersDir;
        if (!Directory.Exists(targetDir)) return null;

        using var zip = ZipFile.OpenRead(full);
        var entry = zip.Entries.FirstOrDefault();
        if (entry == null) return null;

        // 只允许还原成原文件名（防 zip 里塞了相对路径穿越）
        var name = Path.GetFileName(entry.FullName);
        if (name.Length == 0) return null;

        var target = Path.Combine(targetDir, name);
        entry.ExtractToFile(target, overwrite: true);
        return target;
    }

    /// <summary>删除备份文件本身。</summary>
    public bool DeleteBackup(string backupPath)
    {
        var full = Path.GetFullPath(backupPath);
        var root = Path.GetFullPath(BackupDir);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        if (!File.Exists(full)) return false;
        return SendToRecycleBin(full);
    }

    /// <summary>删除存档（送进回收站，可恢复）。</summary>
    public bool DeleteSave(string kind, string path)
    {
        var full = ValidatePath(kind, path);
        if (full == null || !File.Exists(full)) return false;
        return SendToRecycleBin(full);
    }

    /// <summary>返回某个存档或存档夹应打开的目录。</summary>
    public string? ResolveOpenDir(string kind, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            var full = ValidatePath(kind, path!);
            if (full != null && File.Exists(full)) return Path.GetDirectoryName(full);
        }
        return kind == "player" ? PlayersDir : WorldsDir;
    }

    // ---------------- 内部 ----------------

    private string? ValidatePath(string kind, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return null; }

        var root = Path.GetFullPath(kind == "player" ? PlayersDir : WorldsDir);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static (string kind, string original) ParseBackupName(string fileName)
    {
        // 形如 world__MyWorld.wld__20261011-140000.zip
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var parts = stem.Split("__");
        if (parts.Length >= 3)
            return (parts[0], string.Join("__", parts.Skip(1).Take(parts.Length - 2)));
        return ("world", stem);
    }

    /// <summary>送进回收站；失败则退回直接删除。</summary>
    private static bool SendToRecycleBin(string path)
    {
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return true;
        }
        catch
        {
            try { File.Delete(path); return true; }
            catch { return false; }
        }
    }
}
