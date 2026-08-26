using TModManager.Core.Configuration;
using TModManager.Core.Models;
using TModManager.Core.Storage;

namespace TModManager.Core.Mods;

/// <summary>特殊加载项：把本地 .tmod 改名为 .tmod.disabled（优先 workshop 版本），tML 退出时还原。</summary>
public sealed class SpecialLoadService
{
    private readonly AppPaths _paths;
    private readonly JsonStore _store;

    public SpecialLoadService(AppPaths paths, JsonStore store)
    {
        _paths = paths;
        _store = store;
    }

    /// <summary>某整合包生效的禁用集合：启用独立配置用自己列表，否则跟随全局。</summary>
    public HashSet<string> PackSpecialSet(AppData data, string packId)
    {
        var cfg = data.SpecialLoads.TryGetValue(packId, out var c) ? c : new SpecialLoad();
        if (cfg.Independent || cfg.Active) return cfg.Mods.Where(x => !string.IsNullOrEmpty(x)).ToHashSet(StringComparer.Ordinal);
        return data.GlobalSpecial.Active
            ? data.GlobalSpecial.Mods.Where(x => !string.IsNullOrEmpty(x)).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>按目标禁用集合应用文件操作：集合内 .tmod → .tmod.disabled，集合外已禁用的还原。返回错误列表。</summary>
    public List<string> ApplyDisabledSet(HashSet<string> disabledSet)
    {
        var modsPath = _paths.DefaultModsPath;
        var currentDisabled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var currentEnabled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(modsPath))
        {
            foreach (var fn in Directory.EnumerateFiles(modsPath))
            {
                var file = Path.GetFileName(fn);
                var lower = file.ToLowerInvariant();
                if (lower.EndsWith(".tmod.disabled"))
                    currentDisabled[file[..^".tmod.disabled".Length]] = file;
                else if (lower.EndsWith(".tmod"))
                    currentEnabled[Path.GetFileNameWithoutExtension(file)] = file;
            }
        }

        var errors = new List<string>();
        void Toggle(string src, string dst)
        {
            try
            {
                File.Move(Path.Combine(modsPath, src), Path.Combine(modsPath, dst));
            }
            catch (Exception e)
            {
                errors.Add($"{src}: {e.Message}");
            }
        }

        foreach (var name in disabledSet)
            if (currentEnabled.TryGetValue(name, out var fn))
                Toggle(fn, fn + ".disabled");
        foreach (var kv in currentDisabled)
            if (!disabledSet.Contains(kv.Key))
                Toggle(kv.Value, kv.Value[..^".disabled".Length]);
        return errors;
    }

    /// <summary>把所有 .tmod.disabled 还原为 .tmod（tML 退出时调用）。返回还原的文件名列表。</summary>
    public List<string> RestoreAllDisabled()
    {
        var restored = new List<string>();
        if (Directory.Exists(_paths.DefaultModsPath))
        {
            foreach (var fn in Directory.EnumerateFiles(_paths.DefaultModsPath))
            {
                var file = Path.GetFileName(fn);
                if (file.ToLowerInvariant().EndsWith(".tmod.disabled"))
                {
                    try
                    {
                        File.Move(fn, fn[..^".disabled".Length]);
                        restored.Add(file);
                    }
                    catch
                    {
                    }
                }
            }
        }
        return restored;
    }
}
