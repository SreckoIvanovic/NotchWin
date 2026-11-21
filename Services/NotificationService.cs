using System.Windows.Media;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace NotchWin.Services;

public sealed record NotifItem(uint Id, string App, string Title, string Body, DateTime Time, ImageSource? Icon)
{
    public string Header => App.Length > 0 ? $"{App} · {Time:HH:mm}" : Time.ToString("HH:mm");
    public bool HasBody => Body.Length > 0;
}

public sealed class NotificationService
{
    private UserNotificationListener? _listener;
    private readonly Dictionary<uint, NotifItem> _cache = new();
    private bool _first = true;
    private bool _busy;

    public bool Available { get; private set; }

    public async Task<bool> InitAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var status = await _listener.RequestAccessAsync();
            Available = status == UserNotificationListenerAccessStatus.Allowed;
        }
        catch { Available = false; }
        return Available;
    }

    public async Task<(List<NotifItem> all, List<NotifItem> fresh)?> PollAsync()
    {
        if (!Available || _listener == null || _busy) return null;
        _busy = true;
        try
        {
            var list = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var all = new List<NotifItem>();
            var fresh = new List<NotifItem>();
            var ids = new HashSet<uint>();

            foreach (var n in list)
            {
                ids.Add(n.Id);
                if (!_cache.TryGetValue(n.Id, out var item))
                {
                    item = await ParseAsync(n);
                    _cache[n.Id] = item;
                    if (!_first) fresh.Add(item);
                }
                all.Add(item);
            }

            foreach (var gone in _cache.Keys.Where(k => !ids.Contains(k)).ToList()) _cache.Remove(gone);
            _first = false;
            return (all.OrderByDescending(x => x.Time).ToList(), fresh);
        }
        catch { return null; }
        finally { _busy = false; }
    }

    public void Remove(uint id)
    {
        try { _listener?.RemoveNotification(id); _cache.Remove(id); } catch { }
    }

    public void ClearAll()
    {
        try { _listener?.ClearNotifications(); _cache.Clear(); } catch { }
    }

    private static async Task<NotifItem> ParseAsync(UserNotification n)
    {
        string app = "";
        ImageSource? icon = null;
        try { app = n.AppInfo?.DisplayInfo?.DisplayName ?? ""; } catch { }
        try { icon = await ImageUtil.LoadAsync(n.AppInfo?.DisplayInfo?.GetLogo(new Windows.Foundation.Size(64, 64))); } catch { }

        var texts = new List<string>();
        try
        {
            var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            if (binding != null)
                texts = binding.GetTextElements().Select(t => t.Text?.Trim() ?? "").Where(t => t.Length > 0).ToList();
        }
        catch { }

        string title = texts.FirstOrDefault() ?? (app.Length > 0 ? app : "Notification");
        string body = string.Join(" — ", texts.Skip(1));
        return new NotifItem(n.Id, app, title, body, n.CreationTime.LocalDateTime, icon);
    }
}
