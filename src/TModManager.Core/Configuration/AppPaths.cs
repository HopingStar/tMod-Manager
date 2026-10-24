// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

namespace TModManager.Core.Configuration;

/// <summary>应用路径与常量（关键路径与 Python 版对齐，但数据完全独立）。</summary>
public sealed class AppPaths
{
    /// <summary>运行根目录（数据落在哪）：开发期=仓库根；发布后=exe 目录。</summary>
    public string BaseDir { get; }

    public string DataFile => Path.Combine(BaseDir, "data.json");
    public string MetaFile => Path.Combine(BaseDir, "mod_meta.json");
    public string LogFile => Path.Combine(BaseDir, "app.log");

    /// <summary>
    /// 前端根目录。**单文件发布时它指向运行时解压出来的临时目录**，而不是 <see cref="BaseDir"/> 下面
    /// —— 所以不要拿它拼数据路径。
    /// </summary>
    public string WwwRoot { get; }

    public string StaticRoot => Path.Combine(WwwRoot, "static");

    /// <summary>
    /// 模组图标目录。属于「数据」，固定落在 <see cref="BaseDir"/> 下 ——
    /// 单文件发布时不会随临时解压目录一起消失。
    /// </summary>
    public string IconsDir => Path.Combine(BaseDir, "wwwroot", "static", "mod_icons");

    /// <summary>默认 Mods 目录（默认 D:\Documents\...；可用环境变量覆盖）。</summary>
    public string DefaultModsPath { get; }

    /// <summary>全局整合包目录 = Mods/ModPacks。</summary>
    public string GlobalModpacksPath => Path.Combine(DefaultModsPath, "ModPacks");

    /// <summary>当前进程解析出的路径（App 启动时设置，供日志等使用）。</summary>
    public static AppPaths? Current { get; set; }

    public AppPaths(string baseDir, string? defaultModsPath = null, string? wwwRoot = null)
    {
        BaseDir = Path.GetFullPath(baseDir);
        WwwRoot = string.IsNullOrEmpty(wwwRoot)
            ? Path.Combine(BaseDir, "wwwroot")
            : Path.GetFullPath(wwwRoot);
        DefaultModsPath = string.IsNullOrEmpty(defaultModsPath) ? DetectDefaultModsPath() : defaultModsPath;
    }

    /// <summary>
    /// 解析运行路径。
    /// <list type="bullet">
    ///   <item>**目录版发布**（exe 旁边有 <c>wwwroot</c>）：运行根 = exe 目录，
    ///     <c>data.json</c> / <c>logs/</c> / <c>backups/</c> / <c>snapshots/</c> 都在 exe 旁边。</item>
    ///   <item>**单文件版发布**（<c>IncludeAllContentForSelfExtract</c>）：<paramref name="exeDir"/>
    ///     是运行时解压出来的临时目录，前端从那里读；**数据要落到 <paramref name="dataDir"/>
    ///     （用户双击的那个 exe 所在目录）**。</item>
    ///   <item>**开发构建**（<c>bin/Debug</c> 等没有 wwwroot）：向上找 <c>TModManager.sln</c>
    ///     定位仓库根，用仓库里的 wwwroot，改完前端不用重新构建。</item>
    /// </list>
    /// </summary>
    /// <param name="exeDir">前端文件所在目录（单文件版里是解压目录）。</param>
    /// <param name="dataDir">数据应当落在的目录（用户双击的 exe 所在目录）；常规版与 <paramref name="exeDir"/> 相同。</param>
    public static AppPaths Resolve(string exeDir, string? dataDir = null)
    {
        var full = Path.GetFullPath(exeDir);
        var webRoot = Path.Combine(full, "wwwroot");

        if (Directory.Exists(webRoot))
        {
            // 单文件发布：exeDir 是临时解压目录，数据必须落到真实 exe 旁
            if (!string.IsNullOrEmpty(dataDir) && !SameDir(full, dataDir!))
                return new AppPaths(Path.GetFullPath(dataDir!), null, webRoot);
            return new AppPaths(full, null, webRoot);
        }

        var dir = new DirectoryInfo(full);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TModManager.sln")))
                return new AppPaths(dir.FullName, null, Path.Combine(dir.FullName, "wwwroot"));
            dir = dir.Parent;
        }

        return new AppPaths(full, null, webRoot);
    }

    private static bool SameDir(string a, string b) => string.Equals(
        a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static string DetectDefaultModsPath()
    {
        var env = Environment.GetEnvironmentVariable("TMOD_DEFAULT_MODS_PATH");
        if (!string.IsNullOrEmpty(env)) return env;
        return @"D:\Documents\My Games\Terraria\tModLoader\Mods";
    }
}
