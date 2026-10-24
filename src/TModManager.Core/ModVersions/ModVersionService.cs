using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TModManager.Core.Configuration;
using TModManager.Core.Parsing;
using TModManager.Core.Storage;

namespace TModManager.Core.ModVersions;

/// <summary>业务错误：消息直接给用户看。</summary>
public sealed class ModVersionException : Exception
{
    public ModVersionException(string message) : base(message) { }
}

// ---------------------------------------------------------------- 落盘模型

/// <summary>版本列表里的一条（落盘）。</summary>
public sealed class ModVersionEntry
{
    /// <summary>模组自己的版本串（来自 .tmod 头部）。</summary>
    public string Version { get; set; } = "";

    /// <summary>构建该版本所用的 tModLoader 版本 —— 太旧的可能加载不了。</summary>
    public string Build { get; set; } = "";

    /// <summary>内容哈希，版本列表的**真正主键**（同一个版本号可能对应不同构建）。</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>
    /// <c>pool</c> = 内容在快照仓库里；
    /// <c>in_place</c> = 内容**就是 Mods 目录里那个文件**（只在「首次收养当前版本」时出现，一个字节都不复制）。
    /// </summary>
    public string Stored { get; set; } = "pool";

    /// <summary>pool 时的仓库内文件名（相对 <c>mod_snapshots/&lt;内部名&gt;/</c>）。</summary>
    public string File { get; set; } = "";

    public long Size { get; set; }

    /// <summary>这份是从哪个文件名来的（溯源用）。</summary>
    public string OriginalFile { get; set; } = "";

    public long Added { get; set; }

    /// <summary>来源描述：本地 / 拖入 / 仲裁回收。</summary>
    public string Source { get; set; } = "";
}

/// <summary>文件指纹缓存：避免每次打开界面都重算 100 MB 级的 sha256。</summary>
public sealed class FileFingerprint
{
    public string File { get; set; } = "";
    public long Size { get; set; }
    public long Mtime { get; set; }
    public string Sha256 { get; set; } = "";
}

/// <summary>一个模组的版本列表（落盘为 <c>mod_snapshots/&lt;内部名&gt;.json</c>）。</summary>
public sealed class ModVersionIndex
{
    public string InternalName { get; set; } = "";
    public List<ModVersionEntry> Versions { get; set; } = new();
    public List<FileFingerprint> Prints { get; set; } = new();

    /// <summary>指纹缓存被更新过，需要落盘。**不序列化**。</summary>
    [JsonIgnore] public bool Dirty { get; set; }
}

// ---------------------------------------------------------------- 视图模型

/// <summary>列表里的一行。</summary>
public sealed class ModVersionRow
{
    public string InternalName { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public string CurrentBuild { get; set; } = "";

    /// <summary>ok / duplicate（同名冲突）/ unarchived（当前版本未归档）/ missing（只在快照里）。</summary>
    public string State { get; set; } = "ok";

    public int VersionCount { get; set; }
    public int LocalFileCount { get; set; }
    public long SnapshotBytes { get; set; }

    /// <summary>是否存在「≠ 当前版本」的条目 —— 只有它为真那一行才该画回滚按钮。</summary>
    public bool HasRollback { get; set; }
}

/// <summary>详情里的一条版本。</summary>
public sealed class ModVersionItem
{
    public string Version { get; set; } = "";
    public string Build { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Stored { get; set; } = "";
    public long Size { get; set; }
    public long Added { get; set; }
    public string OriginalFile { get; set; } = "";
    public string Source { get; set; } = "";

    /// <summary>它就是 Mods 目录里正在用的那一份。</summary>
    public bool IsCurrent { get; set; }

    /// <summary>视图层合成、**尚未落盘**（该模组还没做过任何动作）。</summary>
    public bool Adopted { get; set; }
}

/// <summary>Mods 目录里属于该模组的一个文件。</summary>
public sealed class LocalFileItem
{
    public string FileName { get; set; } = "";
    public string Version { get; set; } = "";
    public string Build { get; set; } = "";
    public long Size { get; set; }
    public bool Disabled { get; set; }

    /// <summary>内容是否已经在版本列表里。</summary>
    public bool Archived { get; set; }
}

public sealed class ModVersionDetail
{
    public string InternalName { get; set; } = "";
    public string State { get; set; } = "ok";
    public List<ModVersionItem> Versions { get; set; } = new();
    public List<LocalFileItem> LocalFiles { get; set; } = new();
    public string? CurrentSha { get; set; }
    public bool HasRollback { get; set; }
    public string ModsDir { get; set; } = "";
    public string SnapshotDir { get; set; } = "";
}

public sealed class ModVersionPayload
{
    public List<ModVersionRow> Mods { get; set; } = new();
    public string ModsDir { get; set; } = "";
    public string SnapshotDir { get; set; } = "";
    public long SnapshotBytes { get; set; }
}

// ---------------------------------------------------------------- 服务

/// <summary>
/// 本地模组版本回滚。**只针对本地 Mods 目录**（<see cref="AppPaths.DefaultModsPath"/>）。
///
/// 仓库布局（人可读，可直接用资源管理器整理/备份）：
/// <code>
/// &lt;程序目录&gt;/mod_snapshots/
///     CalamityMod.json      ← 版本列表（真源）
///     CalamityMod/
///         0.2.tmod          ← 被实际归档过的版本内容
///         0.3.tmod
/// </code>
///
/// 三条不变量：
/// <list type="number">
///   <item>模组身份 = **内部 ID**，不是文件名（本机实测 4/17 个文件名与内部名不符）；</item>
///   <item>**版本列表的条数只由「首次收养 / 添加 / 删除」改变，回滚永不改变条数**；</item>
///   <item>「当前版本」**不落库**，靠内容（sha256）命中判定 —— 用户手动动过文件也能如实反映。</item>
/// </list>
///
/// 复制的唯一触发条件：**某个版本的内容即将离开 Mods 目录、而它还没被另存过**。
/// 所以「用户从没添加过版本」的模组，一个字节都不会被复制。
/// </summary>
public sealed class ModVersionService
{
    private const string InPlace = "in_place";
    private const string Pool = "pool";

    private readonly AppPaths _paths;

    public ModVersionService(AppPaths paths) => _paths = paths;

    public string ModsDir => _paths.DefaultModsPath;

    /// <summary>快照仓库根目录（程序目录/mod_snapshots）。</summary>
    public string Root => Path.Combine(_paths.BaseDir, "mod_snapshots");

    private string IndexPath(string name) => Path.Combine(Root, name + ".json");
    private string PoolDir(string name) => Path.Combine(Root, name);

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ------------------------------------------------------------ 名字与路径安全

    /// <summary>内部 ID 会被当成目录名/文件名，必须挡住目录穿越与非法字符。</summary>
    private static bool IsSafeName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length > 100) return false;
        if (s.Contains("..")) return false;
        if (s.All(c => c == '.')) return false;
        foreach (var c in s)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')) return false;
        return true;
    }

    /// <summary>版本串当文件名前的消毒：非法字符换成下划线，空串兜底。</summary>
    private static string SanitizeVersion(string v)
    {
        var sb = new StringBuilder();
        foreach (var c in v)
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        var s = sb.ToString().Trim('.');
        if (s.Length == 0) s = "version";
        return s.Length > 60 ? s[..60] : s;
    }

    private static bool ShaEq(string? a, string? b)
        => a is { Length: > 0 } && b is { Length: > 0 } && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>同卷内「写临时文件 → 原子改名」，中断也不会留下半截正式文件。</summary>
    private static void CopyAtomic(string src, string dest)
    {
        var dir = Path.GetDirectoryName(dest)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(dest) + ".tmp");
        File.Copy(src, tmp, overwrite: true);
        File.Move(tmp, dest, overwrite: true);
    }

    private static string Sha256OfFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    private static long MtimeOf(string path)
    {
        try { return File.GetLastWriteTimeUtc(path).Ticks; } catch { return 0; }
    }

    // ------------------------------------------------------------ 扫描

    private sealed record LocalFile(string Path, string FileName, bool Disabled,
        string Name, string Version, string Build, long Size);

    /// <summary>扫 Mods 目录顶层的 .tmod / .tmod.disabled，只读文件头（不解压）。</summary>
    private List<LocalFile> ScanLocal()
    {
        var list = new List<LocalFile>();
        if (!Directory.Exists(ModsDir)) return list;
        foreach (var full in Directory.EnumerateFiles(ModsDir, "*", SearchOption.TopDirectoryOnly))
        {
            var fn = Path.GetFileName(full);
            var lower = fn.ToLowerInvariant();
            bool disabled;
            if (lower.EndsWith(".tmod.disabled")) disabled = true;
            else if (lower.EndsWith(".tmod")) disabled = false;
            else continue;

            var h = TmodParser.ParseHeader(full);
            if (h is null) continue;                       // 头都读不出来的，tML 也加载不了
            long size = 0;
            try { size = new FileInfo(full).Length; } catch { }
            list.Add(new LocalFile(full, fn, disabled, h.Value.Name, h.Value.Version, h.Value.Build, size));
        }
        return list;
    }

    private ModVersionIndex? ReadIndex(string name)
    {
        if (!IsSafeName(name)) return null;
        var p = IndexPath(name);
        if (!File.Exists(p)) return null;
        try
        {
            var idx = JsonSerializer.Deserialize<ModVersionIndex>(File.ReadAllText(p), JsonOpts.Read);
            if (idx == null) return null;
            if (string.IsNullOrEmpty(idx.InternalName)) idx.InternalName = name;
            // 文件名就是身份：内部名对不上 → 这不是我们要的那份索引
            // （比如用户往 mod_snapshots/ 里丢的杂物 .json，别让它变成一个幽灵模组）
            return string.Equals(idx.InternalName, name, StringComparison.OrdinalIgnoreCase) ? idx : null;
        }
        catch { return null; }
    }

    private void WriteIndex(ModVersionIndex idx)
    {
        Directory.CreateDirectory(Root);
        // 顺手清掉已不存在文件的指纹
        idx.Prints.RemoveAll(p =>
        {
            try { return !File.Exists(Path.Combine(ModsDir, p.File)); } catch { return true; }
        });

        var p = IndexPath(idx.InternalName);
        var tmp = p + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(idx, JsonOpts.Write));
        File.Move(tmp, p, overwrite: true);
    }

    private void DeleteIndex(string name) { try { File.Delete(IndexPath(name)); } catch { } }

    /// <summary>
    /// 取 sha256：命中 (文件名, 大小, mtime) 缓存就直接复用，不重算。
    /// 重算出来时把新指纹记进索引并标记 dirty —— 否则用户手改过 .tmod 之后，
    /// **每次**打开界面都要重算一次 100 MB 级的哈希。
    /// ⚠️ 这里**绝不写盘**：`AddVersion` 之类的操作会先调它、再继续改 `Versions`，
    /// 若此刻落盘就会把「还没加完的版本列表」写出去（索引被写空过）。
    /// 落盘交给调用方收尾时做。
    /// </summary>
    private string ShaOf(LocalFile f, ModVersionIndex? idx)
    {
        var mtime = MtimeOf(f.Path);
        var p = idx?.Prints.FirstOrDefault(x => string.Equals(x.File, f.FileName, StringComparison.OrdinalIgnoreCase));
        if (p != null && p.Size == f.Size && p.Mtime == mtime && p.Sha256.Length > 0) return p.Sha256;

        string sha;
        try { sha = Sha256OfFile(f.Path); } catch { return ""; }

        if (idx != null)
        {
            idx.Prints.RemoveAll(x => string.Equals(x.File, f.FileName, StringComparison.OrdinalIgnoreCase));
            idx.Prints.Add(new FileFingerprint { File = f.FileName, Size = f.Size, Mtime = mtime, Sha256 = sha });
            idx.Dirty = true;
        }
        return sha;
    }

    /// <summary>仅供**读路径**收尾时用：把新算出来的指纹缓存落盘（失败静默）。</summary>
    private void TryPersistPrints(ModVersionIndex? idx)
    {
        if (idx is not { Dirty: true } || !IsSafeName(idx.InternalName)) return;
        try { WriteIndex(idx); } catch { }
    }

    /// <summary>仓库里某个 pool 版本的绝对路径；写回了 <paramref name="entry"/> 的文件名。</summary>
    private string? PoolPathOf(string name, ModVersionEntry entry)
    {
        if (entry.File.Length == 0) return null;
        var p = Path.Combine(PoolDir(name), entry.File);
        return File.Exists(p) ? p : null;
    }

    /// <summary>
    /// 仓库内文件名：**沿用加入时的原文件名** —— 快照本来就是那个文件的一份拷贝，名字保持一样最好认
    /// （本机真实情况：`2026.6CalamityMod.tmod` / `CalamityMod_final_update.tmod`）。
    /// 认不出名字（为空 / 含非法字符）时才退回 <c>&lt;版本&gt;.tmod</c>；撞名且内容不同才挂短哈希。
    /// </summary>
    private string UniquePoolFileName(string modName, string? originalFileName, string version, string sha)
    {
        var stem = SafePoolStem(originalFileName) ?? SanitizeVersion(version);
        var dir = PoolDir(modName);

        var candidate = stem + ".tmod";
        var full = Path.Combine(dir, candidate);
        if (!File.Exists(full)) return candidate;
        try { if (ShaEq(Sha256OfFile(full), sha)) return candidate; } catch { }

        var suffix = sha.Length >= 8 ? sha[..8] : "alt";
        return $"{stem}_{suffix}.tmod";
    }

    /// <summary>原文件名 → 能安全当仓库文件名用的「主干」（不含扩展名）；认不出来返回 null。</summary>
    private static string? SafePoolStem(string? originalFileName)
    {
        if (string.IsNullOrWhiteSpace(originalFileName)) return null;

        var name = Path.GetFileName(originalFileName);          // 抹掉任何路径成分
        if (name.EndsWith(".tmod.disabled", StringComparison.OrdinalIgnoreCase))
            name = name[..^".tmod.disabled".Length];            // 仓库里不保留「已禁用」这层意思
        else if (name.EndsWith(".tmod", StringComparison.OrdinalIgnoreCase))
            name = name[..^".tmod".Length];

        if (name.Length is 0 or > 110) return null;
        if (name.Trim('.').Length == 0) return null;
        foreach (var c in Path.GetInvalidFileNameChars())
            if (name.Contains(c)) return null;
        return name;
    }

    // ------------------------------------------------------------ 读

    /// <summary>全部模组（Mods 目录 ∪ 版本列表）。</summary>
    public ModVersionPayload List()
    {
        var local = ScanLocal();
        var groups = local.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                          .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in local) if (seen.Add(g.Name)) names.Add(g.Name);
        if (Directory.Exists(Root))
            foreach (var f in Directory.EnumerateFiles(Root, "*.json", SearchOption.TopDirectoryOnly))
            {
                var n = Path.GetFileNameWithoutExtension(f);
                if (seen.Contains(n)) continue;
                // **真的读一遍索引**才算数：Windows 的 `*.json` 通配符会把 `Foo.json.bak` 之类
                // 也匹配进来，用户的杂物/备份文件不能变成幽灵模组。
                if (ReadIndex(n) == null) continue;
                seen.Add(n);
                names.Add(n);
            }

        var rows = new List<ModVersionRow>();
        foreach (var name in names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var files = groups.TryGetValue(name, out var g) ? g : new List<LocalFile>();
            rows.Add(BuildRow(name, files));
        }

        return new ModVersionPayload
        {
            Mods = rows,
            ModsDir = ModsDir,
            SnapshotDir = Root,
            SnapshotBytes = SnapshotBytes(),
        };
    }

    private ModVersionRow BuildRow(string name, List<LocalFile> files)
    {
        var idx = ReadIndex(name);
        var row = new ModVersionRow
        {
            InternalName = idx?.InternalName ?? name,
            LocalFileCount = files.Count,
            VersionCount = idx?.Versions.Count ?? (files.Count == 1 ? 1 : 0),
            SnapshotBytes = idx?.Versions.Where(v => v.Stored == Pool).Sum(v => v.Size) ?? 0,
        };

        if (files.Count == 0)
        {
            row.State = "missing";                       // 只在快照里，Mods 目录没有 → 可以「安装」
            row.HasRollback = row.VersionCount > 0;
            return row;
        }

        if (files.Count > 1)
        {
            row.State = "duplicate";                     // 同名冲突：tML 只会加载其中一个，且顺序不确定
            var pick = files.FirstOrDefault(f => !f.Disabled) ?? files[0];
            row.CurrentVersion = pick.Version;
            row.CurrentBuild = pick.Build;
            row.HasRollback = row.VersionCount > 0;
            return row;
        }

        var f0 = files[0];
        row.CurrentVersion = f0.Version;
        row.CurrentBuild = f0.Build;

        if (idx == null)
        {
            // 该模组还没做过任何动作：**视图层收养**当前版本 —— 不落盘、不复制、不算哈希
            row.State = "ok";
            row.VersionCount = 1;
            row.HasRollback = false;
            return row;
        }

        var sha = ShaOf(f0, idx);
        var cur = idx.Versions.FirstOrDefault(v => ShaEq(v.Sha256, sha));
        if (cur != null)
        {
            row.State = "ok";
            row.CurrentVersion = cur.Version;
            row.CurrentBuild = cur.Build;
        }
        else
        {
            row.State = "unarchived";                    // 当前版本没归档，回滚会覆盖掉它
        }
        row.HasRollback = idx.Versions.Any(v => !ShaEq(v.Sha256, sha));
        TryPersistPrints(idx);
        return row;
    }

    /// <summary>某个模组的详情；模组与版本列表都不存在时返回 null。</summary>
    public ModVersionDetail? Detail(string name)
    {
        if (!IsSafeName(name)) return null;
        var files = ScanLocal().Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        var idx = ReadIndex(name);
        if (files.Count == 0 && idx == null) return null;

        var detail = new ModVersionDetail
        {
            InternalName = idx?.InternalName ?? files[0].Name,
            ModsDir = ModsDir,
            SnapshotDir = Root,
        };

        // ⚠️ 索引不存在（用户还没对这个模组做过任何动作）时**一个 sha256 都不要算** ——
        // 「内容在不在列表里」这个问题的答案恒为「不在」，而算一次 sha256 要把整个文件读一遍
        // （本机 CalamityMod 单版本 114 MB，两个同名文件就是 2.2 秒）。列表路径同理。
        foreach (var f in files)
        {
            var sha = idx == null ? "" : ShaOf(f, idx);
            detail.LocalFiles.Add(new LocalFileItem
            {
                FileName = f.FileName,
                Version = f.Version,
                Build = f.Build,
                Size = f.Size,
                Disabled = f.Disabled,
                Archived = idx != null && idx.Versions.Any(v => ShaEq(v.Sha256, sha)),
            });
        }

        detail.CurrentSha = files.Count == 1 && idx != null ? ShaOf(files[0], idx) : null;

        detail.State = files.Count switch
        {
            0 => "missing",
            > 1 => "duplicate",
            _ => idx == null ? "ok" : (detail.LocalFiles[0].Archived ? "ok" : "unarchived"),
        };

        if (idx == null)
        {
            // 视图层收养（不落盘、不复制）
            if (files.Count == 1)
                detail.Versions.Add(new ModVersionItem
                {
                    Version = files[0].Version,
                    Build = files[0].Build,
                    Sha256 = detail.CurrentSha ?? "",
                    Stored = InPlace,
                    Size = files[0].Size,
                    OriginalFile = files[0].FileName,
                    Source = "本地",
                    Adopted = true,
                    IsCurrent = true,
                });
        }
        else
        {
            foreach (var v in idx.Versions)
                detail.Versions.Add(new ModVersionItem
                {
                    Version = v.Version,
                    Build = v.Build,
                    Sha256 = v.Sha256,
                    Stored = v.Stored,
                    Size = v.Size,
                    Added = v.Added,
                    OriginalFile = v.OriginalFile,
                    Source = v.Source,
                    IsCurrent = detail.CurrentSha != null && ShaEq(v.Sha256, detail.CurrentSha),
                });
        }

        detail.HasRollback = detail.Versions.Any(v => !v.IsCurrent);
        TryPersistPrints(idx);
        return detail;
    }

    /// <summary>仓库占用（真实磁盘字节）。</summary>
    public long SnapshotBytes()
    {
        if (!Directory.Exists(Root)) return 0;
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                try { total += new FileInfo(f).Length; } catch { }
        }
        catch { }
        return total;
    }

    // ------------------------------------------------------------ 写

    /// <summary>
    /// 把一个 .tmod 加进某模组的版本列表（拖入）。**只归档拖入的这一份**，不动 Mods 目录。
    /// <paramref name="originalFileName"/> 用于 Drag &amp; Drop 那条路：文件是先上传成临时文件再进来的，
    /// 得把用户那边的真实文件名单独带过来做溯源。
    /// </summary>
    public ModVersionDetail AddVersion(string name, string srcPath, string? originalFileName = null)
    {
        if (!IsSafeName(name)) throw new ModVersionException("模组名非法");
        if (string.IsNullOrWhiteSpace(srcPath) || !File.Exists(srcPath))
            throw new ModVersionException("文件不存在");

        var header = TmodParser.ParseHeader(srcPath)
            ?? throw new ModVersionException("这不是一个有效的 .tmod 文件（文件头读不出来）");
        if (!string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))
            throw new ModVersionException($"这是「{header.Name}」的文件，不是「{name}」");

        var srcFull = Path.GetFullPath(srcPath);
        string modsFull;
        try { modsFull = Path.GetFullPath(ModsDir); } catch { modsFull = ModsDir; }
        if (string.Equals(Path.GetDirectoryName(srcFull), modsFull, StringComparison.OrdinalIgnoreCase))
            throw new ModVersionException("这一份就在 Mods 目录里，不用再添加");

        var sha = Sha256OfFile(srcFull);
        var size = new FileInfo(srcFull).Length;

        var existing = ReadIndex(name);
        var idx = existing ?? new ModVersionIndex { InternalName = name };

        // 首次使用这个功能：先把 Mods 目录里的当前版本「收养」为 in_place（只登记，不复制）。
        // ⚠️ 必须排在「重复检查」之前 —— 否则拖进来的就是 Mods 里那一份时，索引还是空的，
        // 重复检查查不到任何东西，同一份内容会被存进快照仓库第二遍。
        if (existing == null) AdoptCurrent(name, idx);

        if (idx.Versions.Any(v => ShaEq(v.Sha256, sha)))
            throw new ModVersionException($"这一份的内容已经在版本列表里了（{idx.Versions.First(v => ShaEq(v.Sha256, sha)).Version}）");

        var originalName = string.IsNullOrWhiteSpace(originalFileName) ? Path.GetFileName(srcFull) : originalFileName;

        Directory.CreateDirectory(PoolDir(name));
        var poolFile = UniquePoolFileName(name, originalName, header.Version, sha);
        CopyAtomic(srcFull, Path.Combine(PoolDir(name), poolFile));

        idx.Versions.Add(new ModVersionEntry
        {
            Version = header.Version,
            Build = header.Build,
            Sha256 = sha,
            Stored = Pool,
            File = poolFile,
            Size = size,
            OriginalFile = originalName,
            Added = Now(),
            Source = "拖入",
        });

        WriteIndex(idx);
        return Detail(name)!;
    }

    /// <summary>把当前 Mods 目录里的那个文件登记为 in_place（**不复制内容**）。</summary>
    private void AdoptCurrent(string name, ModVersionIndex idx)
    {
        var files = ScanLocal().Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count != 1) return;
        var f = files[0];

        var sha = ShaOf(f, idx);
        if (sha.Length == 0) return;

        idx.Versions.Add(new ModVersionEntry
        {
            Version = f.Version,
            Build = f.Build,
            Sha256 = sha,
            Stored = InPlace,
            Size = f.Size,
            OriginalFile = f.FileName,
            Added = Now(),
            Source = "本地",
        });
    }

    /// <summary>
    /// 回滚：把版本列表里某一版的内容写回 Mods 目录。
    ///
    /// ⚠️ **本操作永远不改变版本列表的条数** —— 当前那份若还没被另存过，会先被实体化进仓库
    /// （只改那一条的 <c>stored</c>，不新增条目）。
    /// ⚠️ 落盘的文件名 = **目标版本原来那个文件名**（不是内部名，也不是当前文件名）；
    /// 启用/禁用状态则跟着回滚前的状态走。
    /// </summary>
    public ModVersionDetail Rollback(string name, string sha256, bool force)
    {
        if (!IsSafeName(name)) throw new ModVersionException("模组名非法");
        var idx = ReadIndex(name) ?? throw new ModVersionException("该模组还没有任何版本快照");
        var target = idx.Versions.FirstOrDefault(v => ShaEq(v.Sha256, sha256))
                     ?? throw new ModVersionException("该版本不在版本列表里");

        var files = ScanLocal().Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count > 1)
            throw new ModVersionException("Mods 目录里有多个同名模组文件，请先用「设为当前版本」仲裁");

        var blob = PoolPathOf(name, target);
        if (blob == null)
            throw new ModVersionException(target.Stored == InPlace
                ? $"「{target.Version}」还没有另存过 —— 它的内容就是 Mods 目录里那个文件，而现在那个文件已经不在了（被删掉或被换成了别的）。"
                : $"快照文件已丢失（{target.Version}）。");

        if (files.Count == 1)
        {
            var cur = files[0];
            var curSha = ShaOf(cur, idx);
            var curEntry = idx.Versions.FirstOrDefault(v => ShaEq(v.Sha256, curSha));

            if (curEntry == null && !force)
                throw new ModVersionException(
                    $"Mods 目录里当前的版本（{cur.Version}）还没有归档，回滚后它会丢失。" +
                    "请先把它「添加到版本列表」，或确认放弃。");

            if (curEntry is { Stored: InPlace })
            {
                // 实体化：它的载体马上要被覆盖了 —— 复制进仓库，只改这一条，条数不变
                Directory.CreateDirectory(PoolDir(name));
                var poolName = UniquePoolFileName(name, curEntry.OriginalFile, curEntry.Version, curSha);
                CopyAtomic(cur.Path, Path.Combine(PoolDir(name), poolName));
                curEntry.Stored = Pool;
                curEntry.File = poolName;
                curEntry.Sha256 = curSha;
                curEntry.Size = cur.Size;
            }
        }

        // 回滚 = 把那一版的**内容和它原来的文件名一起**放回 Mods 目录。
        // 文件名本身就是这个版本的标识（本机真实情况：`2026.6CalamityMod.tmod` / `CalamityMod_final_update.tmod`），
        // 统一改成 `<内部名>.tmod` 等于把这份信息抹掉；沿用「当前文件叫什么」也不行 —— 那样回滚完看不出换过版本。
        //
        // ⚠️ 启用/禁用是**另一条轴**，跟着回滚【前】的状态走，不跟着版本走 ——
        // 滚一次版本不该顺手把模组启用/禁用掉。
        var keepDisabled = files.Count == 1 && files[0].Disabled;
        var targetName = (SafePoolStem(target.OriginalFile) ?? SanitizeVersion(target.Version))
                         + (keepDisabled ? ".tmod.disabled" : ".tmod");
        var targetPath = Path.Combine(ModsDir, targetName);
        CopyAtomic(blob, targetPath);

        // 同一内部名在 Mods 目录里只能留一个文件 —— 旧名字那个删掉。
        // 它的内容此刻一定在快照里：要么本来就在，要么上面刚实体化过（否则早就抛异常了）。
        foreach (var f in files)
            if (!string.Equals(f.FileName, targetName, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(f.Path); } catch { }

        // 刚落盘的内容我们是知道哈希的，直接种进指纹缓存 —— 否则紧接着的 Detail() 会白读一遍 100 MB 级文件
        idx.Prints.RemoveAll(p => string.Equals(p.File, targetName, StringComparison.OrdinalIgnoreCase));
        idx.Prints.Add(new FileFingerprint
        {
            File = targetName,
            Size = target.Size,
            Mtime = MtimeOf(targetPath),
            Sha256 = target.Sha256,
        });
        WriteIndex(idx);
        return Detail(name)!;
    }

    /// <summary>
    /// 「设为当前版本」：保留选中的文件，**其余的收进快照**（先复制进仓库、成功后再删源），
    /// 解决 Mods 目录里的同名冲突 —— tML 只会加载其中一个，且顺序不确定。
    /// </summary>
    public ModVersionDetail SetCurrent(string name, string fileName)
    {
        if (!IsSafeName(name)) throw new ModVersionException("模组名非法");
        var files = ScanLocal().Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        var keep = files.FirstOrDefault(f => string.Equals(f.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ModVersionException("该文件不属于这个模组");
        if (files.Count < 2) throw new ModVersionException("没有可仲裁的同名文件");

        var idx = ReadIndex(name) ?? new ModVersionIndex { InternalName = name };
        Directory.CreateDirectory(PoolDir(name));

        foreach (var f in files.Where(x => x != keep))
        {
            var sha = ShaOf(f, idx);
            if (sha.Length > 0 && !idx.Versions.Any(v => ShaEq(v.Sha256, sha)))
            {
                var poolName = UniquePoolFileName(name, f.FileName, f.Version, sha);
                CopyAtomic(f.Path, Path.Combine(PoolDir(name), poolName));   // 先落仓库
                idx.Versions.Add(new ModVersionEntry
                {
                    Version = f.Version,
                    Build = f.Build,
                    Sha256 = sha,
                    Stored = Pool,
                    File = poolName,
                    Size = f.Size,
                    OriginalFile = f.FileName,
                    Added = Now(),
                    Source = "仲裁回收",
                });
            }
            File.Delete(f.Path);                          // 确认落好之后再删源
        }

        var keepSha = ShaOf(keep, idx);
        if (keepSha.Length > 0 && !idx.Versions.Any(v => ShaEq(v.Sha256, keepSha)))
            idx.Versions.Add(new ModVersionEntry
            {
                Version = keep.Version,
                Build = keep.Build,
                Sha256 = keepSha,
                Stored = InPlace,
                Size = keep.Size,
                OriginalFile = keep.FileName,
                Added = Now(),
                Source = "本地",
            });

        WriteIndex(idx);
        return Detail(name)!;
    }

    /// <summary>从版本列表里删掉一条（当前版本不能删）。</summary>
    public ModVersionDetail? DeleteVersion(string name, string sha256)
    {
        if (!IsSafeName(name)) throw new ModVersionException("模组名非法");
        var idx = ReadIndex(name) ?? throw new ModVersionException("该模组还没有版本列表");
        var e = idx.Versions.FirstOrDefault(v => ShaEq(v.Sha256, sha256))
                ?? throw new ModVersionException("该版本不在列表里");
        if (e.Stored == InPlace)
            throw new ModVersionException("这是 Mods 目录里正在使用的版本，不能删除");

        var blob = PoolPathOf(name, e);
        if (blob != null) try { File.Delete(blob); } catch { }
        idx.Versions.Remove(e);

        if (idx.Versions.Count == 0)
        {
            DeleteIndex(name);
            try { if (Directory.Exists(PoolDir(name)) && !Directory.EnumerateFileSystemEntries(PoolDir(name)).Any())
                Directory.Delete(PoolDir(name)); } catch { }
        }
        else WriteIndex(idx);

        return Detail(name);
    }
}
