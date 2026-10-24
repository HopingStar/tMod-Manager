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

    /// <summary>
    /// 从 exe 目录解析运行根。
    /// <list type="bullet">
    ///   <item>**发布 / 便携产物**：<c>wwwroot</c> 就躺在 exe 旁边 ⇒ 这是自包含的运行目录，
    ///     运行根 = exe 目录，<c>data.json</c> / <c>logs/</c> / <c>backups/</c> / <c>snapshots/</c> 全都写在 exe 旁边。</item>
    ///   <item>**开发构建**（<c>bin/Debug</c>、<c>bin/Release</c> 的输出目录没有 wwwroot）：
    ///     向上找 <c>TModManager.sln</c> 定位仓库根，用仓库里的 wwwroot（前端源码），改完前端不用重新构建。</item>
    /// </list>
    /// </summary>
    public static AppPaths Resolve(string exeDir)
    {
        var full = Path.GetFullPath(exeDir);

        if (Directory.Exists(Path.Combine(full, "wwwroot")))
            return new AppPaths(full);

        var dir = new DirectoryInfo(full);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TModManager.sln")))
                return new AppPaths(dir.FullName);
            dir = dir.Parent;
        }

        return new AppPaths(full);
    }

    private static string DetectDefaultModsPath()
    {
        var env = Environment.GetEnvironmentVariable("TMOD_DEFAULT_MODS_PATH");
        if (!string.IsNullOrEmpty(env)) return env;
        return @"D:\Documents\My Games\Terraria\tModLoader\Mods";
    }
}
