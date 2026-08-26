using TModManager.Core.Icons;

namespace TModManager.Core.Parsing;

/// <summary>把 .tmod 解出的 icon.png 加密保存到 icon_dir/{哈希}.dat，返回访问 URL /api/icon/{哈希}。
/// 防普通用户：看图软件打不开 .dat，文件名哈希看不出对应模组；前端仍能正常显示（走 /api/icon 解密输出）。</summary>
public static class IconSaver
{
    public static string? Save(string name, byte[]? iconBytes, string iconDir)
    {
        if (iconBytes is null or { Length: 0 }) return null;
        try
        {
            Directory.CreateDirectory(iconDir);
            var file = Path.Combine(iconDir, IconObfuscator.FileNameWithExt(name));
            File.WriteAllBytes(file, IconObfuscator.Encrypt(iconBytes));
            return "/api/icon/" + IconObfuscator.FileName(name);
        }
        catch
        {
            return null;
        }
    }
}
