// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using System.Security.Cryptography;
using System.Text;
using TModManager.Core.Saves;

namespace TModManager.Tests;

/// <summary>
/// 世界 / 人物存档内部数据的解析测试。
/// 合成文件严格按 <c>Terraria.IO.WorldFile.GetAllMetadata</c> 与 <c>Player.Serialize</c> 的字段顺序构造，
/// 覆盖：名称、种子、大小、难度、腐化/猩红、困难模式、特殊种子、创建时间、人物难度 / 时长 / 生命魔力上限。
/// </summary>
public class SaveDetailsReaderTests
{
    private static readonly byte[] Key = Encoding.Unicode.GetBytes("h3y_gUyZ");
    private static readonly DateTime Creation = new(2024, 5, 6, 7, 8, 9, DateTimeKind.Unspecified);

    private static void Write7(BinaryWriter w, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        w.Write7BitEncodedInt(bytes.Length);
        w.Write(bytes);
    }

    /// <summary>版本 279 的完整世界头部（读到「困难模式」为止，后面的字段本解析器不再需要）。</summary>
    private static void WriteWorld(string path)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);

        w.Write(279);
        w.Write(new byte[20]);                 // FileMetadata
        w.Write((short)2);
        var pointerPos = fs.Position;
        w.Write(0);                            // pointer[0] 占位
        var headerPos = (int)fs.Position;

        Write7(w, "测试世界");
        Write7(w, "123456");
        w.Write(987_654_321UL);                // WorldGeneratorVersion
        w.Write(Guid.Parse("11111111-2222-3333-4444-555555555555").ToByteArray());
        w.Write(42);                           // worldId
        for (var i = 0; i < 4; i++) w.Write(0); // left/right/top/bottom
        w.Write(2400);                         // sizeY
        w.Write(8400);                         // sizeX → 大世界
        w.Write(2);                            // 大师
        w.Write(true);                         // drunk
        w.Write(false);                        // worthy
        w.Write(false);                        // anniversary
        w.Write(false);                        // dontstarve
        w.Write(false);                        // notthebees
        w.Write(false);                        // remix
        w.Write(false);                        // notraps
        w.Write(false);                        // zenith
        w.Write(Creation.ToBinary());
        w.Write((byte)0);                      // moonType
        for (var i = 0; i < 19; i++) w.Write(0);
        for (var i = 0; i < 3; i++) w.Write(0.0);
        w.Write(false);                        // tempDayTime
        w.Write(0);                            // tempMoonPhase
        w.Write(false);                        // tempBloodMoon
        w.Write(false);                        // tempEclipse
        w.Write(0);                            // dungeonX
        w.Write(0);                            // dungeonY
        w.Write(true);                         // HasCrimson
        for (var i = 0; i < 10; i++) w.Write(false);
        w.Write(false);                        // version >= 118
        for (var i = 0; i < 9; i++) w.Write(false);
        w.Write((byte)0);                      // shadowOrbCount
        w.Write(0);                            // altarCount
        w.Write(true);                         // hardMode

        fs.Position = pointerPos;
        w.Write(headerPos);
    }

    private static void WritePlayer(string path)
    {
        using var plain = new MemoryStream();
        using (var w = new BinaryWriter(plain, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(279);
            w.Write(new byte[20]);             // FileMetadata
            Write7(w, "测试角色");
            w.Write((byte)2);                  // 硬核
            w.Write(3600L * TimeSpan.TicksPerSecond);   // 1 小时
            w.Write(7);                        // hair
            w.Write((byte)0);                  // hairDye
            w.Write((byte)0);                  // hideVisibleAccessory 低 8 位
            w.Write((byte)0);                  // hideVisibleAccessory 高 2 位
            w.Write((byte)0);                  // hideMisc
            w.Write((byte)3);                  // skinVariant
            w.Write(400);                      // statLife
            w.Write(500);                      // statLifeMax
            w.Write(100);                      // statMana
            w.Write(200);                      // statManaMax
        }

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = Key;
        aes.IV = Key;
        using var enc = aes.CreateEncryptor();
        File.WriteAllBytes(path, enc.TransformFinalBlock(plain.ToArray(), 0, (int)plain.Length));
    }

    [Fact]
    public void World_Details_AreReadFromHeader()
    {
        using var tmp = new TestPaths();
        var path = Path.Combine(tmp.BaseDir, "whatever.wld");
        WriteWorld(path);

        var d = SaveDetailsReader.TryReadWorld(path);

        Assert.NotNull(d);
        Assert.Equal("测试世界", d!.Name);
        Assert.Equal(279, d.FileVersion);
        Assert.Equal("123456", d.Seed);
        Assert.Equal(8400, d.SizeX);
        Assert.Equal(2400, d.SizeY);
        Assert.Equal("大世界", d.SizeName);
        Assert.Equal("large", d.SizeKey);
        Assert.Equal(2, d.GameMode);
        Assert.Equal("大师", d.GameModeName);
        Assert.True(d.IsCrimson);
        Assert.Equal("猩红", d.EvilName);
        Assert.True(d.IsHardMode);
        Assert.Equal(Creation, d.CreationTime);
        Assert.Contains("醉酒世界", d.SpecialSeeds);
        Assert.Single(d.SpecialSeeds);            // 其他世界类型开关都是 false
    }

    [Fact]
    public void Player_Details_AreReadFromEncryptedFile()
    {
        using var tmp = new TestPaths();
        var path = Path.Combine(tmp.BaseDir, "whatever.plr");
        WritePlayer(path);

        var p = SaveDetailsReader.TryReadPlayer(path);

        Assert.NotNull(p);
        Assert.Equal("测试角色", p!.Name);
        Assert.Equal(2, p.Difficulty);
        Assert.Equal("硬核", p.DifficultyName);
        Assert.Equal("hardcore", p.DifficultyKey);
        Assert.Equal(3600, p.PlaySeconds, 3);
        Assert.Equal(7, p.Hair);
        Assert.Equal(3, p.SkinVariant);
        Assert.Equal(400, p.StatLife);
        Assert.Equal(500, p.StatLifeMax);
        Assert.Equal(100, p.StatMana);
        Assert.Equal(200, p.StatManaMax);
    }

    [Fact]
    public void TruncatedWorld_ReturnsNull_ButNameOnlyReaderStillWorks()
    {
        using var tmp = new TestPaths();
        var path = Path.Combine(tmp.BaseDir, "name-only.wld");

        using (var fs = File.Create(path))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(279);
            w.Write(new byte[20]);
            w.Write((short)1);
            var pointerPos = fs.Position;
            w.Write(0);
            var headerPos = (int)fs.Position;
            Write7(w, "只有名字");
            fs.Position = pointerPos;
            w.Write(headerPos);
        }

        Assert.Null(SaveDetailsReader.TryReadWorld(path));      // 头部不完整
        Assert.Equal("只有名字", SaveNameReader.TryReadWorldName(path));   // 轻量解析兜底
    }

    [Fact]
    public void SavesService_AttachesDetails_AndKeepsFallbackName()
    {
        using var tmp = new TestPaths();
        var worldsDir = Path.Combine(tmp.BaseDir, "Worlds");
        var playersDir = Path.Combine(tmp.BaseDir, "Players");
        Directory.CreateDirectory(worldsDir);
        Directory.CreateDirectory(playersDir);

        WriteWorld(Path.Combine(worldsDir, "a.wld"));
        WritePlayer(Path.Combine(playersDir, "b.plr"));

        var saves = new SavesService(tmp.AppPaths);

        var world = Assert.Single(saves.ListWorlds());
        Assert.Equal("测试世界", world.Name);
        Assert.NotNull(world.World);
        Assert.True(world.World!.IsHardMode);
        Assert.Null(world.Player);

        var player = Assert.Single(saves.ListPlayers());
        Assert.Equal("测试角色", player.Name);
        Assert.NotNull(player.Player);
        Assert.Equal("硬核", player.Player!.DifficultyName);
        Assert.Null(player.World);
    }

    [Fact]
    public void GarbageFiles_ReturnNull()
    {
        using var tmp = new TestPaths();
        var w = Path.Combine(tmp.BaseDir, "bad.wld");
        var p = Path.Combine(tmp.BaseDir, "bad.plr");
        File.WriteAllText(w, "not a world");
        File.WriteAllText(p, "not a player");

        Assert.Null(SaveDetailsReader.TryReadWorld(w));
        Assert.Null(SaveDetailsReader.TryReadPlayer(p));
    }
}
