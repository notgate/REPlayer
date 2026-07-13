using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ReVM;

/// <summary>
/// A real Win32 child HWND hosted inside WPF. External render windows
/// (vmconnect/scrcpy/SDL/etc.) must be parented to this HWND, not the
/// top-level WPF window, or they float over the title bar/toolbars.
/// </summary>
public sealed class NativeChildHost : HwndHost
{
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int SS_BLACKRECT = 0x00000004;

    private IntPtr _hwnd;

    public IntPtr DisplayHandle => _hwnd;
    public event Action<int, int, int, int, int>? AndroidTouchRequested;

    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int MK_LBUTTON = 0x0001;
    private int _downX;
    private int _downY;
    private long _downTicks;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle,
        string lpClassName,
        string? lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwnd = CreateWindowExW(
            0,
            "static",
            null,
            WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN | SS_BLACKRECT,
            0,
            0,
            1,
            1,
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowExW failed: {Marshal.GetLastWin32Error()}");

        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero)
            DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_LBUTTONDOWN:
                _downX = LowWord(lParam);
                _downY = HighWord(lParam);
                _downTicks = Environment.TickCount64;
                handled = true;
                break;
            case WM_LBUTTONUP:
            {
                var upX = LowWord(lParam);
                var upY = HighWord(lParam);
                var duration = (int)Math.Clamp(Environment.TickCount64 - _downTicks, 40, 1200);
                AndroidTouchRequested?.Invoke(_downX, _downY, upX, upY, duration);
                handled = true;
                break;
            }
            case WM_MOUSEMOVE:
                if (((int)wParam & MK_LBUTTON) != 0) handled = true;
                break;
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private static int LowWord(IntPtr value) => unchecked((short)((long)value & 0xFFFF));
    private static int HighWord(IntPtr value) => unchecked((short)(((long)value >> 16) & 0xFFFF));
}
