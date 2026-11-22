using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NotchWin.Services;

public sealed class DockApp
{
    public required string Key { get; init; }
    public string Name { get; set; } = "";
    public string? ShortcutPath { get; set; }
    public ImageSource? Icon { get; set; }
    public List<IntPtr> Windows { get; } = new();
    public HashSet<string> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Matches(string? exeKey) => exeKey != null && (Key == exeKey || Aliases.Contains(exeKey));
    public bool Pinned => ShortcutPath != null;
    public bool Running => Windows.Count > 0;
}

public static class WindowTracker
{
    private static readonly string PinnedDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private sealed record Pin(string Lnk, string Target, string IconPath, int IconIndex, string AltExe, DateTime Created);
    private static List<Pin>? _pinnedCache;
    private static DateTime _pinnedReadAt;

    public static List<DockApp> GetApps()
    {
        var apps = new List<DockApp>();
        var byKey = new Dictionary<string, DockApp>(StringComparer.OrdinalIgnoreCase);

        var byExeName = new Dictionary<string, DockApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var pin in GetPinned())
        {
            string exePath = pin.Target.Length > 0 ? pin.Target
                           : pin.IconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? pin.IconPath : pin.Lnk;
            string key = exePath.ToLowerInvariant();
            if (byKey.ContainsKey(key)) continue;
            var app = new DockApp
            {
                Key = key,
                Name = Path.GetFileNameWithoutExtension(pin.Lnk),
                ShortcutPath = pin.Lnk,
                Icon = (pin.IconPath.Length > 0 ? GetIcon(pin.IconPath, pin.IconIndex) : null)
                       ?? GetIcon(File.Exists(exePath) ? exePath : pin.Lnk),
            };
            byKey[key] = app;
            apps.Add(app);
            byExeName.TryAdd(Path.GetFileName(exePath), app);
            if (pin.AltExe.Length > 0) byExeName.TryAdd(pin.AltExe, app);
        }

        foreach (var (hwnd, exe) in GetTaskbarWindows())
        {
            string key = exe.ToLowerInvariant();
            if (!byKey.TryGetValue(key, out var app) && byExeName.TryGetValue(Path.GetFileName(exe), out var pinned))
            {
                app = pinned;
                byKey[key] = app;
                app.Aliases.Add(key);
            }
            if (app == null)
            {
                app = new DockApp { Key = key, Name = FriendlyName(exe), Icon = GetIcon(exe) ?? WindowIcon(hwnd) };
                byKey[key] = app;
                apps.Add(app);
            }
            app.Windows.Add(hwnd);
        }
        return apps;
    }

    public static string? ForegroundKey()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return null;
        var exe = ExeOfWindow(fg);
        return exe?.ToLowerInvariant();
    }

public static void Click(DockApp app)
    {
        if (!app.Running)
        {
            if (app.ShortcutPath != null)
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(app.ShortcutPath) { UseShellExecute = true }); } catch { }
            return;
        }

        var fg = GetForegroundWindow();
        int idx = app.Windows.IndexOf(fg);
        if (idx >= 0)
        {
            if (app.Windows.Count == 1) { ShowWindow(fg, SW_MINIMIZE); return; }
            Activate(app.Windows[^1]);
            return;
        }
        Activate(app.Windows[0]);
    }

    public static void Activate(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

private static List<(IntPtr hwnd, string exe)> GetTaskbarWindows()
    {
        var result = new List<(IntPtr, string)>();
        uint ownPid = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsTaskbarWindow(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == ownPid) return true;
            var exe = ExeOfWindow(h);
            if (exe != null) result.Add((h, exe));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static bool IsTaskbarWindow(IntPtr h)
    {
        if (!IsWindowVisible(h)) return false;
        long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        bool appWindow = (ex & WS_EX_APPWINDOW) != 0;
        if ((ex & WS_EX_TOOLWINDOW) != 0 && !appWindow) return false;
        if (GetWindow(h, GW_OWNER) != IntPtr.Zero && !appWindow) return false;
        if (GetWindowTextLength(h) == 0) return false;
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;

        var cls = ClassName(h);
        return cls is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow");
    }

    private static string? ExeOfWindow(IntPtr h)
    {
        GetWindowThreadProcessId(h, out uint pid);
        var exe = ExeOfPid(pid);
        if (exe != null && exe.EndsWith(@"\ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            string? inner = null;
            EnumChildWindows(h, (c, _) =>
            {
                GetWindowThreadProcessId(c, out uint cpid);
                if (cpid != pid) { inner = ExeOfPid(cpid); return false; }
                return true;
            }, IntPtr.Zero);
            if (inner != null) return inner;
        }
        return exe;
    }

    private static string? ExeOfPid(uint pid)
    {
        var hp = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hp == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(hp, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(hp); }
    }

    private static List<Pin> GetPinned()
    {
        if (_pinnedCache != null && DateTime.Now - _pinnedReadAt < TimeSpan.FromSeconds(30)) return _pinnedCache;
        var list = new List<Pin>();
        try
        {
            if (Directory.Exists(PinnedDir))
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                dynamic? shell = shellType != null ? Activator.CreateInstance(shellType) : null;
                foreach (var lnk in Directory.GetFiles(PinnedDir, "*.lnk"))
                {
                    string target = "", iconLoc = "", args = "";
                    try
                    {
                        var sc = shell?.CreateShortcut(lnk);
                        target = (string)(sc?.TargetPath ?? "");
                        iconLoc = (string)(sc?.IconLocation ?? "");
                        args = (string)(sc?.Arguments ?? "");
                    }
                    catch { }
                    target = Environment.ExpandEnvironmentVariables(target);

                    string iconPath = iconLoc;
                    int iconIndex = 0;
                    int comma = iconLoc.LastIndexOf(',');
                    if (comma > 0 && int.TryParse(iconLoc[(comma + 1)..], out var idx)) { iconPath = iconLoc[..comma]; iconIndex = idx; }
                    iconPath = Environment.ExpandEnvironmentVariables(iconPath.Trim());

                    var m = System.Text.RegularExpressions.Regex.Match(args, @"--processStart\s+""?([^""\s]+\.exe)");
                    string altExe = m.Success ? m.Groups[1].Value : "";

                    list.Add(new Pin(lnk, target, iconPath, iconIndex, altExe, File.GetCreationTime(lnk)));
                }
            }
        }
        catch { }
        _pinnedCache = list.OrderBy(p => p.Created).ToList();
        _pinnedReadAt = DateTime.Now;
        return _pinnedCache;
    }

    private static string FriendlyName(string exe)
    {
        try
        {
            var desc = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileDescription;
            if (!string.IsNullOrWhiteSpace(desc)) return desc.Trim();
        }
        catch { }
        return Path.GetFileNameWithoutExtension(exe);
    }

public static ImageSource? GetIcon(string path, int index = 0)
    {
        string cacheKey = path + "," + index;
        if (IconCache.TryGetValue(cacheKey, out var cached)) return cached;
        ImageSource? img = null;
        try
        {
            if (File.Exists(path) && SHDefExtractIcon(path, index, 0, out var large, out var small, (48 & 0xFFFF) | (16 << 16)) == 0 && large != IntPtr.Zero)
            {
                img = FromHIcon(large);
                DestroyIcon(large);
                if (small != IntPtr.Zero) DestroyIcon(small);
            }
            if (img == null)
            {
                var info = new SHFILEINFO();
                if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON) != IntPtr.Zero && info.hIcon != IntPtr.Zero)
                {
                    img = FromHIcon(info.hIcon);
                    DestroyIcon(info.hIcon);
                }
            }
        }
        catch { }
        IconCache[cacheKey] = img;
        return img;
    }

    private static ImageSource? WindowIcon(IntPtr hwnd)
    {
        var h = SendMessage(hwnd, WM_GETICON, (IntPtr)ICON_BIG, IntPtr.Zero);
        if (h == IntPtr.Zero) h = GetClassLongPtr(hwnd, GCLP_HICON);
        return h == IntPtr.Zero ? null : FromHIcon(h);
    }

    private static ImageSource? FromHIcon(IntPtr h)
    {
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
    }

    private static string ClassName(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int SW_RESTORE = 9, SW_MINIMIZE = 6;
    private const int WM_GETICON = 0x7F, ICON_BIG = 1, GCLP_HICON = -14;
    private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0;

    private delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] private static extern IntPtr GetClassLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr hp, int flags, StringBuilder sb, ref int size);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHDefExtractIcon(string file, int index, uint flags, out IntPtr large, out IntPtr small, int size);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);
}
