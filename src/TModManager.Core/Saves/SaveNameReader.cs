// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using System.Security.Cryptography;
using System.Text;

namespace TModManager.Core.Saves;

/// <summary>
/// 从存档文件**内部**读出「游戏里显示的那个名字」。
///
/// 依据 Terraria / tModLoader 源码（本机 tModLoader9）核对：
///   · <c>Terraria.IO.FileData.Name</c> 才是游戏列表显示的名字；<c>GetFileName()</c> 只是文件名。
///   · 世界 <c>.wld</c>：明文。结构 = int32 版本(279) + FileMetadata(20B) + int16 指针数 + int32[] 指针；
///     第 0 个指针指向「头部段」，段首即 7-bit 长度前缀的 <c>Main.worldName</c>（WorldFile.SaveWorldHeader）。
///   · 人物 <c>.plr</c>：**整个文件经 Rijndael/AES-128-CBC 加密**，key = IV = UTF16LE("h3y_gUyZ")
///     （<c>Player.ENCRYPTION_KEY</c>，见 Player.SavePlayerFile_Vanilla）；解密后 =
///     int32 版本(279) + FileMetadata(20B) + 7-bit 长度前缀的 <c>newPlayer.name</c>。
///
/// ⇒ 所以「游戏里的名字」和文件名**可以完全不同**（把文件改名并不会改存档内的名字）。
/// 读不出来时一律返回 null，调用方回退到文件名。
/// </summary>
public static class SaveNameReader
{
    /// <summary>FileMetadata 固定 20 字节：magic(8) + revision(4) + flags(8)。</summary>
    private const int MetadataSize = 20;

    /// <summary>Player.ENCRYPTION_KEY —— UTF-16LE 编码的 "h3y_gUyZ"，正好 16 字节（AES-128）。</summary>
    private static readonly byte[] EncryptionKey = Encoding.Unicode.GetBytes("h3y_gUyZ");

    public static string? TryReadWorldName(string path)
    {
        try { return ReadWorldName(path); } catch { return null; }
    }

    public static string? TryReadPlayerName(string path)
    {
        try { return ReadPlayerName(path); } catch { return null; }
    }

    private static string? ReadWorldName(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs, Encoding.UTF8);

        br.ReadInt32();                       // 版本号（当前为 279）
        br.ReadBytes(MetadataSize);           // FileMetadata
        int pointerCount = br.ReadInt16();
        if (pointerCount is <= 0 or > 1024) return null;
        int nameOffset = br.ReadInt32();      // pointers[0] = 头部段在文件中的偏移
        if (nameOffset <= 0 || nameOffset >= fs.Length) return null;

        fs.Position = nameOffset;
        return Read7BitString(br);
    }

    private static string? ReadPlayerName(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length == 0 || data.Length % 16 != 0) return null;

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = EncryptionKey;
        aes.IV = EncryptionKey;

        byte[] plain;
        using (var decryptor = aes.CreateDecryptor())
            plain = decryptor.TransformFinalBlock(data, 0, data.Length);

        using var ms = new MemoryStream(plain);
        using var br = new BinaryReader(ms, Encoding.UTF8);
        br.ReadInt32();                       // 版本号（当前为 279）
        br.ReadBytes(MetadataSize);           // FileMetadata
        return Read7BitString(br);
    }

    /// <summary>读取 .NET <c>BinaryWriter.Write(string)</c 写出的 7-bit 长度前缀 + UTF-8 字符串。</summary>
    private static string Read7BitString(BinaryReader br)
    {
        int length = 0, shift = 0;
        while (true)
        {
            byte b = br.ReadByte();
            length |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 28) throw new FormatException("字符串长度字段损坏");
        }
        if (length is < 0 or > 1_000_000) throw new FormatException("字符串过长");
        return Encoding.UTF8.GetString(br.ReadBytes(length));
    }
}
