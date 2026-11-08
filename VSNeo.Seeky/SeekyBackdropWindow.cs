namespace VSNeo.Seeky;

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

/// <summary>
/// The non-activating acrylic layer over Visual Studio while Seeky is open. The picker uses the
/// same blurred surface through its transparent client area, rather than applying a second blur.
/// </summary>
internal sealed class SeekyBackdropWindow : Window
{
    private readonly IntPtr ownerHwnd;

    internal SeekyBackdropWindow(IntPtr ownerHwnd)
    {
        this.ownerHwnd = ownerHwnd;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        Topmost = false;
        ShowActivated = false;
        ShowInTaskbar = false;
        Focusable = false;

        if (ownerHwnd != IntPtr.Zero)
        {
            new WindowInteropHelper(this).Owner = ownerHwnd;
        }

        SourceInitialized += (_, _) =>
            AcrylicWindowEffect.Apply(new WindowInteropHelper(this).Handle);
    }

    internal event Action? DismissRequested;

    internal void ShowOverOwner()
    {
        if (ownerHwnd != IntPtr.Zero && GetWindowRect(ownerHwnd, out RECT rect))
        {
            double scale = OwnerDpiScale();
            Left = rect.Left / scale;
            Top = rect.Top / scale;
            Width = (rect.Right - rect.Left) / scale;
            Height = (rect.Bottom - rect.Top) / scale;
        }
        else
        {
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
        }

        if (!IsVisible)
        {
            Show();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        DismissRequested?.Invoke();
        e.Handled = true;
        base.OnMouseLeftButtonDown(e);
    }

    private double OwnerDpiScale()
    {
        uint dpi = GetDpiForWindow(ownerHwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
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

internal static class AcrylicWindowEffect
{
    internal static void Apply(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var accent = new AccentPolicy
        {
            AccentState = AccentEnableAcrylicBlurBehind,
            GradientColor = unchecked((int)0x20000000),
        };
        int accentSize = Marshal.SizeOf<AccentPolicy>();
        IntPtr accentPtr = Marshal.AllocHGlobal(accentSize);
        try
        {
            Marshal.StructureToPtr(accent, accentPtr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute,
                Data = accentPtr,
                SizeOfData = accentSize,
            };
            if (SetWindowCompositionAttribute(hwnd, ref data) == 0)
            {
                SeekyLog.Info($"Seeky backdrop: SetWindowCompositionAttribute failed (win32 {Marshal.GetLastWin32Error()})");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(accentPtr);
        }
    }

    private const int AccentEnableAcrylicBlurBehind = 4;
    private const int WindowCompositionAttribute = 19;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }
}
