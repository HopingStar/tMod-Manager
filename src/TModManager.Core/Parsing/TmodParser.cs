using System.IO.Compression;
using System.Text;

namespace TModManager.Core.Parsing;

/// <summary>
/// .tmod 二进制逆向（对齐 Python 版 modules/tmod_parser.py）：
/// TMOD 魔数 + 7-bit 长度字符串 + SHA1(20) + 签名(256) + U32 数据长度 + 模组名/版本
/// + I32 文件数 + 条目(文件名/未压缩长/压缩长) + 条目数据（raw DEFLATE）。
/// 只 seek 到 icon.png（精确 basename）和 Info 两个条目。
/// </summary>
public static class TmodParser
{
    private const uint TmodMagic = 0x444F4D54; // 'TMOD' little-endian（字节 54 4D 4F 44）

    public static TmodMeta? Parse(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt32() != TmodMagic) return null;

            Read7Str(br);        // tML 版本
            br.ReadBytes(20);    // SHA1
            br.ReadBytes(256);   // 签名
            br.ReadUInt32();     // 数据长度
            var modName = Read7Str(br);
            var modVersion = Read7Str(br);

            var count = Math.Max(br.ReadInt32(), 0);
            var entries = new List<(string Name, int Ulen, int Clen)>();
            for (var i = 0; i < count; i++)
            {
                var name = Read7Str(br);
                var ulen = br.ReadInt32();
                var clen = br.ReadInt32();
                entries.Add((name, ulen, clen));
            }

            // 计算各条目数据偏移，只定位 icon.png / Info
            long cur = br.BaseStream.Position;
            long iconPos = -1, infoPos = -1;
            int iconUlen = 0, iconClen = 0, infoUlen = 0, infoClen = 0;
            foreach (var (name, ulen, clen) in entries)
            {
                var low = name.ToLowerInvariant();
                if (iconPos < 0 && Path.GetFileName(low) == "icon.png")
                {
                    iconPos = cur; iconUlen = ulen; iconClen = clen;
                }
                else if (infoPos < 0 && name == "Info")
                {
                    infoPos = cur; infoUlen = ulen; infoClen = clen;
                }
                cur += clen;
            }

            byte[]? iconBytes = null;
            byte[]? infoBytes = null;
            if (iconPos >= 0) iconBytes = ReadEntry(br, iconPos, iconUlen, iconClen);
            if (infoPos >= 0) infoBytes = ReadEntry(br, infoPos, infoUlen, infoClen);

            if ((iconBytes is null or { Length: 0 }) && infoBytes is null) return null;

            // Info：7-bit 键值对，取 displayName / author / version
            string displayName = "", author = "", infoVersion = "";
            if (infoBytes is { Length: > 0 })
            {
                var pos = 0;
                while (pos < infoBytes.Length)
                {
                    if (!TryRead7(infoBytes, ref pos, out var kl) || pos + kl > infoBytes.Length) break;
                    var k = Encoding.UTF8.GetString(infoBytes, pos, kl); pos += kl;
                    if (!TryRead7(infoBytes, ref pos, out var vl) || pos + vl > infoBytes.Length) break;
                    var v = Encoding.UTF8.GetString(infoBytes, pos, vl); pos += vl;
                    if (k == "displayName") displayName = v;
                    else if (k == "author") author = v;
                    else if (k == "version") infoVersion = v;
                }
            }

            if ((iconBytes is null or { Length: 0 }) && displayName.Length == 0) return null;
            return new TmodMeta
            {
                Name = modName,
                DisplayName = displayName,
                Author = author,
                Version = string.IsNullOrEmpty(infoVersion) ? modVersion : infoVersion,
                IconBytes = iconBytes,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string Read7Str(BinaryReader br)
    {
        var n = br.Read7BitEncodedInt();
        return Encoding.UTF8.GetString(br.ReadBytes(Math.Max(n, 0)));
    }

    /// <summary>读取一个条目：seek 到偏移，读 clen 字节，ulen!=clen 时 raw DEFLATE 解压。</summary>
    private static byte[]? ReadEntry(BinaryReader br, long pos, int ulen, int clen)
    {
        br.BaseStream.Seek(pos, SeekOrigin.Begin);
        var data = br.ReadBytes(clen);
        if (ulen == clen) return data;
        try
        {
            using var ms = new MemoryStream(data);
            using var ds = new DeflateStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            ds.CopyTo(outMs);
            return outMs.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>.NET BinaryReader 的 7-bit 编码整数（对字节数组实现）。</summary>
    private static bool TryRead7(byte[] data, ref int pos, out int result)
    {
        result = 0;
        var shift = 0;
        while (true)
        {
            if (pos >= data.Length) return false;
            var b = data[pos++];
            result |= (b & 0x7f) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 28) return false;
        }
        return true;
    }
}
