using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace NotchWin.Services;

public sealed class Settings
{
    public bool AutoStart { get; set; } = true;

    public bool AutoHide { get; set; } = true;

    public bool DockEnabled { get; set; } = true;

    public string City { get; set; } = "";

    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NotchWin");
    public static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        var s = new Settings();
        s.Save();
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "NotchWin";

    public static void Apply(bool enabled)
    {
        var exe = Environment.ProcessPath;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (enabled && exe != null) key.SetValue(Name, $"\"{exe}\"");
            else key.DeleteValue(Name, false);
            Log.Write($"Run entry {(enabled ? "set to " + exe : "removed")}; now: {key.GetValue(Name)}");
        }
        catch (Exception ex) { Log.Write("Run entry failed: " + ex.Message); }

        Task.Run(() =>
        {
            try
            {
                if (enabled && exe != null) CreateLogonTask(exe);
                else RunSchtasks($"/Delete /TN \"{Name}\" /F");
            }
            catch { }
        });
    }

    private static void CreateLogonTask(string exe)
    {
        string user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts NotchWin when you sign in.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{Esc(user)}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{Esc(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>"{Esc(exe)}"</Command></Exec></Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), "NotchWin", "logon-task.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
        RunSchtasks($"/Create /XML \"{file}\" /TN \"{Name}\" /F");
        Log.Write("Logon task registered");
    }

    private static string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? s;

    private static void RunSchtasks(string args)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        p?.WaitForExit(15000);
    }
}

public static class Log
{
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NotchWin", "log.txt");
    private static readonly object Gate = new();

    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 200_000) File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
