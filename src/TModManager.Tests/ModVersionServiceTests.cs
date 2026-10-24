using System.Text;
using TModManager.Core.ModVersions;

namespace TModManager.Tests;

/// <summary>造「最小可用 .tmod」：只要文件头合法，<see cref="TmodParser.ParseHeader"/> 就能读出内部名与版本。
/// 尾部填充长度用来让不同版本的字节内容互不相同（也避免依赖 mtime 精度）。</summary>
internal static class FakeTmod
{
    public static byte[] Build(string name, string version, string build = "2026.6.3.6", int filler = 64)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(new byte[] { 0x54, 0x4D, 0x4F, 0x44 });   // 'TMOD'
            Write7(w, build);                                  // tML 构建版本
            w.Write(new byte[20]);                             // SHA1
            w.Write(new byte[256]);                            // 签名
            w.Write(0u);                                       // 数据长度
            Write7(w, name);                                   // 内部 ID
            Write7(w, version);                                // 模组版本
            w.Write(0);                                        // 条目数
            w.Write(new byte[Math.Max(0, filler)]);
        }
        return ms.ToArray();
    }

    private static void Write7(BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        w.Write7BitEncodedInt(b.Length);
        w.Write(b);
    }

    public static string Write(string dir, string fileName, string name, string version,
        string build = "2026.6.3.6", int filler = 64)
    {
        var p = Path.Combine(dir, fileName);
        File.WriteAllBytes(p, Build(name, version, build, filler));
        return p;
    }
}

public class ModVersionServiceTests
{
    /// <summary>还没做过任何动作的模组：列表里能看到，但**一个字节都不复制、也不落盘**。</summary>
    [Fact]
    public void Adopt_IsViewOnly_NoCopyNoWrite()
    {
        using var tp = new TestPaths();
        var svc = new ModVersionService(tp.AppPaths);
        FakeTmod.Write(tp.ModsDir, "Foo.tmod", "Foo", "0.1", filler: 10);

        var row = Assert.Single(svc.List().Mods);
        Assert.Equal("Foo", row.InternalName);
        Assert.Equal("0.1", row.CurrentVersion);
        Assert.Equal("ok", row.State);
        Assert.Equal(1, row.VersionCount);
        Assert.False(row.HasRollback);          // ← 只有当前版本 ⇒ 不该画回滚按钮

        var d = svc.Detail("Foo");
        Assert.NotNull(d);
        var v = Assert.Single(d!.Versions);
        Assert.True(v.Adopted);
        Assert.True(v.IsCurrent);
        Assert.Equal("in_place", v.Stored);
        Assert.False(d.HasRollback);
        Assert.False(d.State == "duplicate");

        // 关键：快照目录里什么都没有
        Assert.False(Directory.Exists(svc.Root) &&
                     Directory.EnumerateFiles(svc.Root, "*", SearchOption.AllDirectories).Any());
    }

    /// <summary>内部名不符 / 内容已在列表里 → 一律拒绝；真正的新版本 → 归档。</summary>
    [Fact]
    public void AddVersion_ValidatesModAndDuplicate()
    {
        using var tp = new TestPaths();
        var svc = new ModVersionService(tp.AppPaths);
        FakeTmod.Write(tp.ModsDir, "Foo.tmod", "Foo", "0.1", filler: 10);

        var bar = FakeTmod.Write(tp.BaseDir, "Bar.tmod", "Bar", "9.9", filler: 10);
        Assert.Contains("Bar", Assert.Throws<ModVersionException>(() => svc.AddVersion("Foo", bar)).Message);

        // 与 Mods 里那份内容完全一致 → 重复，拒绝
        var dup = FakeTmod.Write(tp.BaseDir, "FooDup.tmod", "Foo", "0.1", filler: 10);
        Assert.Throws<ModVersionException>(() => svc.AddVersion("Foo", dup));

        // Mods 目录里已有的文件本身，不该再被「添加」
        Assert.Throws<ModVersionException>(() => svc.AddVersion("Foo", Path.Combine(tp.ModsDir, "Foo.tmod")));

        var v2 = FakeTmod.Write(tp.BaseDir, "Foo2.tmod", "Foo", "0.2", filler: 30);
        var d = svc.AddVersion("Foo", v2);
        Assert.Equal(2, d.Versions.Count);
        // 仓库里的文件名 = 拖进来那个文件的**原文件名**（不是版本号）
        Assert.True(File.Exists(Path.Combine(svc.Root, "Foo", "Foo2.tmod")));
        Assert.True(File.Exists(Path.Combine(svc.Root, "Foo.json")));
        // 收养的那条只是登记，内容仍在 Mods 目录里
        Assert.Equal("in_place", d.Versions.First(v => v.Version == "0.1").Stored);
        Assert.Equal("pool", d.Versions.First(v => v.Version == "0.2").Stored);
    }

    /// <summary>回滚：当前那份**先被实体化进快照**，然后才被替换 —— 列表条数一变不变。</summary>
    [Fact]
    public void Rollback_MaterializesCurrent_AndNeverChangesListSize()
    {
        using var tp = new TestPaths();
        var svc = new ModVersionService(tp.AppPaths);
        FakeTmod.Write(tp.ModsDir, "Foo.tmod", "Foo", "0.1", filler: 10);
        var v2 = FakeTmod.Write(tp.BaseDir, "Foo2.tmod", "Foo", "0.2", filler: 30);
        var before = svc.AddVersion("Foo", v2);
        Assert.Equal(2, before.Versions.Count);

        var target = before.Versions.First(v => v.Version == "0.2");
        var after = svc.Rollback("Foo", target.Sha256, force: false);

        Assert.Equal(2, after.Versions.Count);                       // ← 核心不变量
        Assert.All(after.Versions, v => Assert.Equal("pool", v.Stored));
        Assert.Equal("0.2", after.Versions.First(v => v.IsCurrent).Version);
        Assert.True(File.Exists(Path.Combine(svc.Root, "Foo", "Foo.tmod")));   // 0.1 被救下来了

        // 落盘的文件名 = **目标版本原来那个名字**（Foo2.tmod），旧名字那个被删掉
        var only = Assert.Single(Directory.GetFiles(tp.ModsDir, "*.tmod"));
        Assert.Equal("Foo2.tmod", Path.GetFileName(only));
        Assert.Equal(FakeTmod.Build("Foo", "0.2", filler: 30), File.ReadAllBytes(only));

        // 来回切：名字跟着版本走（0.1 变回 Foo.tmod），列表仍是 2 条
        var back = svc.Rollback("Foo", after.Versions.First(v => v.Version == "0.1").Sha256, force: false);
        Assert.Equal(2, back.Versions.Count);
        var only2 = Assert.Single(Directory.GetFiles(tp.ModsDir, "*.tmod"));
        Assert.Equal("Foo.tmod", Path.GetFileName(only2));
        Assert.Equal(FakeTmod.Build("Foo", "0.1", filler: 10), File.ReadAllBytes(only2));
    }

    /// <summary>当前那份没归档时，回滚默认拒绝（防误丢），force 才放行。</summary>
    [Fact]
    public void Rollback_RefusesUnarchivedCurrent_UnlessForced()
    {
        using var tp = new TestPaths();
        var svc = new ModVersionService(tp.AppPaths);
        FakeTmod.Write(tp.ModsDir, "Foo.tmod", "Foo", "0.1", filler: 10);
        var v2 = FakeTmod.Write(tp.BaseDir, "Foo2.tmod", "Foo", "0.2", filler: 30);
        svc.AddVersion("Foo", v2);

        // 用户绕过本软件，直接把 Mods 里的文件换成了 0.9
        FakeTmod.Write(tp.ModsDir, "Foo.tmod", "Foo", "0.9", filler: 90);

        var d = svc.Detail("Foo");
        Assert.NotNull(d);
        Assert.Equal("unarchived", d!.State);
        Assert.True(svc.List().Mods[0].HasRollback);

        var target = d.Versions.First(v => v.Version == "0.2");
        Assert.Throws<ModVersionException>(() => svc.Rollback("Foo", target.Sha256, force: false));

        var after = svc.Rollback("Foo", target.Sha256, force: true);
        Assert.Equal("0.2", after.Versions.First(v => v.IsCurrent).Version);
        // force = 用户确认放弃：那份未归档的内容**不会**被偷偷收进列表（列表条数仍是 2）
        Assert.Equal(2, after.Versions.Count);
        Assert.False(File.Exists(Path.Combine(svc.Root, "Foo", "0.9.tmod")));
    }

    /// <summary>同名冲突仲裁：留一个在 Mods 目录，其余先落快照再从 Mods 移走。</summary>
    [Fact]
    public void SetCurrent_ResolvesDuplicate_WithoutLosingAnything()
    {
        using var tp = new TestPaths();
        var svc = new ModVersionService(tp.AppPaths);
        FakeTmod.Write(tp.ModsDir, "Foo.tmod", "Foo", "0.1", filler: 10);
        FakeTmod.Write(tp.ModsDir, "Foo_old.tmod", "Foo", "0.2", filler: 30);

        Assert.Equal("duplicate", Assert.Single(svc.List().Mods).State);

        var d = svc.SetCurrent("Foo", "Foo.tmod");
        Assert.Equal("ok", d.State);
        Assert.Equal(2, d.Versions.Count);
        Assert.Equal("0.1", d.Versions.First(v => v.IsCurrent).Version);

        Assert.Single(Directory.GetFiles(tp.ModsDir, "*.tmod"));
        Assert.True(File.Exists(Path.Combine(tp.ModsDir, "Foo.tmod")));
        Assert.False(File.Exists(Path.Combine(tp.ModsDir, "Foo_old.tmod")));
        Assert.True(File.Exists(Path.Combine(svc.Root, "Foo", "Foo_old.tmod")));   // 被回收，没丢（沿用原文件名）
    }

    /// <summary>删除：当前版本（in_place）不能删，其他能删且会删掉仓库里的文件。</summary>
    [Fact]
    public void DeleteVersion_KeepsCurrent()
    {
        using var tp = new TestPaths();
        var svc = new ModVersionService(tp.AppPaths);
        FakeTmod.Write(tp.ModsDir, "Foo.tmod", "Foo", "0.1", filler: 10);
        var d0 = svc.AddVersion("Foo", FakeTmod.Write(tp.BaseDir, "Foo2.tmod", "Foo", "0.2", filler: 30));

        Assert.Throws<ModVersionException>(() => svc.DeleteVersion("Foo", d0.Versions.First(v => v.IsCurrent).Sha256));

        var d1 = svc.DeleteVersion("Foo", d0.Versions.First(v => !v.IsCurrent).Sha256);
        Assert.NotNull(d1);
        Assert.Single(d1!.Versions);
        Assert.False(File.Exists(Path.Combine(svc.Root, "Foo", "Foo2.tmod")));
    }

    /// <summary>回滚到哪一版，**落盘的文件名就是那一版原来那个名字** —— 文件名是版本的标识。</summary>
    [Fact]
    public void Rollback_RestoresTheTargetVersionsFileName()
    {
        using var tp = new TestPaths();
        var svc = new ModVersionService(tp.AppPaths);
        // 用户把版本写进了文件名（本机真实情况：2026.6CalamityMod.tmod）
        FakeTmod.Write(tp.ModsDir, "Foo_2026.6.tmod", "Foo", "0.1", filler: 10);
        var d0 = svc.AddVersion("Foo", FakeTmod.Write(tp.BaseDir, "Foo2.tmod", "Foo", "0.2", filler: 30));

        var after = svc.Rollback("Foo", d0.Versions.First(v => v.Version == "0.2").Sha256, force: false);

        var only = Assert.Single(Directory.GetFiles(tp.ModsDir, "*.tmod"));
        Assert.Equal("Foo2.tmod", Path.GetFileName(only));                       // 换上目标版本的名字
        Assert.Equal(FakeTmod.Build("Foo", "0.2", filler: 30), File.ReadAllBytes(only));
        Assert.False(File.Exists(Path.Combine(tp.ModsDir, "Foo_2026.6.tmod")));  // 旧名字那个收走了
        Assert.Equal("0.2", after.Versions.First(v => v.IsCurrent).Version);
        Assert.Equal(2, after.Versions.Count);

        // 再滚回去 → 名字跟着回到 0.1 原来的名字
        svc.Rollback("Foo", after.Versions.First(v => v.Version == "0.1").Sha256, force: false);
        var only2 = Assert.Single(Directory.GetFiles(tp.ModsDir, "*.tmod"));
        Assert.Equal("Foo_2026.6.tmod", Path.GetFileName(only2));
        Assert.Equal(FakeTmod.Build("Foo", "0.1", filler: 10), File.ReadAllBytes(only2));
    }

    /// <summary>启用/禁用是另一条轴：回滚换了版本名，但**状态跟着回滚前走**，不跟版本走。</summary>
    [Fact]
    public void Rollback_KeepsDisabledState_ButTakesTheVersionsName()
    {
        using var tp = new TestPaths();
        var svc = new ModVersionService(tp.AppPaths);
        FakeTmod.Write(tp.ModsDir, "Foo.tmod.disabled", "Foo", "0.1", filler: 10);
        var d0 = svc.AddVersion("Foo", FakeTmod.Write(tp.BaseDir, "Foo2.tmod", "Foo", "0.2", filler: 30));

        svc.Rollback("Foo", d0.Versions.First(v => v.Version == "0.2").Sha256, force: false);

        Assert.True(File.Exists(Path.Combine(tp.ModsDir, "Foo2.tmod.disabled")));   // 版本名 + 仍是禁用
        Assert.False(File.Exists(Path.Combine(tp.ModsDir, "Foo2.tmod")));           // 没被顺手启用
        Assert.False(File.Exists(Path.Combine(tp.ModsDir, "Foo.tmod.disabled")));   // 旧文件收走
        var d = svc.Detail("Foo")!;
        Assert.True(d.LocalFiles[0].Disabled);
        Assert.Equal("0.2", d.Versions.First(v => v.IsCurrent).Version);
    }

    /// <summary>Mods 目录里本来没有这个模组（用户整个删了）→ 从快照装回来，仍然用那一版原来的文件名。</summary>
    [Fact]
    public void Rollback_InstallsUsingTheVersionsOriginalName()
    {
        using var tp = new TestPaths();
        var svc = new ModVersionService(tp.AppPaths);
        FakeTmod.Write(tp.ModsDir, "Foo.tmod", "Foo", "0.1", filler: 10);
        var d0 = svc.AddVersion("Foo", FakeTmod.Write(tp.BaseDir, "Foo2.tmod", "Foo", "0.2", filler: 30));

        File.Delete(Path.Combine(tp.ModsDir, "Foo.tmod"));
        Assert.Equal("missing", Assert.Single(svc.List().Mods).State);

        // 0.1 只是「收养」进来的（内容就是刚被删掉的那个文件）→ 已经找不回来了，要说清楚
        Assert.Contains("没有另存过", Assert.Throws<ModVersionException>(
            () => svc.Rollback("Foo", d0.Versions.First(v => v.Version == "0.1").Sha256, force: false)).Message);

        // 0.2 在快照里 → 装回来，名字是它原来那个
        var after = svc.Rollback("Foo", d0.Versions.First(v => v.Version == "0.2").Sha256, force: false);
        Assert.Equal("ok", after.State);
        Assert.Equal(FakeTmod.Build("Foo", "0.2", filler: 30),
            File.ReadAllBytes(Path.Combine(tp.ModsDir, "Foo2.tmod")));
    }

    /// <summary>快照目录里混进来的杂物 .json 不该变成一个幽灵模组。</summary>
    [Fact]
    public void StrayJsonInSnapshotRoot_IsIgnored()
    {
        using var tp = new TestPaths();
        var svc = new ModVersionService(tp.AppPaths);
        FakeTmod.Write(tp.ModsDir, "Foo.tmod", "Foo", "0.1", filler: 10);
        svc.AddVersion("Foo", FakeTmod.Write(tp.BaseDir, "Foo2.tmod", "Foo", "0.2", filler: 30));

        Directory.CreateDirectory(svc.Root);
        File.WriteAllText(Path.Combine(svc.Root, "Foo.json.bak-rename"), """{"internal_name":"Foo","versions":[]}""");
        File.WriteAllText(Path.Combine(svc.Root, "Stray.json"), """{"internal_name":"SomeoneElse","versions":[]}""");
        File.WriteAllText(Path.Combine(svc.Root, "Junk.json"), "{ not json");

        Assert.Equal("Foo", Assert.Single(svc.List().Mods).InternalName);
    }

    /// <summary>内部 ID 会被当成文件名/目录名 —— 必须挡住目录穿越与非法字符。</summary>
    [Fact]
    public void UnsafeNames_AreRejected()
    {
        using var tp = new TestPaths();
        var svc = new ModVersionService(tp.AppPaths);
        Assert.Null(svc.Detail("../evil"));
        Assert.Null(svc.Detail(".."));
        Assert.Null(svc.Detail("a/b"));
        Assert.Null(svc.Detail("a\\b"));
        Assert.Null(svc.Detail(""));
        Assert.Throws<ModVersionException>(() => svc.AddVersion("../evil", "whatever"));
    }

    /// <summary>仓库里的文件名沿用**原文件名**；原文件名没法用时才退回消毒过的版本号。</summary>
    [Fact]
    public void PoolFile_KeepsOriginalName_AndFallsBackToVersion()
    {
        using var tp = new TestPaths();
        var svc = new ModVersionService(tp.AppPaths);
        FakeTmod.Write(tp.ModsDir, "Foo.tmod", "Foo", "0.1", filler: 10);

        // ① 原文件名可用 → 原样沿用；界面上的版本串也保持原样（哪怕里面有非法字符）
        var weird = FakeTmod.Write(tp.BaseDir, "FooWeird.tmod", "Foo", "1.0:beta|2", filler: 30);
        var d = svc.AddVersion("Foo", weird);
        Assert.Equal("1.0:beta|2", d.Versions.First(x => x.Version == "1.0:beta|2").Version);
        Assert.True(File.Exists(Path.Combine(svc.Root, "Foo", "FooWeird.tmod")));

        // ② 原文件名本身非法（上传那条路可能带进来）→ 退回 `<消毒过的版本>.tmod`
        var bad = FakeTmod.Write(tp.BaseDir, "tmp1.tmod", "Foo", "1.0:beta|2", filler: 40);
        svc.AddVersion("Foo", bad, "bad:name.tmod");
        Assert.True(File.Exists(Path.Combine(svc.Root, "Foo", "1.0_beta_2.tmod")));
        foreach (var f in Directory.GetFiles(Path.Combine(svc.Root, "Foo"), "*.tmod"))
            Assert.DoesNotContain(":", Path.GetFileName(f));
    }
}
