using System.Security.Cryptography;
using System.Text;
using TModManager.Core.Saves;

namespace TModManager.Tests;

/// <summary>
/// 存档内部名称读取（游戏里显示的名字 ≠ 文件名）的回归测试。
/// 用合成文件覆盖两种真实格式：世界 .wld（明文头）、人物 .plr（AES-128-CBC 整文件加密）。
/// </summary>
public class SaveNameReaderTests
{
    private static readonly byte[] Key = Encoding.Unicode.GetBytes("h3y_gUyZ");   // UTF-16LE，与 Player.ENCRYPTION_KEY 一致

    /// <summary>模拟 .NET BinaryWriter.Write(string)：7-bit 长度前缀 + UTF-8。</summary>
    private static byte[] SevenBit(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        using var ms = new MemoryStream();
        int len = bytes.Length;
        while (len >= 0x80) { ms.WriteByte((byte)(len | 0x80)); len >>= 7; }
        ms.WriteByte((byte)len);
        ms.Write(bytes, 0, bytes.Length);
        return ms.ToArray();
    }

    /// <summary>写一个最小可解析的世界文件：版本 + FileMetadata(20B) + 指针表 + 头部段（段首即名字）。</summary>
    private static void WriteWorld(string path, string worldName)
    {
        const int pointerCount = 1;
        int nameOffset = 4 + 20 + 2 + 4 * pointerCount;   // 指针表之后就是头部段

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write(279);                     // 版本
        bw.Write(new byte[20]);            // FileMetadata
        bw.Write((short)pointerCount);
        bw.Write(nameOffset);              // pointers[0]
        Assert.Equal(nameOffset, (int)fs.Position);
        bw.Write(SevenBit(worldName));
    }

    private static void WritePlayer(string path, string playerName)
    {
        using var plain = new MemoryStream();
        using (var pw = new BinaryWriter(plain, Encoding.UTF8, leaveOpen: true))
        {
            pw.Write(279);
            pw.Write(new byte[20]);        // FileMetadata
            pw.Write(SevenBit(playerName));
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
    public void World_Name_ComesFromInsideFile_NotFileName()
    {
        using var tmp = new TestPaths();
        var path = Path.Combine(tmp.BaseDir, "File_Name_Here.wld");
        WriteWorld(path, "内部世界名 123");

        Assert.Equal("内部世界名 123", SaveNameReader.TryReadWorldName(path));
    }

    [Fact]
    public void Player_Name_ComesFromEncryptedFile_NotFileName()
    {
        using var tmp = new TestPaths();
        var path = Path.Combine(tmp.BaseDir, "SomeFile.plr");
        WritePlayer(path, "内部角色名");

        Assert.Equal("内部角色名", SaveNameReader.TryReadPlayerName(path));
    }

    [Fact]
    public void UnreadableFiles_ReturnNull_SoCallerCanFallBackToFileName()
    {
        using var tmp = new TestPaths();
        var w = Path.Combine(tmp.BaseDir, "bad.wld");
        var p = Path.Combine(tmp.BaseDir, "bad.plr");
        File.WriteAllText(w, "not a world");
        File.WriteAllText(p, "not a player");     // 长度不是 16 的倍数 ⇒ 解密必失败

        Assert.Null(SaveNameReader.TryReadWorldName(w));
        Assert.Null(SaveNameReader.TryReadPlayerName(p));
    }

    [Fact]
    public void SavesService_ShowsInternalName_AndDoesNotDoubleListTplr()
    {
        using var tmp = new TestPaths();
        var worldsDir = Path.Combine(tmp.BaseDir, "Worlds");
        var playersDir = Path.Combine(tmp.BaseDir, "Players");
        Directory.CreateDirectory(worldsDir);
        Directory.CreateDirectory(playersDir);

        WriteWorld(Path.Combine(worldsDir, "Nyarlathotep.wld"), "真名世界");
        WritePlayer(Path.Combine(playersDir, "zxcv.plr"), "真名角色");
        File.WriteAllText(Path.Combine(playersDir, "zxcv.tplr"), "mod side-car");   // 伴生文件，不应单独成条

        var saves = new SavesService(tmp.AppPaths);

        var worlds = saves.ListWorlds();
        Assert.Single(worlds);
        Assert.Equal("真名世界", worlds[0].Name);          // 名字取自文件内部
        Assert.Equal("Nyarlathotep.wld", worlds[0].FileName); // 文件名照常保留

        var players = saves.ListPlayers();
        Assert.Single(players);                             // .plr + 同名 .tplr 只算一条
        Assert.Equal("真名角色", players[0].Name);
    }
}
