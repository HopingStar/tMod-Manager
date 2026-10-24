// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using TModManager.Core.Storage;
using Xunit;

namespace TModManager.Tests;

/// <summary>C# 版完全独立：Provision 只确保目录存在，绝不从 Python 版复制数据/前端。</summary>
public class FirstRunProvisionerTests
{
    [Fact]
    public void Provision_CreatesDirs_AndKeepsDataIndependent()
    {
        using var tmp = new TestPaths();
        // 全新临时 BaseDir：无 data.json / wwwroot
        Assert.False(File.Exists(tmp.AppPaths.DataFile));
        Assert.False(Directory.Exists(tmp.AppPaths.WwwRoot));

        new FirstRunProvisioner(tmp.AppPaths).Provision();

        Assert.True(Directory.Exists(tmp.AppPaths.BaseDir));
        Assert.True(Directory.Exists(tmp.AppPaths.WwwRoot));
        Assert.True(Directory.Exists(tmp.AppPaths.StaticRoot));
        Assert.True(Directory.Exists(tmp.AppPaths.IconsDir));
        // 数据完全独立：不生成 data.json（由 JsonStore 首次写入时创建），不复制任何 Python 内容
        Assert.False(File.Exists(tmp.AppPaths.DataFile));
    }
}
