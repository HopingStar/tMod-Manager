// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

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

    [Fact]
    public async Task VersionMods_ConflictDetectedByInternalId_NotFilename()
    {
        using var tmp = new TestPaths();
        // 本地：文件名带版本后缀，但内部 ID 是 FooMod；另有一个只在本地存在的 SoloMod
        File.WriteAllBytes(Path.Combine(tmp.ModsDir, "FooMod_v1.2.3.tmod"), TmodHeader("FooMod"));
        File.WriteAllBytes(Path.Combine(tmp.ModsDir, "SoloMod.tmod"), TmodHeader("SoloMod"));
        // workshop：文件名 FooMod.tmod，内部 ID 也是 FooMod
        var wsDir = Path.Combine(tmp.BaseDir, "steamapps", "workshop", "content", "1281930", "999");
        Directory.CreateDirectory(wsDir);
        File.WriteAllBytes(Path.Combine(wsDir, "FooMod.tmod"), TmodHeader("FooMod"));

        var store = new JsonStore(tmp.AppPaths);
        await store.WriteAsync(new AppData
        {
            Versions = new List<VersionEntry>
            {
                new() { Id = "v1", Name = "v1", BatPath = tmp.CreateBat(), ModsPath = tmp.ModsDir },
            }
        });
        await File.WriteAllTextAsync(Path.Combine(tmp.ModsDir, "enabled.json"), "[\"FooMod\", \"SoloMod\"]");

        var result = (await CreateMods(tmp).VersionModsAsync("v1")).Result;
        Assert.NotNull(result);
        var all = result!.Enabled.Concat(result.Disabled).ToList();
        // 内部 ID 相同（FooMod）但文件名不同（FooMod.tmod / FooMod_v1.2.3.tmod）→ 两个条目都标记冲突
        Assert.Contains(all, m => m.Name == "FooMod" && m.Conflict);
        Assert.Contains(all, m => m.Name == "FooMod_v1.2.3" && m.Conflict);
        // 只在本地存在 → 不冲突
        Assert.Contains(all, m => m.Name == "SoloMod" && !m.Conflict);
    }

    /// <summary>构造一个只含文件头的最小 .tmod（供 ParseInternalName 读取内部 ID），不解压条目。</summary>
    private static byte[] TmodHeader(string internalName)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(0x444F4D54u);          // 'TMOD'
        Write7(bw, "2024.0.0.0");       // tML 版本
        bw.Write(new byte[20]);         // SHA1
        bw.Write(new byte[256]);        // 签名
        bw.Write(0u);                   // 数据长度
        Write7(bw, internalName);       // 内部 ID
        bw.Flush();
        return ms.ToArray();
    }

    private static void Write7(BinaryWriter bw, string s)
    {
        var b = System.Text.Encoding.UTF8.GetBytes(s);
        bw.Write7BitEncodedInt(b.Length);
        bw.Write(b);
    }
}
