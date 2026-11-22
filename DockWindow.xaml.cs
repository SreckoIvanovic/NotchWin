using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NotchWin.Services;

namespace NotchWin;

public partial class DockWindow : Window
{
    private const double CapsuleHeight = 62;
    private const double BottomGap = 8;
    private const double TopGap = 6;
    private const double ShadowMargin = 14;

    private readonly MediaService _media;
    private readonly AudioService _audio;
    private readonly SystemStats _stats;
    private readonly Action<int> _openNotchTab;
    private readonly Action _hideDock;

    private readonly DispatcherTimer _slow = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _fast = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Spring _activeX = new(0) { Stiffness = 340, Damping = 30 };
    private bool _animating;
    private TimeSpan _lastFrame;

    private IntPtr _hwnd;
    private AppBar? _appBar;
    private string _appsSignature = "";
    private List<DockApp> _apps = new();
    private string? _activeKey;
    private bool _fullscreen;

    public DockWindow(MediaService media, AudioService audio, SystemStats stats, Action<int> openNotchTab, Action hideDock)
    {
        _media = media;
        _audio = audio;
        _stats = stats;
        _openNotchTab = openNotchTab;
        _hideDock = hideDock;
        InitializeComponent();

        _media.Changed += () => Dispatcher.BeginInvoke(UpdateMedia);
        _slow.Tick += (_, _) => SlowTick();
        _fast.Tick += (_, _) => UpdateActive();
        SizeChanged += (_, _) => PlaceWindow();
        MediaPill.MouseEnter += (_, _) => MediaHover.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        MediaPill.MouseLeave += (_, _) => MediaHover.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)));

        Loaded += (_, _) =>
        {
            UpdateMedia();
            SlowTick();
            _slow.Start();
            _fast.Start();
        };
    }

protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));

        _appBar = new AppBar(_hwnd);
        _appBar.PositionChanged += () => Dispatcher.BeginInvoke(ReserveSpace);
        _appBar.FullscreenChanged += full => Dispatcher.BeginInvoke(() => SetFullscreen(full));
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(ReserveSpace);
        ReserveSpace();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_appBar != null && msg == _appBar.CallbackMessage)
        {
            _appBar.HandleCallback(wParam, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private double _stripBottomDip;

    private void ReserveSpace()
    {
        if (_appBar == null) return;
        var src = PresentationSource.FromVisual(this);
        double scale = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        int heightPx = (int)Math.Ceiling((CapsuleHeight + BottomGap + TopGap) * scale);
        var rc = _appBar.Reserve(heightPx);
        _stripBottomDip = rc.Bottom / scale;
        PlaceWindow();
    }

    private void PlaceWindow()
    {
        if (_stripBottomDip <= 0) _stripBottomDip = SystemParameters.PrimaryScreenHeight;
        Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
        Top = _stripBottomDip - BottomGap - CapsuleHeight - ShadowMargin;
    }

    private void SetFullscreen(bool full)
    {
        _fullscreen = full;
        if (full) Hide(); else Show();
    }

    public void Shutdown()
    {
        _appBar?.Remove();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _appBar?.Remove();
        base.OnClosed(e);
    }

private void SlowTick()
    {
        ClockText.Text = DateTime.Now.ToString("HH:mm");
        UpdateBattery();
        UpdateWifi();
        RefreshApps();
    }

    private void UpdateBattery()
    {
        _stats.UpdateBattery();
        int pct = _stats.BatteryPercent;
        BatBtn.Visibility = pct < 0 ? Visibility.Collapsed : Visibility.Visible;
        if (pct < 0) return;
        const double inner = 22 - 2 * 1.3 - 2 * 1.5;
        BatFill.Width = Math.Max(1.5, inner * pct / 100.0);
        BatFill.Background = _stats.Charging ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58))
            : pct <= 20 ? new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)) : Brushes.White;
        BatBtn.ToolTip = $"{pct}%" + (_stats.Charging ? " · charging" : "");
    }

    private void UpdateWifi()
    {
        var (kind, bars, name) = NetworkStatus.Get();
        WifiIcon.Data = Geometry.Parse(kind switch
        {
            NetKind.Ethernet => "M4,4 H20 V14 H4 Z M8,18 H16 M12,14 V18",
            NetKind.None => "M2,8.5 A15,15 0 0 1 22,8.5 M5,12 A10.5,10.5 0 0 1 19,12 M3,3 L21,21",
            _ => bars switch
            {
                <= 1 => "M12,19.5 L12.01,19.5 M8.5,15.5 A5.5,5.5 0 0 1 15.5,15.5",
                2 => "M5,12 A10.5,10.5 0 0 1 19,12 M8.5,15.5 A5.5,5.5 0 0 1 15.5,15.5 M12,19.5 L12.01,19.5",
                _ => "M2,8.5 A15,15 0 0 1 22,8.5 M5,12 A10.5,10.5 0 0 1 19,12 M8.5,15.5 A5.5,5.5 0 0 1 15.5,15.5 M12,19.5 L12.01,19.5",
            },
        });
        WifiIcon.Opacity = kind == NetKind.None ? 0.5 : 1;
        WifiBtn.ToolTip = name;

        int vol = (int)Math.Round(_audio.GetVolume() * 100);
        VolBtn.ToolTip = $"Volume {vol}%";
        VolIcon.Data = Geometry.Parse(vol == 0
            ? "M11,5 L6,9 H2 V15 H6 L11,19 Z M16,9 L22,15 M22,9 L16,15"
            : vol < 40
                ? "M11,5 L6,9 H2 V15 H6 L11,19 Z M15.54,8.46 A5,5 0 0 1 15.54,15.54"
                : "M11,5 L6,9 H2 V15 H6 L11,19 Z M15.54,8.46 A5,5 0 0 1 15.54,15.54 M19.07,4.93 A10,10 0 0 1 19.07,19.07");
    }

private void RefreshApps()
    {
        var apps = WindowTracker.GetApps();
        string sig = string.Join("|", apps.Select(a => a.Key + (a.Running ? "*" : "")));
        if (sig != _appsSignature)
        {
            _appsSignature = sig;
            _apps = apps;
            AppsList.ItemsSource = _apps;
            Dispatcher.BeginInvoke(UpdateActive, DispatcherPriority.Loaded);
        }
        else
        {
            for (int i = 0; i < apps.Count; i++)
            {
                _apps[i].Windows.Clear();
                _apps[i].Windows.AddRange(apps[i].Windows);
            }
        }
    }

    private void UpdateActive()
    {
        var key = WindowTracker.ForegroundKey();
        if (key == null || key.EndsWith(@"\notchwin.exe")) key = _activeKey;

        var app = _apps.FirstOrDefault(a => a.Matches(key) && a.Running);
        _activeKey = key;
        if (app == null)
        {
            ActiveIndicator.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)));
            return;
        }
        if (AppsList.ItemContainerGenerator.ContainerFromItem(app) is not FrameworkElement c || !c.IsLoaded) return;

        double x = c.TranslatePoint(new Point(0, 0), AppsHost).X + (c.ActualWidth - ActiveIndicator.Width) / 2;
        if (ActiveIndicator.Opacity < 0.05) { _activeX.Value = x; _activeX.Velocity = 0; ActiveX.X = x; }
        _activeX.Target = x;
        if (ActiveIndicator.Opacity < 0.99)
            ActiveIndicator.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
        StartAnimating();
    }

    private void StartAnimating()
    {
        if (_animating) return;
        _animating = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return;
        double dt = _lastFrame == TimeSpan.Zero ? 1.0 / 60 : Math.Clamp((now - _lastFrame).TotalSeconds, 0, 1.0 / 30);
        _lastFrame = now;
        _activeX.Step(dt);
        ActiveX.X = _activeX.Value;
        if (_activeX.Settled)
        {
            _activeX.Snap();
            ActiveX.X = _activeX.Value;
            CompositionTarget.Rendering -= OnFrame;
            _animating = false;
        }
    }

    private void App_Click(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is DockApp app)
        {
            Bounce((FrameworkElement)sender);
            WindowTracker.Click(app);
            Dispatcher.BeginInvoke(UpdateActive, DispatcherPriority.Background);
        }
    }

    private static ScaleTransform? IconScale(object sender) =>
        sender is Grid g && g.Children.Count > 0 && g.Children[0] is Image img ? img.RenderTransform as ScaleTransform : null;

    private void App_Enter(object sender, MouseEventArgs e) => ScaleIcon(sender, 1.15);
    private void App_Leave(object sender, MouseEventArgs e) => ScaleIcon(sender, 1.0);

    private static void ScaleIcon(object sender, double to)
    {
        if (IconScale(sender) is not { } st) return;
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(260)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    private static void Bounce(FrameworkElement el)
    {
        if (IconScale(el) is not { } st) return;
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(360) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0.85, KeyTime.FromPercent(0.3)));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1.15, KeyTime.FromPercent(1.0), new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 }));
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

private void UpdateMedia()
    {
        bool has = _media.HasSession && _media.Title.Length > 0;
        MediaApp.Text = has ? _media.SourceApp : "Music";
        MediaSub.Text = has
            ? (_media.Artist.Length > 0 ? $"{_media.Artist} - {_media.Title}" : _media.Title)
            : "Nothing playing";
        MediaArtBrush.ImageSource = _media.Art;
        MediaArt.Visibility = has && _media.Art != null ? Visibility.Visible : Visibility.Collapsed;
        MediaHoverIcon.Data = Geometry.Parse(_media.Playing
            ? "M6.5,4.5 H10 V19.5 H6.5 Z M14,4.5 H17.5 V19.5 H14 Z"
            : "M7,4.5 L19.5,12 L7,19.5 Z");
    }

    private void MediaPill_Click(object sender, MouseButtonEventArgs e) => _media.PlayPause();

private void Clock_Click(object sender, MouseButtonEventArgs e) => _openNotchTab(3);
    private void Battery_Click(object sender, MouseButtonEventArgs e) => _openNotchTab(2);
    private void Volume_Click(object sender, MouseButtonEventArgs e) => _openNotchTab(2);

    private void Volume_Wheel(object sender, MouseWheelEventArgs e)
    {
        float v = Math.Clamp(_audio.GetVolume() + (e.Delta > 0 ? 0.02f : -0.02f), 0f, 1f);
        _audio.SetVolume(v);
        UpdateWifi();
        e.Handled = true;
    }

    private void Wifi_Click(object sender, MouseButtonEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-actioncenter:controlcenter/&showFooter=true") { UseShellExecute = true }); }
        catch
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:network-wifi") { UseShellExecute = true }); } catch { }
        }
    }

    private void HideDock_Click(object sender, RoutedEventArgs e) => _hideDock();

    private void TaskbarSettings_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true }); } catch { }
    }
}
