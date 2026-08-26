using TModManager.Core.Configuration;

namespace TModManager.Tests;

/// <summary>测试用的临时路径夹具：独立 BaseDir + 独立 Mods 目录（绝不影响真实数据/Mods）。</summary>
public sealed class TestPaths : IDisposable
{
    public string BaseDir { get; }
    public string ModsDir { get; }
    public AppPaths AppPaths { get; }

    public TestPaths()
    {
        BaseDir = Path.Combine(Path.GetTempPath(), "tmodcs_" + Guid.NewGuid().ToString("N"));
        ModsDir = Path.Combine(BaseDir, "FakeMods");
        Directory.CreateDirectory(ModsDir);
        AppPaths = new AppPaths(BaseDir, ModsDir);
    }

    public string CreateBat(string name = "start.bat")
    {
        var path = Path.Combine(BaseDir, name);
        File.WriteAllText(path, "@echo off\r\necho fake bat\r\n");
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(BaseDir, recursive: true); } catch { }
    }
}
