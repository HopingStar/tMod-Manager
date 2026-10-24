// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using TModManager.Core.Icons;

namespace TModManager.Tests;

public class IconObfuscatorTests
{
    [Fact]
    public void Xor_RoundTrips()
    {
        var data = new byte[] { 1, 2, 3, 4, 255, 0, 100 };
        Assert.Equal(data, IconObfuscator.Decrypt(IconObfuscator.Encrypt(data)));
        Assert.NotEqual(data, IconObfuscator.Encrypt(data));
    }

    [Fact]
    public void FileName_IsStableAndHashed()
    {
        var a = IconObfuscator.FileName("CalamityMod");
        var b = IconObfuscator.FileName("CalamityMod");
        Assert.Equal(a, b);
        Assert.Equal(16, a.Length);
        Assert.NotEqual("CalamityMod", a);
        Assert.DoesNotContain(a, "CalamityMod");
    }

    [Theory]
    [InlineData("0123456789abcdef", true)]
    [InlineData("ABC", false)]
    [InlineData("0123456789abcdeG", false)]
    [InlineData("", false)]
    [InlineData("../etc/passwd", false)]
    public void IsValidHash_RejectsBadInput(string hash, bool expected)
        => Assert.Equal(expected, IconObfuscator.IsValidHash(hash));
}

public class IconCodecTests
{
    [Fact]
    public void Xor_RoundTrips()
    {
        var data = new byte[] { 1, 2, 3, 4, 255, 0 };
        var enc = IconCodec.Xor(data);
        Assert.Equal(data, IconCodec.Xor(enc));
        Assert.NotEqual(data, enc);
    }

    [Fact]
    public void DecryptIcon_ValidIco_ReturnsIcoAndPicksBest()
    {
        using var tmp = new TestPaths();
        // 最小 ICO：6 字节头 + 1 个 16 字节目录项 + 8 字节图像数据
        var ico = new byte[6 + 16 + 8];
        ico[0] = 0; ico[1] = 0; ico[2] = 1; ico[3] = 0; // type=icon
        ico[4] = 1; ico[5] = 0;                          // count=1
        ico[6] = 32; ico[7] = 32;                        // 32x32
        BitConverter.GetBytes((uint)8).CopyTo(ico, 6 + 8);   // sizeInBytes
        BitConverter.GetBytes((uint)22).CopyTo(ico, 6 + 12); // imageOffset
        for (int i = 0; i < 8; i++) ico[22 + i] = (byte)i;

        var dat = Path.Combine(tmp.BaseDir, "icon.dat");
        File.WriteAllBytes(dat, IconCodec.Xor(ico));

        var dec = IconCodec.DecryptIcon(dat);
        Assert.NotNull(dec);
        Assert.Equal(ico, dec);

        var picked = IconCodec.PickBestImage(dec);
        Assert.NotNull(picked);
        Assert.Equal(8, picked.Length);
        Assert.Equal(ico[22..30], picked);
    }

    [Fact]
    public void DecryptIcon_InvalidDat_ReturnsNull()
    {
        using var tmp = new TestPaths();
        var dat = Path.Combine(tmp.BaseDir, "icon.dat");
        File.WriteAllBytes(dat, IconCodec.Xor(new byte[] { 0xFF, 0xFF, 0xFF }));
        Assert.Null(IconCodec.DecryptIcon(dat));
    }

    [Fact]
    public void DecryptIcon_MissingFile_ReturnsNull()
    {
        using var tmp = new TestPaths();
        Assert.Null(IconCodec.DecryptIcon(Path.Combine(tmp.BaseDir, "none.dat")));
    }
}
