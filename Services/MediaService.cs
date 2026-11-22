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
    public bool Shuffle { get; private set; }
    public Windows.Media.MediaPlaybackAutoRepeatMode Repeat { get; private set; }
    public bool CanShuffle { get; private set; }
    public bool CanRepeat { get; private set; }
    public bool CanSeek { get; private set; }
    public string SourceApp { get; private set; } = "";

    public async Task InitAsync()
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.CurrentSessionChanged += (_, _) => Hook();
            Hook();
        }
        catch {  }
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
                SourceApp = FriendlySource(s.SourceAppUserModelId ?? "");
                var info = s.GetPlaybackInfo();
                Playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                Shuffle = info.IsShuffleActive ?? false;
                Repeat = info.AutoRepeatMode ?? Windows.Media.MediaPlaybackAutoRepeatMode.None;
                CanShuffle = info.Controls.IsShuffleEnabled;
                CanRepeat = info.Controls.IsRepeatEnabled;
                CanSeek = info.Controls.IsPlaybackPositionEnabled;
                Art = await ImageUtil.LoadAsync(props.Thumbnail);
            }
        }
        catch { }
        Changed?.Invoke();
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

    private static string FriendlySource(string id)
    {
        string s = id.ToLowerInvariant();
        if (s.Contains("spotify")) return "Spotify";
        if (s.Contains("chrome")) return "Chrome";
        if (s.Contains("brave")) return "Brave";
        if (s.Contains("msedge")) return "Edge";
        if (s.Contains("firefox")) return "Firefox";
        if (s.Contains("zunemusic") || s.Contains("media.player")) return "Media Player";
        if (s.Contains("vlc")) return "VLC";
        if (id.Length == 0) return "Music";
        var name = id.Contains('!') ? id[(id.LastIndexOf('!') + 1)..] : System.IO.Path.GetFileNameWithoutExtension(id);
        return name.Length > 0 ? char.ToUpper(name[0]) + name[1..] : "Music";
    }

    public void ToggleShuffle() => _ = _session?.TryChangeShuffleActiveAsync(!Shuffle);

    public void CycleRepeat()
    {
        var next = Repeat switch
        {
            Windows.Media.MediaPlaybackAutoRepeatMode.None => Windows.Media.MediaPlaybackAutoRepeatMode.List,
            Windows.Media.MediaPlaybackAutoRepeatMode.List => Windows.Media.MediaPlaybackAutoRepeatMode.Track,
            _ => Windows.Media.MediaPlaybackAutoRepeatMode.None,
        };
        _ = _session?.TryChangeAutoRepeatModeAsync(next);
    }

    public void Seek(TimeSpan position)
    {
        try
        {
            var tl = _session?.GetTimelineProperties();
            if (tl == null) return;
            _ = _session!.TryChangePlaybackPositionAsync((tl.StartTime + position).Ticks);
        }
        catch { }
    }

    public void PlayPause() => _ = _session?.TryTogglePlayPauseAsync();
    public void Next() => _ = _session?.TrySkipNextAsync();
    public void Previous() => _ = _session?.TrySkipPreviousAsync();
}
