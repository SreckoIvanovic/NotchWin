using System.IO;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace NotchWin.Services;

public sealed class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private GlobalSystemMediaTransportControlsSession? _session;

    public event Action? Changed;

    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public bool Playing { get; private set; }
    public bool HasSession => _session != null;
    public BitmapImage? Art { get; private set; }

    public async Task InitAsync()
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.CurrentSessionChanged += (_, _) => Hook();
            Hook();
        }
        catch { }
    }

    private void Hook()
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnChanged;
            _session.PlaybackInfoChanged -= OnChanged;
        }
        _session = _mgr?.GetCurrentSession();
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnChanged;
            _session.PlaybackInfoChanged += OnChanged;
        }
        _ = RefreshAsync();
    }

    private void OnChanged(GlobalSystemMediaTransportControlsSession s, object a) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        try
        {
            var s = _session;
            if (s == null)
            {
                Title = Artist = "";
                Playing = false;
                Art = null;
            }
            else
            {
                var props = await s.TryGetMediaPropertiesAsync();
                Title = props.Title ?? "";
                Artist = props.Artist ?? "";
                Playing = s.GetPlaybackInfo().PlaybackStatus ==
                          GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                Art = await LoadThumbAsync(props.Thumbnail);
            }
        }
        catch { }
        Changed?.Invoke();
    }

    private static async Task<BitmapImage?> LoadThumbAsync(Windows.Storage.Streams.IRandomAccessStreamReference? thumb)
    {
        if (thumb == null) return null;
        try
        {
            using var ras = await thumb.OpenReadAsync();
            using var stream = ras.AsStreamForRead();
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    public (TimeSpan pos, TimeSpan dur) GetTimeline()
    {
        try
        {
            var tl = _session?.GetTimelineProperties();
            if (tl == null) return (TimeSpan.Zero, TimeSpan.Zero);
            var dur = tl.EndTime - tl.StartTime;
            var pos = tl.Position - tl.StartTime;
            if (Playing) pos += DateTimeOffset.Now - tl.LastUpdatedTime;
            if (pos > dur) pos = dur;
            if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
            return (pos, dur);
        }
        catch { return (TimeSpan.Zero, TimeSpan.Zero); }
    }

    public void PlayPause() => _ = _session?.TryTogglePlayPauseAsync();
    public void Next() => _ = _session?.TrySkipNextAsync();
    public void Previous() => _ = _session?.TrySkipPreviousAsync();
}
