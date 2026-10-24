using TModManager.Core.Configuration;
using TModManager.Core.Models;

namespace TModManager.Core.Logs;

/// <summary>日志文件条目（供前端列表展示）。</summary>
public sealed class LogFileEntry
{
    public string Name { get; set; } = "";

    /// <summary>绝对路径（读取内容时回传，服务端会校验它确实在候选目录内）。</summary>
    public string Path { get; set; } = "";

    /// <summary>所属日志目录，用于前端分组显示。</summary>
    public string Dir { get; set; } = "";

    public long Size { get; set; }
    public DateTime Modified { get; set; }
}

/// <summary>日志内容。</summary>
public sealed class LogContent
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Text { get; set; } = "";

    /// <summary>true = 文件过大只返回了尾部。</summary>
    public bool Truncated { get; set; }

    public int TotalLines { get; set; }

    /// <summary>返回内容中命中错误关键字的行数（行号从 1 开始，对应 Text 内部行号）。</summary>
    public List<int> ErrorLines { get; set; } = new();
}

/// <summary>
/// tModLoader 日志（tModLoader-Logs/*.log）的枚举与读取。
/// 日志目录随 tML 安装位置走：优先各版本启动脚本所在目录下的 tModLoader-Logs，
/// 其次回退到存档根（Mods 的父目录）下的 tModLoader-Logs。
/// </summary>
public sealed class CrashLogService
{
    /// <summary>单次最多返回的字节数，防止超大日志把前端拖垮。</summary>
    private const int MaxBytes = 3 * 1024 * 1024;

    /// <summary>判定为「错误行」的关键字（大小写不敏感）。</summary>
    private static readonly string[] ErrorKeywords =
    {
        "exception", "error", "fail", "fatal", "crash", "unhandled", "stack trace",
    };

    private readonly AppPaths _paths;

    public CrashLogService(AppPaths paths) => _paths = paths;

    /// <summary>候选日志目录（仅返回真实存在者）。</summary>
    public List<string> CandidateDirs(AppData data)
    {
        var dirs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? root)
        {
            if (string.IsNullOrEmpty(root)) return;
            var d = Path.Combine(root, "tModLoader-Logs");
            if (Directory.Exists(d) && seen.Add(d)) dirs.Add(d);
        }

        foreach (var v in data.Versions)
            TryAdd(Path.GetDirectoryName(v.BatPath));

        // 回退：存档根（Mods 的父目录）——部分安装把日志放在存档目录下
        TryAdd(Path.GetDirectoryName(_paths.DefaultModsPath));
        return dirs;
    }

    /// <summary>列出全部日志文件，按修改时间倒序。</summary>
    public List<LogFileEntry> List(AppData data)
    {
        var list = new List<LogFileEntry>();
        foreach (var dir in CandidateDirs(data))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.log", SearchOption.AllDirectories); }
            catch { continue; }

            foreach (var f in files)
            {
                try
                {
                    var fi = new FileInfo(f);
                    list.Add(new LogFileEntry
                    {
                        Name = fi.Name,
                        Path = fi.FullName,
                        Dir = fi.DirectoryName ?? dir,
                        Size = fi.Length,
                        Modified = fi.LastWriteTime,
                    });
                }
                catch
                {
                    // 单个文件读不到（被写入进程独占等）跳过，不影响其余
                }
            }
        }

        return list.OrderByDescending(x => x.Modified).ToList();
    }

    /// <summary>
    /// 读取指定日志。path 必须落在候选日志目录之内，否则拒绝（防任意文件读取）。
    /// 超过 MaxBytes 时只返回尾部。
    /// </summary>
    public LogContent? Read(AppData data, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        string full;
        try { full = Path.GetFullPath(path); }
        catch { return null; }

        if (!IsAllowed(data, full) || !File.Exists(full)) return null;

        byte[] bytes;
        try { bytes = File.ReadAllBytes(full); }
        catch { return null; }

        var truncated = false;
        if (bytes.Length > MaxBytes)
        {
            bytes = bytes[^MaxBytes..];
            truncated = true;
        }

        var text = System.Text.Encoding.UTF8.GetString(bytes);
        if (truncated)
        {
            // 截断点大概率落在半行上，丢掉第一行残片
            var nl = text.IndexOf('\n');
            if (nl >= 0) text = text[(nl + 1)..];
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var errorLines = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (IsErrorLine(lines[i])) errorLines.Add(i + 1);
        }

        return new LogContent
        {
            Name = Path.GetFileName(full),
            Path = full,
            Text = text.Replace("\r\n", "\n"),
            Truncated = truncated,
            TotalLines = lines.Length,
            ErrorLines = errorLines,
        };
    }

    /// <summary>返回该日志所在目录（在资源管理器中打开用）；同样做白名单校验。</summary>
    public string? ResolveDir(AppData data, string path)
    {
        var full = Read(data, path)?.Path;
        return full == null ? null : Path.GetDirectoryName(full);
    }

    public static bool IsErrorLine(string line)
    {
        if (line.Length == 0) return false;
        foreach (var kw in ErrorKeywords)
        {
            if (line.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private bool IsAllowed(AppData data, string fullPath)
    {
        foreach (var dir in CandidateDirs(data))
        {
            var root = Path.GetFullPath(dir);
            if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
