using System.Runtime.InteropServices;

namespace TModManager.App.Native;

/// <summary>user32 P/Invoke（对齐 Python 版 ctypes 调用）。</summary>
internal static class NativeMethods
{
    public const int WM_NCLBUTTONDOWN = 0x00A1;
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_SETCURSOR = 0x0020;
    public const int HTCAPTION = 0x0002;
    public const int HTCLIENT = 0x0001;
    public const int HTLEFT = 0x000A;
    public const int HTRIGHT = 0x000B;
    public const int HTTOP = 0x000C;
    public const int HTTOPLEFT = 0x000D;
    public const int HTTOPRIGHT = 0x000E;
    public const int HTBOTTOM = 0x000F;
    public const int HTBOTTOMLEFT = 0x0010;
    public const int HTBOTTOMRIGHT = 0x0011;
    public const int IDC_SIZEALL = 32650;    // move（四向箭头）
    public const int IDC_SIZENWSE = 32642;   // 左上↔右下
    public const int IDC_SIZENESW = 32643;   // 右上↔左下
    public const int IDC_SIZEWE = 32644;     // 左右
    public const int IDC_SIZENS = 32645;     // 上下
    public const uint WM_SETICON = 0x0080;
    public const uint SPI_GETWORKAREA = 0x0030;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    [DllImport("user32.dll")]
    public static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateIconFromResourceEx(byte[] presBits, uint dwResSize, int fIcon, uint dwVer, int cxDesired, int cyDesired, uint flags);
}
