// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using TModManager.Core.Mods;
using TModManager.Core.Models;
using TModManager.Core.Storage;

namespace TModManager.Tests;

public class SpecialLoadServiceTests
{
    [Fact]
    public void ApplyDisabledSet_Renames_AndRestoreAll_Undoes()
    {
        using var tmp = new TestPaths();
        File.WriteAllText(Path.Combine(tmp.ModsDir, "ModA.tmod"), "a");
        File.WriteAllText(Path.Combine(tmp.ModsDir, "ModB.tmod"), "b");

        var svc = new SpecialLoadService(tmp.AppPaths, new JsonStore(tmp.AppPaths));

        var errors = svc.ApplyDisabledSet(new HashSet<string> { "ModA" });
        Assert.Empty(errors);
        Assert.True(File.Exists(Path.Combine(tmp.ModsDir, "ModA.tmod.disabled")));
        Assert.False(File.Exists(Path.Combine(tmp.ModsDir, "ModA.tmod")));
        Assert.True(File.Exists(Path.Combine(tmp.ModsDir, "ModB.tmod")));

        var restored = svc.RestoreAllDisabled();
        Assert.Single(restored);
        Assert.True(File.Exists(Path.Combine(tmp.ModsDir, "ModA.tmod")));
        Assert.False(File.Exists(Path.Combine(tmp.ModsDir, "ModA.tmod.disabled")));
    }

    [Fact]
    public void ApplyDisabledSet_ReEnabled_WhenDroppedFromSet()
    {
        using var tmp = new TestPaths();
        File.WriteAllText(Path.Combine(tmp.ModsDir, "ModX.tmod"), "x");
        var svc = new SpecialLoadService(tmp.AppPaths, new JsonStore(tmp.AppPaths));

        svc.ApplyDisabledSet(new HashSet<string> { "ModX" });
        Assert.True(File.Exists(Path.Combine(tmp.ModsDir, "ModX.tmod.disabled")));

        svc.ApplyDisabledSet(new HashSet<string>());
        Assert.True(File.Exists(Path.Combine(tmp.ModsDir, "ModX.tmod")));
    }

    [Fact]
    public void PackSpecialSet_Independent_BeatsGlobal()
    {
        using var tmp = new TestPaths();
        var svc = new SpecialLoadService(tmp.AppPaths, new JsonStore(tmp.AppPaths));
        var data = new AppData();
        data.GlobalSpecial.Active = true;
        data.GlobalSpecial.Mods = new List<string> { "G1" };
        data.SpecialLoads["packA"] = new SpecialLoad { Independent = true, Mods = new List<string> { "P1" } };
        data.SpecialLoads["packB"] = new SpecialLoad { Independent = false };

        Assert.Equal(new[] { "P1" }, svc.PackSpecialSet(data, "packA"));
        Assert.Equal(new[] { "G1" }, svc.PackSpecialSet(data, "packB"));
    }
}
