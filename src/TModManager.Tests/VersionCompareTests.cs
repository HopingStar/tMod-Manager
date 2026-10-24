// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using TModManager.Core.Network;

namespace TModManager.Tests;

public class VersionCompareTests
{
    [Theory]
    [InlineData("1.0", "1.0", 0)]
    [InlineData("1.0.1", "1.0", 1)]
    [InlineData("1.0", "1.0.1", -1)]
    [InlineData("v2026.06.3.6", "2026.06.3.6", 0)]
    [InlineData("2026.06.3.6-preview", "2026.06.3.6", 0)]
    [InlineData("2026.7", "2026.06", 1)]
    [InlineData("", "1.0", -1)]
    public void Compare_VersionStrings(string a, string b, int expected)
        => Assert.Equal(expected, VersionCompare.Compare(a, b));

    [Theory]
    [InlineData("v2026.06.3.6-preview", "2026.06.3.6")]
    [InlineData("abc", "")]
    [InlineData("1.2", "1.2")]
    [InlineData("", "")]
    public void Extract_ReturnsNumericCore(string tag, string expected)
        => Assert.Equal(expected, VersionCompare.Extract(tag));
}
