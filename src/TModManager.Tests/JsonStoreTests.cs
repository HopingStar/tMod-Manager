// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using TModManager.Core.Models;
using TModManager.Core.Storage;

namespace TModManager.Tests;

public class JsonStoreTests
{
    [Fact]
    public async Task WriteRead_RoundTrip_PreservesFields()
    {
        using var tmp = new TestPaths();
        var store = new JsonStore(tmp.AppPaths);
        var data = new AppData();
        data.Versions.Add(new VersionEntry { Name = "v1", BatPath = "C:\\x.bat", VersionInfo = "1.0" });
        data.Favorites.Versions.Add("fav");

        await store.WriteAsync(data);
        var read = await store.ReadAsync();

        Assert.Single(read.Versions);
        Assert.Equal("v1", read.Versions[0].Name);
        Assert.Equal("C:\\x.bat", read.Versions[0].BatPath);
        Assert.Equal("fav", Assert.Single(read.Favorites.Versions));
    }

    [Fact]
    public async Task WriteRead_RoundTrip_WindowSize()
    {
        using var tmp = new TestPaths();
        var store = new JsonStore(tmp.AppPaths);
        var data = new AppData { WindowSize = new WindowSizeInfo { Width = 1400, Height = 900 } };
        await store.WriteAsync(data);
        var read = await store.ReadAsync();
        Assert.Equal(1400, read.WindowSize!.Width);
        Assert.Equal(900, read.WindowSize.Height);
    }

    [Fact]
    public async Task UpdateAsync_AppliesMutations_AndPersists()
    {
        using var tmp = new TestPaths();
        var store = new JsonStore(tmp.AppPaths);
        await store.UpdateAsync(d => { d.Versions.Add(new VersionEntry { Name = "a" }); return true; });
        await store.UpdateAsync(d => { d.Versions.Add(new VersionEntry { Name = "b" }); return true; });
        var read = await store.ReadAsync();
        Assert.Equal(2, read.Versions.Count);
    }

    [Fact]
    public async Task ReadAsync_CorruptedFile_ReturnsEmptyData()
    {
        using var tmp = new TestPaths();
        await File.WriteAllTextAsync(tmp.AppPaths.DataFile, "{ not valid json !!");
        var store = new JsonStore(tmp.AppPaths);
        var data = await store.ReadAsync();
        Assert.NotNull(data);
        Assert.Empty(data.Versions);
    }

    [Fact]
    public async Task ReadAsync_MissingFile_ReturnsEmptyData()
    {
        using var tmp = new TestPaths();
        var store = new JsonStore(tmp.AppPaths);
        var data = await store.ReadAsync();
        Assert.Empty(data.Versions);
    }

    [Fact]
    public async Task Write_IsAtomic_LeavesNoTempFile_AndKeepsPreviousAsBak()
    {
        using var tmp = new TestPaths();
        var store = new JsonStore(tmp.AppPaths);

        await store.UpdateAsync(d => { d.Versions.Add(new VersionEntry { Name = "第一版" }); return true; });
        await store.UpdateAsync(d => { d.Versions.Add(new VersionEntry { Name = "第二版" }); return true; });

        // 主文件是最后一次的内容；上一份被留成 .bak；临时文件不残留
        Assert.False(File.Exists(tmp.AppPaths.DataFile + ".tmp"));
        var bak = tmp.AppPaths.DataFile + ".bak";
        Assert.True(File.Exists(bak));
        Assert.Contains("第一版", await File.ReadAllTextAsync(bak));
        Assert.Contains("第二版", await File.ReadAllTextAsync(tmp.AppPaths.DataFile));
    }

    [Fact]
    public async Task CorruptedFile_IsPreserved_AndRecoveredFromBackup()
    {
        using var tmp = new TestPaths();
        var store = new JsonStore(tmp.AppPaths);
        await store.UpdateAsync(d => { d.Versions.Add(new VersionEntry { Name = "好数据" }); return true; });
        await store.UpdateAsync(d => { d.Versions.Add(new VersionEntry { Name = "新数据" }); return true; });

        await File.WriteAllTextAsync(tmp.AppPaths.DataFile, "{ 半截文件");   // 模拟写到一半被杀

        var reader = new JsonStore(tmp.AppPaths);                            // 模拟下次启动
        var recovered = await reader.ReadAsync();

        Assert.Equal("好数据", Assert.Single(recovered.Versions).Name);      // 从 .bak 恢复
        Assert.NotNull(reader.LastLoadWarning);
        Assert.Contains(Directory.GetFiles(tmp.AppPaths.BaseDir, "data.json.corrupt-*"), _ => true);  // 坏文件留证
    }

    [Fact]
    public async Task CorruptedFile_WithoutBackup_WarnsInsteadOfSilentlyResetting()
    {
        using var tmp = new TestPaths();
        await File.WriteAllTextAsync(tmp.AppPaths.DataFile, "not json at all");

        var store = new JsonStore(tmp.AppPaths);
        var data = await store.ReadAsync();

        Assert.Empty(data.Versions);
        Assert.NotNull(store.LastLoadWarning);                                // 关键：不再静默
        Assert.NotEmpty(Directory.GetFiles(tmp.AppPaths.BaseDir, "data.json.corrupt-*"));
    }

    [Fact]
    public async Task Write_SnakeCaseAndEscaping_MatchPythonStyle()
    {
        using var tmp = new TestPaths();
        var store = new JsonStore(tmp.AppPaths);
        await store.UpdateAsync(d =>
        {
            d.LastLaunch = new LastLaunch { VersionId = "abc", Disabled = new List<string> { "A", "B" } };
            return true;
        });
        var json = await File.ReadAllTextAsync(tmp.AppPaths.DataFile);
        // snake_case 字段名 + 中文字符不转义（UnsafeRelaxedJsonEscaping）
        Assert.Contains("\"last_launch\"", json);
        Assert.Contains("\"version_id\"", json);
    }
}
