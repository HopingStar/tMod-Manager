using TModManager.Core.Models;
using TModManager.Core.Mods;
using TModManager.Core.Network;
using TModManager.Core.Storage;

namespace TModManager.Tests;

/// <summary>ModsService 定位逻辑：FindModFileAsync 本地 Mods / 整合包优先、Workshop 兜底。</summary>
public class ModsServiceTests
{
    private static ModsService CreateMods(TestPaths tmp)
    {
        var store = new JsonStore(tmp.AppPaths);
        var meta = new MetaStore(tmp.AppPaths);
        return new ModsService(tmp.AppPaths, store, meta, new WorkshopScanner(), new SteamService());
    }

    [Fact]
    public async Task FindModFile_ReturnsLocalTmod()
    {
        using var tmp = new TestPaths();
        File.WriteAllBytes(Path.Combine(tmp.ModsDir, "TestMod.tmod"), new byte[] { 1, 2, 3 });

        var found = await CreateMods(tmp).FindModFileAsync("TestMod");

        Assert.NotNull(found);
        Assert.EndsWith("TestMod.tmod", found);
    }

    [Fact]
    public async Task FindModFile_DisabledTmod_AlsoFound()
    {
        using var tmp = new TestPaths();
        File.WriteAllBytes(Path.Combine(tmp.ModsDir, "OffMod.tmod.disabled"), new byte[] { 1 });

        var found = await CreateMods(tmp).FindModFileAsync("OffMod");

        Assert.NotNull(found);
        Assert.EndsWith("OffMod.tmod.disabled", found);
    }

    [Fact]
    public async Task FindModFile_Unknown_ReturnsNull()
    {
        using var tmp = new TestPaths();

        var found = await CreateMods(tmp).FindModFileAsync("NoSuchMod");

        Assert.Null(found);
    }

    [Fact]
    public async Task FindModFile_PackContext_Wins()
    {
        using var tmp = new TestPaths();
        // 两个整合包都有同名模组：packA 与 packB/Mods/SharedMod.tmod
        var packA = Path.Combine(tmp.ModsDir, "ModPacks", "packA", "Mods");
        var packB = Path.Combine(tmp.ModsDir, "ModPacks", "packB", "Mods");
        Directory.CreateDirectory(packA);
        Directory.CreateDirectory(packB);
        File.WriteAllBytes(Path.Combine(packA, "SharedMod.tmod"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(packB, "SharedMod.tmod"), new byte[] { 2 });

        // 不传上下文：TmodFinder 遍历目录顺序不定，但只要指定 packId 就应定位到该整合包
        var found = await CreateMods(tmp).FindModFileAsync("SharedMod", packId: "packA");

        Assert.NotNull(found);
        Assert.Equal(Path.Combine(packA, "SharedMod.tmod"), found);
    }

    [Fact]
    public async Task FindModFile_VersionModsPath_Fallback()
    {
        using var tmp = new TestPaths();
        // 默认 Mods 没有，版本自定义 ModsPath 里有：version 上下文本地兜底应返回版本目录
        var verMods = Path.Combine(tmp.BaseDir, "VerMods");
        Directory.CreateDirectory(verMods);
        File.WriteAllBytes(Path.Combine(verMods, "OnlyVerMod.tmod"), new byte[] { 2 });

        var store = new JsonStore(tmp.AppPaths);
        await store.WriteAsync(new AppData
        {
            Versions = new List<VersionEntry>
            {
                new() { Id = "v1", Name = "v1", BatPath = tmp.CreateBat(), ModsPath = verMods },
            }
        });

        var found = await CreateMods(tmp).FindModFileAsync("OnlyVerMod", versionId: "v1");

        Assert.NotNull(found);
        Assert.Equal(Path.Combine(verMods, "OnlyVerMod.tmod"), found);
    }

    [Fact]
    public async Task FindModFile_Version_PrefersWorkshopOverLocal()
    {
        using var tmp = new TestPaths();
        // 同名模组在默认 Mods 和版本 Workshop 都有：version 模式应优先返回 Workshop
        File.WriteAllBytes(Path.Combine(tmp.ModsDir, "DupMod.tmod"), new byte[] { 1 });
        var wsMod = Path.Combine(tmp.BaseDir, "steamapps", "workshop", "content", "1281930", "12345", "DupMod.tmod");
        Directory.CreateDirectory(Path.GetDirectoryName(wsMod)!);
        File.WriteAllBytes(wsMod, new byte[] { 2 });

        var store = new JsonStore(tmp.AppPaths);
        await store.WriteAsync(new AppData
        {
            Versions = new List<VersionEntry>
            {
                new() { Id = "v1", Name = "v1", BatPath = tmp.CreateBat() },
            }
        });

        var found = await CreateMods(tmp).FindModFileAsync("DupMod", versionId: "v1");

        Assert.NotNull(found);
        Assert.Equal(wsMod, found);
    }

    [Fact]
    public async Task FindModFile_Version_IgnoresUnrelatedPacks()
    {
        using var tmp = new TestPaths();
        // 某整合包里有同名模组，但 version 列表来源是 Workshop ∪ 本地：不应开整合包里的文件
        var packMods = Path.Combine(tmp.ModsDir, "ModPacks", "packX", "Mods");
        Directory.CreateDirectory(packMods);
        File.WriteAllBytes(Path.Combine(packMods, "OnlyPackMod.tmod"), new byte[] { 1 });

        var store = new JsonStore(tmp.AppPaths);
        await store.WriteAsync(new AppData
        {
            Versions = new List<VersionEntry>
            {
                new() { Id = "v1", Name = "v1", BatPath = tmp.CreateBat() },
            }
        });

        var found = await CreateMods(tmp).FindModFileAsync("OnlyPackMod", versionId: "v1");

        Assert.Null(found);
    }
}
