namespace TModManager.Core.Parsing;

/// <summary>找模组对应的 .tmod 文件路径：全局 Mods 目录（含 .tmod.disabled），再扫整合包目录。</summary>
public static class TmodFinder
{
    public static string? Find(string name, string modsPath, string packsPath)
    {
        foreach (var ext in new[] { ".tmod", ".tmod.disabled" })
        {
            var p = Path.Combine(modsPath, name + ext);
            if (File.Exists(p)) return p;
        }
        if (!string.IsNullOrEmpty(packsPath) && Directory.Exists(packsPath))
        {
            foreach (var pack in Directory.EnumerateDirectories(packsPath))
            {
                var p = Path.Combine(pack, "Mods", name + ".tmod");
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }
}
