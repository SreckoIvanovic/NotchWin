using System.Management;

namespace NotchWin.Services;

public sealed class BrightnessService
{
    private readonly object _gate = new();
    private int? _pending;
    private bool _writing;

    public bool Supported { get; }

    public BrightnessService()
    {
        Supported = Get() >= 0;
    }

    public int Get()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject o in s.Get())
                using (o) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        return -1;
    }

    public void Set(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        lock (_gate)
        {
            _pending = percent;
            if (_writing) return;
            _writing = true;
        }
        Task.Run(WriteLoop);
    }

    private void WriteLoop()
    {
        while (true)
        {
            int value;
            lock (_gate)
            {
                if (_pending is not { } p) { _writing = false; return; }
                value = p;
                _pending = null;
            }
            try
            {
                using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
                foreach (ManagementObject o in s.Get())
                    using (o) o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)value });
            }
            catch { }
        }
    }
}
