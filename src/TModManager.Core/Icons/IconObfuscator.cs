using System.Security.Cryptography;
using System.Text;

namespace TModManager.Core.Icons;

/// <summary>图标混淆（目标：防普通用户用看图软件直接打开 / 从文件名猜模组）。
/// XOR 加密 + SHA256 哈希文件名。注意这只是防君子不防小人的混淆——密钥写死在本地程序里，
/// 抓本地流量或反编译 exe 仍可还原（与 tmod_icon.dat 的窗口图标加密同理）。</summary>
public static class IconObfuscator
{
    private const byte Key = 0xA7;              // 与窗口图标 tmod_icon.dat(0x5A) 区分开
    private const string Extension = ".dat";

    public static byte[] Encrypt(byte[] data) => Xor(data);
    public static byte[] Decrypt(byte[] data) => Xor(data);

    /// <summary>模组名 → 稳定哈希名（不含后缀，16 位 hex）。哈希单向，无法反推模组名。</summary>
    public static string FileName(string name)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>磁盘文件名（含 .dat 后缀）。</summary>
    public static string FileNameWithExt(string name) => FileName(name) + Extension;

    /// <summary>哈希是否合法（16 位小写 hex），供 /api/icon 路由校验防路径遍历。</summary>
    public static bool IsValidHash(string? hash)
        => hash is { Length: 16 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static byte[] Xor(byte[] data)
    {
        var r = new byte[data.Length];
        for (int i = 0; i < data.Length; i++) r[i] = (byte)(data[i] ^ Key);
        return r;
    }
}
