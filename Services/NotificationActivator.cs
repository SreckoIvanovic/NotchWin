using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace NotchWin.Services;

public static class NotificationActivator
{
    private static readonly string DbDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Notifications");
    private static readonly string CopyDir = Path.Combine(Path.GetTempPath(), "NotchWin", "wpn");

    static NotificationActivator()
    {
        try { SQLitePCL.Batteries_V2.Init(); } catch { }
    }

    public static bool Activate(uint notificationId)
    {
        var info = ReadToast(notificationId);
        if (info is not { } t) return false;

        AllowSetForegroundWindow(-1);

        try
        {
            if (t.ActivationType == "protocol" && t.Launch.Length > 0)
            {
                Process.Start(new ProcessStartInfo(t.Launch) { UseShellExecute = true });
                return true;
            }

            if (TryComActivator(t.Aumid, t.Launch)) return true;

            if (t.Aumid.Contains('!') && TryActivateStoreApp(t.Aumid, t.Launch)) return true;

            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{t.Aumid}") { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    private record ToastInfo(string Aumid, string Launch, string ActivationType);

    private static ToastInfo? ReadToast(uint id)
    {
        try
        {
            Directory.CreateDirectory(CopyDir);
            foreach (var ext in new[] { "", "-wal", "-shm" })
            {
                var src = Path.Combine(DbDir, "wpndatabase.db" + ext);
                var dst = Path.Combine(CopyDir, "wpndatabase.db" + ext);
                if (File.Exists(src)) File.Copy(src, dst, true);
                else if (File.Exists(dst)) File.Delete(dst);
            }

            using var con = new SqliteConnection($"Data Source={Path.Combine(CopyDir, "wpndatabase.db")};Pooling=False");
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT n.Payload, h.PrimaryId FROM Notification n " +
                              "JOIN NotificationHandler h ON h.RecordId = n.HandlerId WHERE n.Id = $id";
            cmd.Parameters.AddWithValue("$id", (long)id);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;

            string payload = r.GetValue(0) switch
            {
                byte[] b => Encoding.UTF8.GetString(b),
                string s => s,
                _ => "",
            };
            string aumid = r.GetString(1);

            var toast = XDocument.Parse(payload).Root;
            string launch = toast?.Attribute("launch")?.Value ?? "";
            string type = toast?.Attribute("activationType")?.Value ?? "foreground";
            return new ToastInfo(aumid, launch, type);
        }
        catch { return null; }
    }

[ComImport, Guid("53E31837-6600-4A81-9395-75CFFE746F94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INotificationActivationCallback
    {
        void Activate([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
                      [MarshalAs(UnmanagedType.LPWStr)] string invokedArgs,
                      IntPtr data, uint count);
    }

    private static bool TryComActivator(string aumid, string args)
    {
        try
        {
            string? clsid = null;
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var k = root.OpenSubKey($@"Software\Classes\AppUserModelId\{aumid}");
                clsid = k?.GetValue("CustomActivator") as string;
                if (clsid != null) break;
            }
            if (clsid == null) return false;

            var type = Type.GetTypeFromCLSID(new Guid(clsid));
            if (type == null || Activator.CreateInstance(type) is not INotificationActivationCallback cb) return false;
            cb.Activate(aumid, args, IntPtr.Zero, 0);
            Marshal.ReleaseComObject(cb);
            return true;
        }
        catch { return false; }
    }

[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
                                [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint processId);
    }

    private static bool TryActivateStoreApp(string aumid, string args)
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"));
            if (type == null || Activator.CreateInstance(type) is not IApplicationActivationManager mgr) return false;
            int hr = mgr.ActivateApplication(aumid, args.Length > 0 ? args : null, 0, out _);
            Marshal.ReleaseComObject(mgr);
            return hr >= 0;
        }
        catch { return false; }
    }

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
}
