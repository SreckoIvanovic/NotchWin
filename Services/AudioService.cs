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

    public void Dispose() => _enum.Dispose();
}
