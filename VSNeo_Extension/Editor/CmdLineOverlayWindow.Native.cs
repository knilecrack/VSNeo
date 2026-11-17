using System;
using System.Runtime.InteropServices;

namespace VSNeo_Extension.Editor;

// The window-style bits WPF does not expose: no-activate, DPI, and the rect struct.
internal static partial class CmdLineOverlayWindow
{
    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000;

    // The *Ptr pair exists on x64, which is the only place Visual Studio
    // 2022+ runs; there is no 32-bit fallback to worry about.
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
