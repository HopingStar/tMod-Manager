// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using System.Security.Cryptography;
using System.Text;

namespace TModManager.Core.Saves;

/// <summary>世界存档（<c>.wld</c>）头部里的可读信息。</summary>
public sealed class WorldDetails
{
    public string Name { get; set; } = "";

    /// <summary>存档格式版本（当前为 279）。</summary>
    public int FileVersion { get; set; }

    /// <summary>世界种子文本（1.4 起保存在头部）。</summary>
    public string Seed { get; set; } = "";

    public ulong GeneratorVersion { get; set; }

    public string UniqueId { get; set; } = "";

    public long WorldId { get; set; }

    public int SizeX { get; set; }

    public int SizeY { get; set; }

    /// <summary>小 / 中 / 大 / 未知。</summary>
    public string SizeName { get; set; } = "";
    public string SizeKey { get; set; } = "";          // small | medium | large | unknown（前端配色用）

    /// <summary>0 = 经典 / 1 = 专家 / 2 = 大师 / 3 = 旅途。</summary>
    public int GameMode { get; set; }

    public string GameModeName { get; set; } = "";
    public string GameModeKey { get; set; } = "";      // classic | expert | master | journey

    /// <summary>true = 猩红，false = 腐化。</summary>
    public bool IsCrimson { get; set; }

    public string EvilName => IsCrimson ? "猩红" : "腐化";

    public bool IsHardMode { get; set; }

    public DateTime CreationTime { get; set; }

    /// <summary>特殊世界种子（醉酒世界 / 天顶世界 / 饥荒……）。</summary>
    public List<string> SpecialSeeds { get; set; } = new();
}

/// <summary>人物存档（<c>.plr</c>）里的可读信息。</summary>
public sealed class PlayerDetails
{
    public string Name { get; set; } = "";
    public int FileVersion { get; set; }

    /// <summary>0 = 软核 / 1 = 中核 / 2 = 硬核 / 3 = 旅途。</summary>
    public int Difficulty { get; set; }

    public string DifficultyName { get; set; } = "";
    public string DifficultyKey { get; set; } = "";    // softcore | mediumcore | hardcore | journey

    /// <summary>该人物的累计游玩时长（秒）。</summary>
    public double PlaySeconds { get; set; }

    public int Hair { get; set; }
    public int SkinVariant { get; set; }

    public int StatLife { get; set; }
    public int StatLifeMax { get; set; }
    public int StatMana { get; set; }
    public int StatManaMax { get; set; }
}

/// <summary>
/// 从存档文件内部读取结构化信息。
///
/// 世界（<c>.wld</c>）：完全按 <c>Terraria.IO.WorldFile.GetAllMetadata</c>（本机 tModLoader 1.4.4.9）的读取顺序解析 ——
/// 版本(Int32) + FileMetadata(20B) + Int16 指针数 + Int32[指针]；指针[0] 指向头部段：
/// 名称 / 种子 / 生成器版本(UInt64) / Guid(16B) / 世界ID / 四边界 / 高宽 /
/// 游戏模式(Int32) / 8 个世界类型开关(bool) / 创建时间(Int64 DateTimeBinary) / 月相(Byte) /
/// 19 个 Int32 / 3 个 Double / … / 「猩红」bool / 一串 boss 标志 / 困难模式 bool。
///
/// 人物（<c>.plr</c>）：整个文件 Rijndael/AES-128-CBC 加密（key = IV = UTF16LE("h3y_gUyZ")），
/// 解密后 = 版本(Int32) + FileMetadata(20B) + 名称(7-bit 长度+UTF8) + 难度(Byte) + 游玩时长(Int64 ticks)
/// + 发型(Int32) + 发色(Byte) + 2 个 BitsByte + hideMisc(Byte) + 肤色(Byte) + 生命/生命上限/魔力/魔力上限(Int32×4)。
///
/// 读不出来（旧版本 / 文件损坏 / 被游戏独占）时一律返回 null，调用方回退到文件名。
/// </summary>
public static class SaveDetailsReader
{
    private const int MetadataSize = 20;
    private static readonly byte[] EncryptionKey = Encoding.Unicode.GetBytes("h3y_gUyZ");

    private static readonly (string Key, string Label)[] SpecialSeedFlags =
    {
        ("zenith", "天顶世界"),
        ("remix", "颠倒世界"),
        ("notthebees", "不是蜜蜂"),
        ("dontstarve", "饥荒"),
        ("anniversary", "十周年"),
        ("worthy", "值得一战"),
        ("drunk", "醉酒世界"),
        ("notraps", "无陷阱"),
    };

    // ---------------- 世界 ----------------

    public static WorldDetails? TryReadWorld(string path)
    {
        try { return ReadWorld(path); }
        catch { return null; }
    }

    private static WorldDetails? ReadWorld(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs, Encoding.UTF8);

        var version = br.ReadInt32();
        if (version < 100) return null;

        br.ReadBytes(MetadataSize);            // FileMetadata（magic / revision / flags）
        var pointerCount = br.ReadInt16();
        if (pointerCount <= 0 || pointerCount > 1024) return null;

        var headerOffset = br.ReadInt32();     // pointers[0] = 头部段偏移
        if (headerOffset <= 0 || headerOffset >= fs.Length) return null;

        fs.Position = headerOffset;

        var details = new WorldDetails { FileVersion = version };
        details.Name = Read7BitString(br);

        // 种子从 1.4（版本 179）起进头部；179 那一版写的是整数，之后是字符串
        if (version >= 179)
        {
            details.Seed = version == 179 ? br.ReadInt32().ToString() : Read7BitString(br);
            details.GeneratorVersion = br.ReadUInt64();
        }

        if (version >= 181)
        {
            var guid = br.ReadBytes(16);
            details.UniqueId = guid.Length == 16 ? new Guid(guid).ToString() : "";
        }

        details.WorldId = br.ReadInt32();
        br.ReadInt32();                        // left
        br.ReadInt32();                        // right
        br.ReadInt32();                        // top
        br.ReadInt32();                        // bottom
        var sizeY = br.ReadInt32();
        var sizeX = br.ReadInt32();
        details.SizeX = sizeX;
        details.SizeY = sizeY;
        (details.SizeName, details.SizeKey) = sizeX switch
        {
            4200 => ("小世界", "small"),
            6400 => ("中世界", "medium"),
            8400 => ("大世界", "large"),
            _ => ("未知大小", "unknown"),
        };

        if (version >= 209)
        {
            details.GameMode = br.ReadInt32();
        }
        else if (version >= 112)
        {
            details.GameMode = br.ReadBoolean() ? 1 : 0;   // 1.4 之前只有「专家」开关
        }

        // 世界类型开关（版本逐代增加）
        var special = new List<string>();
        if (version >= 222)
        {
            if (br.ReadBoolean()) special.Add("drunk");
            if (version >= 227 && br.ReadBoolean()) special.Add("worthy");
            if (version >= 238 && br.ReadBoolean()) special.Add("anniversary");
            if (version >= 239 && br.ReadBoolean()) special.Add("dontstarve");
            if (version >= 241 && br.ReadBoolean()) special.Add("notthebees");
            if (version >= 249 && br.ReadBoolean()) special.Add("remix");
            if (version >= 266 && br.ReadBoolean()) special.Add("notraps");

            var zenith = version >= 267
                ? br.ReadBoolean()
                : special.Contains("drunk") && special.Contains("remix");
            if (zenith) special.Add("zenith");
        }

        details.SpecialSeeds = SpecialSeedFlags
            .Where(f => special.Contains(f.Key))
            .Select(f => f.Label)
            .ToList();

        details.CreationTime = version >= 141
            ? DateTime.FromBinary(br.ReadInt64())
            : File.GetCreationTime(path);

        // 到「猩红」之前的固定字段（顺序与 GetAllMetadata 完全一致）
        br.ReadByte();                         // moonType
        for (var i = 0; i < 19; i++) br.ReadInt32();
        for (var i = 0; i < 3; i++) br.ReadDouble();
        br.ReadBoolean();
        br.ReadInt32();
        br.ReadBoolean();
        br.ReadBoolean();
        br.ReadInt32();                        // dungeonX
        br.ReadInt32();                        // dungeonY
        details.IsCrimson = br.ReadBoolean();

        // boss 标志：10 个 + 老版本额外 1 个 + 9 个，然后 shadowOrbCount / altarCount，最后才是困难模式
        for (var i = 0; i < 10; i++) br.ReadBoolean();
        if (version >= 118) br.ReadBoolean();
        for (var i = 0; i < 9; i++) br.ReadBoolean();
        br.ReadByte();
        br.ReadInt32();
        details.IsHardMode = br.ReadBoolean();

        (details.GameModeName, details.GameModeKey) = details.GameMode switch
        {
            1 => ("专家", "expert"),
            2 => ("大师", "master"),
            3 => ("旅途", "journey"),
            _ => ("经典", "classic"),
        };

        return details;
    }

    // ---------------- 人物 ----------------

    public static PlayerDetails? TryReadPlayer(string path)
    {
        try { return ReadPlayer(path); }
        catch { return null; }
    }

    private static PlayerDetails? ReadPlayer(string path)
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

        var version = br.ReadInt32();
        if (version < 100) return null;

        br.ReadBytes(MetadataSize);

        var details = new PlayerDetails
        {
            FileVersion = version,
            Name = Read7BitString(br),
            Difficulty = br.ReadByte(),
        };

        var ticks = br.ReadInt64();
        details.PlaySeconds = Math.Max(0, ticks) / (double)TimeSpan.TicksPerSecond;

        // 名称 / 难度 / 时长之后依次是：发型(Int32) 发色(Byte) 两个 BitsByte hideMisc(Byte) 肤色(Byte)
        details.Hair = br.ReadInt32();
        br.ReadByte();                         // hairDye
        br.ReadByte();                         // hideVisibleAccessory[0..7]
        br.ReadByte();                         // hideVisibleAccessory[8..9]
        br.ReadByte();                         // hideMisc
        details.SkinVariant = br.ReadByte();

        details.StatLife = br.ReadInt32();
        details.StatLifeMax = br.ReadInt32();
        details.StatMana = br.ReadInt32();
        details.StatManaMax = br.ReadInt32();

        (details.DifficultyName, details.DifficultyKey) = details.Difficulty switch
        {
            1 => ("中核", "mediumcore"),
            2 => ("硬核", "hardcore"),
            3 => ("旅途", "journey"),
            _ => ("软核", "softcore"),
        };

        return details;
    }

    // ---------------- 基础读取 ----------------

    /// <summary>读取 .NET <c>BinaryWriter.Write(string)</c> 写出的 7-bit 长度前缀 + UTF-8 字符串。</summary>
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
