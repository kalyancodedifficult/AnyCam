using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AnyCam.Desktop.Controls;

/// <summary>
/// Hosts a native child window that the media engine renders the preview into with a DXGI swap chain.
/// Reports its size in physical pixels and mouse clicks (for tap-to-focus).
/// </summary>
public sealed class PreviewHost : HwndHost
{
    private const string ClassName = "AnyCamPreview";
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WmSize = 0x0005;
    private const int WmEraseBackground = 0x0014;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmLeftButtonDoubleClick = 0x0203;
    private const int WmMouseWheel = 0x020A;
    private const int CsDoubleClicks = 0x0008;

    private static readonly WndProcDelegate DefaultWindowProc = DefWindowProcW;
    private static bool _classRegistered;

    private IntPtr _hwnd;

    /// <summary>The native window exists (hwnd, width, height in pixels).</summary>
    public event Action<IntPtr, int, int>? SurfaceCreated;

    public event Action<int, int>? SurfaceResized;
    public event Action? SurfaceDestroyed;

    /// <summary>Left click at (x, y) in pixels.</summary>
    public event Action<double, double>? SurfaceClicked;

    /// <summary>Double click at (x, y) in pixels.</summary>
    public event Action<double, double>? SurfaceDoubleClicked;

    /// <summary>Mouse wheel: positive = away from the user.</summary>
    public event Action<int>? SurfaceWheel;

    public IntPtr SurfaceHandle => _hwnd;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var instance = GetModuleHandleW(null);
        if (!_classRegistered)
        {
            var windowClass = new WndClassEx
            {
                Size = (uint)Marshal.SizeOf<WndClassEx>(),
                Style = CsDoubleClicks,
                WindowProc = Marshal.GetFunctionPointerForDelegate(DefaultWindowProc),
                Instance = instance,
                Cursor = LoadCursorW(IntPtr.Zero, 32515), // IDC_CROSS: hints that clicking focuses
                ClassName = ClassName,
            };
            RegisterClassExW(ref windowClass);
            _classRegistered = true;
        }

        _hwnd = CreateWindowExW(0, ClassName, "", WsChild | WsVisible | WsClipChildren | WsClipSiblings,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, instance, IntPtr.Zero);
        GetClientRect(_hwnd, out var rect);
        SurfaceCreated?.Invoke(_hwnd, Math.Max(rect.Right - rect.Left, 1), Math.Max(rect.Bottom - rect.Top, 1));
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        SurfaceDestroyed?.Invoke();
        DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmSize:
                SurfaceResized?.Invoke(Math.Max((int)(lParam.ToInt64() & 0xFFFF), 1), Math.Max((int)((lParam.ToInt64() >> 16) & 0xFFFF), 1));
                break;
            case WmEraseBackground:
                handled = true; // the swap chain covers everything; avoids flicker
                return new IntPtr(1);
            case WmLeftButtonDown:
                SurfaceClicked?.Invoke((short)(lParam.ToInt64() & 0xFFFF), (short)((lParam.ToInt64() >> 16) & 0xFFFF));
                break;
            case WmLeftButtonDoubleClick:
                SurfaceDoubleClicked?.Invoke((short)(lParam.ToInt64() & 0xFFFF), (short)((lParam.ToInt64() >> 16) & 0xFFFF));
                break;
            case WmMouseWheel:
                SurfaceWheel?.Invoke((short)((wParam.ToInt64() >> 16) & 0xFFFF));
                handled = true;
                return IntPtr.Zero;
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public int Style;
        public IntPtr WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursorW(IntPtr instance, int cursor);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);
}
