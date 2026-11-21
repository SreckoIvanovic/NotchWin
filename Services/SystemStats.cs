using System.Runtime.InteropServices;

namespace NotchWin.Services;

public sealed class SystemStats
{
    private ulong _lastIdle, _lastTotal;

    public int CpuPercent { get; private set; }
    public int RamPercent { get; private set; }
    public int BatteryPercent { get; private set; } = -1;
    public bool Charging { get; private set; }

    public void Update()
    {
        if (NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            ulong i = idle.Value, total = kernel.Value + user.Value;
            ulong dIdle = i - _lastIdle, dTotal = total - _lastTotal;
            if (_lastTotal != 0 && dTotal > 0)
                CpuPercent = (int)Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
            _lastIdle = i;
            _lastTotal = total;
        }

        var mem = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        if (NativeMethods.GlobalMemoryStatusEx(ref mem)) RamPercent = (int)mem.dwMemoryLoad;

        if (NativeMethods.GetSystemPowerStatus(out var p))
        {
            BatteryPercent = p.BatteryLifePercent == 255 ? -1 : p.BatteryLifePercent;
            Charging = p.ACLineStatus == 1;
        }
    }
}
