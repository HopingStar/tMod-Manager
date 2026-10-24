// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using TModManager.Core.Snapshots;

namespace TModManager.Tests;

/// <summary>enabled.json 快照（取代原先只有一代的 .bk）。</summary>
public class SnapshotServiceTests
{
    private static string WriteEnabled(string modsDir, string json)
    {
        Directory.CreateDirectory(modsDir);
        var path = Path.Combine(modsDir, "enabled.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Capture_StoresPayloadUnderAppDir_AndListRoundTrips()
    {
        using var tmp = new TestPaths();
        var modsDir = Path.Combine(tmp.BaseDir, "Mods");
        WriteEnabled(modsDir, "[\n  \"A\",\n  \"B\"\n]");

        var svc = new SnapshotService(tmp.AppPaths);
        var entry = svc.Capture(modsDir, "切换前（测试）", "V1", "PackX");

        Assert.NotNull(entry);
        // 快照必须落在**程序目录**下，而不是游戏 Mods 目录里
        Assert.StartsWith(tmp.AppPaths.BaseDir, entry!.Path);
        Assert.StartsWith(Path.Combine(tmp.AppPaths.BaseDir, "snapshots"), entry.Path);
        Assert.False(File.Exists(Path.Combine(modsDir, "enabled.json.bk")));   // 不再用 .bk

        var list = svc.List();
        Assert.Single(list);
        Assert.Equal(2, list[0].Count);
        Assert.Equal("切换前（测试）", list[0].Label);
        Assert.Equal("PackX", list[0].PackId);
        Assert.Equal(Path.GetFullPath(modsDir), list[0].ModsDir);
    }

    [Fact]
    public void Restore_WritesTheSnapshotBack()
    {
        using var tmp = new TestPaths();
        var modsDir = Path.Combine(tmp.BaseDir, "Mods");
        var enabled = WriteEnabled(modsDir, "[\"OldSetup\"]");

        var svc = new SnapshotService(tmp.AppPaths);
        var entry = svc.Capture(modsDir, "切换前", "V1");
        Assert.NotNull(entry);

        File.WriteAllText(enabled, "[\"NewSetup\"]");          // 模拟被整合包覆盖
        Assert.Equal(enabled, svc.Restore(entry!.Id, modsDir)); // 还原
        Assert.Contains("OldSetup", File.ReadAllText(enabled));
    }

    [Fact]
    public void Restore_RejectsTraversalAndUnknownId()
    {
        using var tmp = new TestPaths();
        var modsDir = Path.Combine(tmp.BaseDir, "Mods");
        WriteEnabled(modsDir, "[\"A\"]");

        var svc = new SnapshotService(tmp.AppPaths);
        Assert.Null(svc.Restore("../../etc", modsDir));
        Assert.Null(svc.Restore("no-such-snapshot", modsDir));
        Assert.Null(svc.ResolveDir("..\\..\\windows"));
        Assert.Null(svc.ResolveDir("a/b"));
    }

    [Fact]
    public void Prune_KeepsOnlyNewest()
    {
        using var tmp = new TestPaths();
        var modsDir = Path.Combine(tmp.BaseDir, "Mods");
        WriteEnabled(modsDir, "[\"A\"]");

        var svc = new SnapshotService(tmp.AppPaths);
        for (var i = 0; i < SnapshotService.KeepCount + 4; i++)
            Assert.NotNull(svc.Capture(modsDir, "第 " + i + " 次"));

        Assert.Equal(SnapshotService.KeepCount, svc.List().Count);
    }

    [Fact]
    public void TryParseEnabledJson_FlagsEmptyAndBroken()
    {
        // 正常
        Assert.True(SnapshotService.TryParseEnabledJson("[\"A\",\"B\"]", out var names, out _));
        Assert.Equal(new[] { "A", "B" }, names);

        // 空数组：语法合法但"启用 0 个模组" = 全禁用，调用方必须靠 Count==0 拦下
        Assert.True(SnapshotService.TryParseEnabledJson("[]", out var empty, out _));
        Assert.Empty(empty);

        // 坏 JSON / 顶层不是数组 / 混入非字符串
        Assert.False(SnapshotService.TryParseEnabledJson("{ not json", out _, out var e1));
        Assert.NotNull(e1);
        Assert.False(SnapshotService.TryParseEnabledJson("{\"a\":1}", out _, out _));
        Assert.False(SnapshotService.TryParseEnabledJson("[\"A\", 3]", out _, out _));
        Assert.False(SnapshotService.TryParseEnabledJson("[\"A\", \"  \"]", out _, out _));
    }
}
