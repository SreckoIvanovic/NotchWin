using NAudio.CoreAudioApi;

namespace NotchWin.Services;

public sealed class AudioService : IDisposable
{
    private readonly MMDeviceEnumerator _enum = new();

    public float GetVolume()
    {
        try
        {
            using var d = _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return d.AudioEndpointVolume.MasterVolumeLevelScalar;
        }
        catch { return 0; }
    }

    public void SetVolume(float v)
    {
        try
        {
            using var d = _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            d.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(v, 0f, 1f);
        }
        catch { }
    }

    public bool IsMicMuted()
    {
        try
        {
            foreach (var d in _enum.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (d) return d.AudioEndpointVolume.Mute;
            }
        }
        catch { }
        return false;
    }

    public bool ToggleMic()
    {
        bool newState = !IsMicMuted();
        try
        {
            foreach (var d in _enum.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (d) d.AudioEndpointVolume.Mute = newState;
            }
        }
        catch { }
        return newState;
    }

    public void Dispose() => _enum.Dispose();
}
