using TModManager.Core.Models;

namespace TModManager.Core.Mods;

/// <summary>扫描各版本 tML 的 Steam Workshop 内容目录（1281930 = tModLoader），返回 模组名 → {id, path}。</summary>
public sealed class WorkshopScanner
{
    public Dictionary<string, WorkshopMod> Scan(IEnumerable<string> batPaths)
    {
        var result = new Dictionary<string, WorkshopMod>(StringComparer.OrdinalIgnoreCase);
        var bases = new HashSet<string>();
        foreach (var bat in batPaths)
        {
            if (!string.IsNullOrEmpty(bat) && Path.GetDirectoryName(bat) is { } dir)
                bases.Add(dir);
        }

        foreach (var baseDir in bases)
        {
            var content = Path.Combine(baseDir, "steamapps", "workshop", "content", "1281930");
            if (!Directory.Exists(content)) continue;
            try
            {
                foreach (var widDir in Directory.EnumerateDirectories(content))
                {
                    foreach (var tmod in Directory.EnumerateFiles(widDir, "*.tmod", SearchOption.AllDirectories))
                    {
                        var name = Path.GetFileNameWithoutExtension(tmod);
                        if (!result.ContainsKey(name))
                            result[name] = new WorkshopMod { Id = Path.GetFileName(widDir), Path = tmod };
                    }
                }
            }
            catch
            {
            }
        }
        return result;
    }
}
