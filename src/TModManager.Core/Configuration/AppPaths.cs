namespace TModManager.Core.Configuration;

/// <summary>应用路径与常量（关键路径与 Python 版对齐，但数据完全独立）。</summary>
public sealed class AppPaths
{
    /// <summary>运行根目录：开发期=仓库根（含 TModManager.sln），发布后=exe 目录。</summary>
    public string BaseDir { get; }

    public string DataFile => Path.Combine(BaseDir, "data.json");
    public string MetaFile => Path.Combine(BaseDir, "mod_meta.json");
    public string LogFile => Path.Combine(BaseDir, "app.log");
    public string WwwRoot => Path.Combine(BaseDir, "wwwroot");
    public string StaticRoot => Path.Combine(WwwRoot, "static");
    public string IconsDir => Path.Combine(StaticRoot, "mod_icons");

    /// <summary>默认 Mods 目录（默认 D:\Documents\...；可用环境变量覆盖）。</summary>
    public string DefaultModsPath { get; }

    /// <summary>全局整合包目录 = Mods/ModPacks。</summary>
    public string GlobalModpacksPath => Path.Combine(DefaultModsPath, "ModPacks");

    /// <summary>当前进程解析出的路径（App 启动时设置，供日志等使用）。</summary>
    public static AppPaths? Current { get; set; }

    public AppPaths(string baseDir, string? defaultModsPath = null)
    {
        BaseDir = Path.GetFullPath(baseDir);
        DefaultModsPath = string.IsNullOrEmpty(defaultModsPath) ? DetectDefaultModsPath() : defaultModsPath;
    }

    /// <summary>从 exe 目录解析运行根：向上找 TModManager.sln 定位仓库根；找不到则用 exe 目录。</summary>
    public static AppPaths Resolve(string exeDir)
    {
        string? repo = null;
        var dir = new DirectoryInfo(exeDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TModManager.sln")))
            {
                repo = dir.FullName;
                break;
            }
            dir = dir.Parent;
        }

        string baseDir = repo ?? Path.GetFullPath(exeDir);
        return new AppPaths(baseDir);
    }

    private static string DetectDefaultModsPath()
    {
        var env = Environment.GetEnvironmentVariable("TMOD_DEFAULT_MODS_PATH");
        if (!string.IsNullOrEmpty(env)) return env;
        return @"D:\Documents\My Games\Terraria\tModLoader\Mods";
    }
}
