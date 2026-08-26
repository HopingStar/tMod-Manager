namespace TModManager.Core.Icons;

/// <summary>图标 XOR 加密（密钥 0x5A）与 ICO 目录解析（对齐 Python 版 icon_util）。</summary>
public static class IconCodec
{
    public const byte Key = 0x5A;

    /// <summary>逐字节异或 0x5A（加密/解密同函数）。</summary>
    public static byte[] Xor(byte[] data)
    {
        var r = new byte[data.Length];
        for (int i = 0; i < data.Length; i++) r[i] = (byte)(data[i] ^ Key);
        return r;
    }

    /// <summary>校验是否为有效 ICO：前 4 字节 00 00 01 00。</summary>
    public static bool IsIco(byte[] data)
        => data.Length >= 6 && data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == 0;

    /// <summary>读 .dat 异或还原，校验 ICO 后返回原始 ICO bytes；失败返回 null。</summary>
    public static byte[]? DecryptIcon(string datPath)
    {
        if (!File.Exists(datPath)) return null;
        try
        {
            var data = Xor(File.ReadAllBytes(datPath));
            return IsIco(data) ? data : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从 ICO 挑选 32x32 或最大的图像段，返回该段字节（对齐 Python _pick_best_image）。</summary>
    public static byte[]? PickBestImage(byte[] ico)
    {
        try
        {
            var count = BitConverter.ToUInt16(ico, 4);
            int bestIdx = -1, bestW = 0, bestH = 0;
            for (int i = 0; i < count; i++)
            {
                int off = 6 + i * 16;
                if (off + 16 > ico.Length) break;
                int w = ico[off] == 0 ? 256 : ico[off];
                int h = ico[off + 1] == 0 ? 256 : ico[off + 1];
                var sizeInRes = BitConverter.ToUInt32(ico, off + 8);
                var imgOff = BitConverter.ToUInt32(ico, off + 12);
                if (sizeInRes == 0 || imgOff + sizeInRes > ico.Length) continue;

                if (bestIdx < 0
                    || (w == 32 && h == 32)
                    || (bestW != 32 && w <= 32 && w >= bestW))
                {
                    bestIdx = i;
                    bestW = w;
                    bestH = h;
                }
            }
            if (bestIdx < 0) return null;

            int boff = 6 + bestIdx * 16;
            var bs = BitConverter.ToUInt32(ico, boff + 8);
            var bimg = BitConverter.ToUInt32(ico, boff + 12);
            return ico[(int)bimg..(int)(bimg + bs)];
        }
        catch
        {
            return null;
        }
    }
}
