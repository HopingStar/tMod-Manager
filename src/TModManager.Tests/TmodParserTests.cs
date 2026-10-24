// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using System.IO;
using System.IO.Compression;
using System.Text;
using TModManager.Core.Parsing;

namespace TModManager.Tests;

public class TmodParserTests
{
    [Fact]
    public void Parse_UncompressedEntries_ReturnsMeta()
    {
        var icon = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var bytes = BuildTmod("TestMod", "1.2.3", "Test Mod Display", "Alice", icon, compressInfo: false, description: "A test mod");
        using var tmp = new TestPaths();
        var file = Path.Combine(tmp.BaseDir, "TestMod.tmod");
        File.WriteAllBytes(file, bytes);

        var meta = TmodParser.Parse(file);

        Assert.NotNull(meta);
        Assert.Equal("TestMod", meta.Name);
        Assert.Equal("Test Mod Display", meta.DisplayName);
        Assert.Equal("Alice", meta.Author);
        Assert.Equal("1.2.3", meta.Version);
        Assert.Equal("A test mod", meta.Description);
        Assert.Equal(icon, meta.IconBytes);
    }

    [Fact]
    public void Parse_DeflatedInfoEntry_ReturnsMeta()
    {
        // 用足够长的可压缩描述，确保 deflate 后 clen < ulen（否则解析器按未压缩处理）
        var desc = "This is a longer mod description with enough repeated text to actually compress when deflated. ".PadRight(200, 'x');
        var bytes = BuildTmod("DefMod", "9.9", "Def Mod", "Bob", new byte[] { 1, 2, 3 }, compressInfo: true, description: desc);
        using var tmp = new TestPaths();
        var file = Path.Combine(tmp.BaseDir, "DefMod.tmod");
        File.WriteAllBytes(file, bytes);

        var meta = TmodParser.Parse(file);

        Assert.NotNull(meta);
        Assert.Equal("Def Mod", meta.DisplayName);
        Assert.Equal("Bob", meta.Author);
        Assert.Equal("9.9", meta.Version);
        Assert.Equal(desc, meta.Description);
    }

    [Fact]
    public void Parse_IconOnlyNoInfo_FallsBackToHeaderVersion()
    {
        // 只有 icon.png，没有 Info → DisplayName 空，Version 取文件头版本
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(0x444F4D54u);
            WriteStr(bw, "2024.0.0.0");
            bw.Write(new byte[20]);
            bw.Write(new byte[256]);
            bw.Write(0u);
            WriteStr(bw, "IconOnly");
            WriteStr(bw, "7.7.7");
            bw.Write(1);
            WriteStr(bw, "icon.png");
            bw.Write(8); bw.Write(8);
            bw.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        }

        using var tmp = new TestPaths();
        var file = Path.Combine(tmp.BaseDir, "IconOnly.tmod");
        File.WriteAllBytes(file, ms.ToArray());

        var meta = TmodParser.Parse(file);

        Assert.NotNull(meta);
        Assert.Equal("IconOnly", meta.Name);
        Assert.Equal("7.7.7", meta.Version);
        Assert.Equal("", meta.DisplayName);
        Assert.Equal(8, meta.IconBytes!.Length);
    }

    [Fact]
    public void Parse_InvalidMagic_ReturnsNull()
    {
        using var tmp = new TestPaths();
        var file = Path.Combine(tmp.BaseDir, "bad.tmod");
        File.WriteAllBytes(file, new byte[] { 0x00, 0x01, 0x02, 0x03, 0xFF });
        Assert.Null(TmodParser.Parse(file));
    }

    // ---- 合成 .tmod 构建器（与 TmodParser 格式一一对应） ----

    private static byte[] BuildTmod(string name, string version, string displayName, string author, byte[] icon, bool compressInfo, string description = "")
    {
        var infoRaw = BuildInfo("displayName", displayName, "author", author, "version", version, "description", description);
        var info = compressInfo ? Deflate(infoRaw) : infoRaw;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(0x444F4D54u);            // 'TMOD'（字节 54 4D 4F 44）
        WriteStr(bw, "2024.0.0.0");       // tML 版本
        bw.Write(new byte[20]);           // SHA1
        bw.Write(new byte[256]);          // 签名
        bw.Write(0u);                     // 数据长度
        WriteStr(bw, name);
        WriteStr(bw, version);

        // Raw=原始字节，Stored=实际存储（压缩或未压缩）。ulen 必须写原始长度，clen 写存储长度，
        // 否则解析器会因 ulen==clen 跳过 deflate 解压。
        var entries = new (string Name, byte[] Raw, byte[] Stored)[]
        {
            ("icon.png", icon, icon),
            ("Info", infoRaw, info),
        };
        bw.Write(entries.Length);
        foreach (var (n, raw, stored) in entries)
        {
            WriteStr(bw, n);
            bw.Write(raw.Length);    // ulen
            bw.Write(stored.Length); // clen
        }
        foreach (var (_, _, stored) in entries) bw.Write(stored);
        bw.Flush();
        return ms.ToArray();
    }

    private static void WriteStr(BinaryWriter bw, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        bw.Write7BitEncodedInt(b.Length);
        bw.Write(b);
    }

    private static byte[] BuildInfo(params string[] kv)
    {
        using var ms = new MemoryStream();
        for (int i = 0; i < kv.Length; i += 2)
        {
            Write7(ms, Encoding.UTF8.GetByteCount(kv[i]));
            ms.Write(Encoding.UTF8.GetBytes(kv[i]));
            Write7(ms, Encoding.UTF8.GetByteCount(kv[i + 1]));
            ms.Write(Encoding.UTF8.GetBytes(kv[i + 1]));
        }
        return ms.ToArray();
    }

    private static void Write7(MemoryStream ms, int value)
    {
        while (value >= 0x80) { ms.WriteByte((byte)((value & 0x7f) | 0x80)); value >>= 7; }
        ms.WriteByte((byte)value);
    }

    private static byte[] Deflate(byte[] data)
    {
        using var outMs = new MemoryStream();
        using (var ds = new DeflateStream(outMs, CompressionLevel.Optimal)) ds.Write(data);
        return outMs.ToArray();
    }
}
