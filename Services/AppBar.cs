using System.Runtime.InteropServices;

namespace NotchWin.Services;

public sealed class AppBar
{
    private readonly IntPtr _hwnd;
    private readonly uint _callbackMsg;
    private bool _registered;

    public static AppBar? Current { get; private set; }

    public event Action<bool>? FullscreenChanged;
    public event Action? PositionChanged;

    public uint CallbackMessage => _callbackMsg;

    public AppBar(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _callbackMsg = RegisterWindowMessage("NotchWin.Dock.AppBar");
        Current = this;
    }

    public RECT Reserve(int heightPx)
    {
        var abd = NewData();
        if (!_registered)
        {
            abd.uCallbackMessage = _callbackMsg;
            SHAppBarMessage(ABM_NEW, ref abd);
            _registered = true;
        }

        int w = GetSystemMetrics(SM_CXSCREEN), h = GetSystemMetrics(SM_CYSCREEN);
        abd.uEdge = ABE_BOTTOM;
        abd.rc = new RECT { Left = 0, Right = w, Top = h - heightPx, Bottom = h };
        SHAppBarMessage(ABM_QUERYPOS, ref abd);
        abd.rc.Top = abd.rc.Bottom - heightPx;
        SHAppBarMessage(ABM_SETPOS, ref abd);
        return abd.rc;
    }

    public void Remove()
    {
        if (!_registered) return;
        var abd = NewData();
        SHAppBarMessage(ABM_REMOVE, ref abd);
        _registered = false;
    }

    public void HandleCallback(IntPtr wParam, IntPtr lParam)
    {
        switch (wParam.ToInt32())
        {
            case ABN_POSCHANGED: PositionChanged?.Invoke(); break;
            case ABN_FULLSCREENAPP: FullscreenChanged?.Invoke(lParam != IntPtr.Zero); break;
        }
    }

    private APPBARDATA NewData() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };

    private const int ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3;
    private const int ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;
    private const uint ABE_BOTTOM = 3;
    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")] private static extern IntPtr SHAppBarMessage(int msg, ref APPBARDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
