using System.IO;
using System.Windows;
using System.Windows.Interop;
using TModManager.Core.Icons;

namespace TModManager.App.Native;

/// <summary>解密 tmod_icon.dat 并设为窗口/任务栏图标（WM_SETICON），失败静默。</summary>
internal static class WindowIconService
{
    public static void TrySetWindowIcon(Window window, string datPath)
    {
        try
        {
            if (!File.Exists(datPath)) return;
            var ico = IconCodec.DecryptIcon(datPath);
            if (ico is null) return;
            var segment = IconCodec.PickBestImage(ico);
            if (segment is null || segment.Length == 0) return;

            var hwnd = new WindowInteropHelper(window).Handle;
            var hicon = NativeMethods.CreateIconFromResourceEx(segment, (uint)segment.Length, 1, 0x00030000, 0, 0, 0);
            if (hicon == IntPtr.Zero) return;

            _ = NativeMethods.SendMessage(hwnd, NativeMethods.WM_SETICON, IntPtr.Zero, hicon); // 小图标
            _ = NativeMethods.SendMessage(hwnd, NativeMethods.WM_SETICON, (IntPtr)1, hicon);   // 大图标（任务栏）
        }
        catch
        {
            // 静默，不影响启动
        }
    }
}
