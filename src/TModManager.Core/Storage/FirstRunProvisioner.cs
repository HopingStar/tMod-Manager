using TModManager.Core.Configuration;

namespace TModManager.Core.Storage;

/// <summary>
/// 首次启动预置（幂等）：确保运行所需目录存在（BaseDir / wwwroot / static / mod_icons）。
/// C# 版完全独立：数据文件（data.json / mod_meta.json）从空开始，前端 wwwroot 随仓库/发布自带，
/// 不再从 Python 版复制任何内容。
/// </summary>
public sealed class FirstRunProvisioner
{
    private readonly AppPaths _paths;

    public FirstRunProvisioner(AppPaths paths) => _paths = paths;

    public void Provision(bool force = false)
    {
        Directory.CreateDirectory(_paths.BaseDir);
        Directory.CreateDirectory(_paths.WwwRoot);
        Directory.CreateDirectory(_paths.StaticRoot);
        // 图标目录：C# 版自管（加密 .dat），确保目录存在
        Directory.CreateDirectory(_paths.IconsDir);
    }
}
