using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using NotchWin.Services;

namespace NotchWin;

public record ShelfItem(string Name, string Path);

public partial class MainWindow : Window
{
    private enum Mode { Collapsed, Peek, Expanded }

    private static readonly Size CollapsedSize = new(260, 34);
    private static readonly Size PeekSize = new(420, 74);
    private static readonly Size ExpandedSize = new(540, 214);
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    private readonly MediaService _media = new();
    private readonly AudioService _audio = new();
    private readonly SystemStats _stats = new();
    private readonly NotificationService _notifs = new();
    private readonly WeatherService _weather = new();
    private readonly Settings _settings = Settings.Load();
    private readonly ObservableCollection<ShelfItem> _shelf = new();

    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _peekTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _notifTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _weatherTimer = new() { Interval = TimeSpan.FromMinutes(15) };

    private readonly Spring _w = new(CollapsedSize.Width);
    private readonly Spring _h = new(CollapsedSize.Height);
    private bool _animating;
    private TimeSpan _lastFrame;

    private FrameworkElement[] _panels = Array.Empty<FrameworkElement>();
    private Button[] _tabBtns = Array.Empty<Button>();

    private Mode _mode = Mode.Collapsed;
    private bool _dragging;
    private int _tab;
    private int _unseen;
    private DateTime _calBuiltFor = DateTime.MinValue;

    private Point _dragStart;
    private ShelfItem? _dragItem;

    public MainWindow()
    {
        InitializeComponent();

        _panels = new FrameworkElement[] { Panel0, Panel1, Panel2, Panel3, Panel4, Panel5 };
        _tabBtns = new[] { TabBtn0, TabBtn1, TabBtn2, TabBtn3, TabBtn4, TabBtn5 };
        ShelfList.ItemsSource = _shelf;
        _shelf.CollectionChanged += (_, _) =>
            ShelfHint.Visibility = _shelf.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        AutoStartItem.IsChecked = _settings.AutoStart;
        AutoHideItem.IsChecked = _settings.AutoHide;
        AutoStart.Apply(_settings.AutoStart);

        PlaceWindow();
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(PlaceWindow);
        SelectTab(0);
        ApplyShape();

        _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); if (!_dragging) GoTo(Mode.Collapsed); };
        _peekTimer.Tick += (_, _) => { _peekTimer.Stop(); if (_mode == Mode.Peek && !Notch.IsMouseOver) GoTo(Mode.Collapsed); };
        _tick.Tick += (_, _) => OnTick();
        _notifTimer.Tick += async (_, _) => await PollNotificationsAsync();
        _weatherTimer.Tick += async (_, _) => await RefreshWeatherAsync();

        Notch.MouseEnter += (_, _) =>
        {
            _collapseTimer.Stop();
            if (_mode == Mode.Peek)
            {
                _peekTimer.Stop();
                _peekExpandTimer.Start();
                return;
            }
            GoTo(Mode.Expanded);
        };
        Notch.MouseLeave += (_, _) =>
        {
            _peekExpandTimer.Stop();
            if (_dragging) return;
            if (_mode == Mode.Peek) { _peekTimer.Interval = TimeSpan.FromSeconds(1.5); _peekTimer.Start(); }
            else _collapseTimer.Start();
        };
        _peekExpandTimer.Tick += (_, _) =>
        {
            _peekExpandTimer.Stop();
            if (_mode != Mode.Peek) return;
            SelectTab(NotifTab);
            GoTo(Mode.Expanded);
        };
        PeekView.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _peekExpandTimer.Stop();
            if (_peekItem != null) OpenNotification(_peekItem);
        };
        DragEnter += OnDragEnterFiles;
        DragOver += (_, e) => { e.Effects = DragDropEffects.Copy; e.Handled = true; _collapseTimer.Stop(); };
        DragLeave += (_, _) => { if (!_dragging) _collapseTimer.Start(); };
        Drop += OnDropFiles;

        _media.Changed += () => Dispatcher.BeginInvoke(UpdateMedia);
        VolPill.SizeChanged += (_, _) => SetVolumeUi(_audio.GetVolume() * 100);

        Loaded += async (_, _) =>
        {
            OnTick();
            _tick.Start();
            await _media.InitAsync();
            if (await _notifs.InitAsync())
            {
                await PollNotificationsAsync();
                _notifTimer.Start();
            }
            await RefreshWeatherAsync();
            _weatherTimer.Start();
        };
    }

protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));

        SetClickThrough(true);
        _hoverPoll.Tick += (_, _) => PollHover();
        _hoverPoll.Start();
        _fgPoll.Tick += (_, _) => PollForeground();
        _fgPoll.Start();

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        bool ok = NativeMethods.RegisterHotKey(_hwnd, HideHotkeyId,
            NativeMethods.MOD_WIN | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, 0x4E );
        Log.Write(ok ? "Hotkey Win+Alt+N registered" : "Hotkey Win+Alt+N is taken by another app");
    }

    private const int HideHotkeyId = 1;

    private enum FgState { Normal, Maximized, Fullscreen }
    private bool _hidden;
    private bool _forceShow;
    private bool _shown = true;
    private FgState _fg = FgState.Normal;
    private IntPtr _lastFg;
    private readonly DispatcherTimer _fgPoll = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HideHotkeyId)
        {
            OnHideHotkey();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OnHideHotkey()
    {
        if (_shown)
        {
            _hidden = true;
            _forceShow = false;
            _collapseTimer.Stop();
            _peekTimer.Stop();
            GoTo(Mode.Collapsed);
        }
        else
        {
            _hidden = false;
            if (_fg != FgState.Normal && _settings.AutoHide) _forceShow = true;
        }
        ApplyVisibility();
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => OnHideHotkey();

    private bool AutoHidden => _settings.AutoHide && _fg != FgState.Normal && !_forceShow;

    private bool ShouldShow => !_hidden && (!AutoHidden || _mode != Mode.Collapsed);

    private void ApplyVisibility()
    {
        bool show = ShouldShow;
        if (show == _shown) return;
        _shown = show;
        var root = (UIElement)Content;
        if (show)
        {
            Show();
            SetClickThrough(_mode == Mode.Collapsed);
            root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
        }
        else
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
            fade.Completed += (_, _) => { if (!_shown) Hide(); };
            root.BeginAnimation(OpacityProperty, fade);
        }
    }

    private void PollForeground()
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _hwnd) return;
        NativeMethods.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == Environment.ProcessId) return;

        var sb = new System.Text.StringBuilder(128);
        NativeMethods.GetClassName(fg, sb, sb.Capacity);
        string cls = sb.ToString();

        if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow"
            or "XamlExplorerHostIslandWindow" or "TopLevelWindowForOverflowXamlIsland" or "NotifyIconOverflowWindow")
            return;

        if (fg != _lastFg) { _lastFg = fg; _forceShow = false; }

        FgState state = FgState.Normal;
        if (cls is not ("Progman" or "WorkerW") &&
            NativeMethods.MonitorFromWindow(fg, NativeMethods.MONITOR_DEFAULTTONEAREST) ==
            NativeMethods.MonitorFromWindow(_hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST))
        {
            if (IsFullscreen(fg)) state = FgState.Fullscreen;
            else if (NativeMethods.IsZoomed(fg)) state = FgState.Maximized;
        }
        _fg = state;
        ApplyVisibility();
    }

    private static bool IsFullscreen(IntPtr hwnd)
    {
        var mi = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST), ref mi)) return false;
        if (!NativeMethods.GetWindowRect(hwnd, out var r)) return false;
        bool covers = r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
                      r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
        long style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        bool hasCaption = (style & NativeMethods.WS_CAPTION) == NativeMethods.WS_CAPTION;
        return covers && !hasCaption;
    }

    private void AutoHide_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoHide = AutoHideItem.IsChecked;
        _settings.Save();
        ApplyVisibility();
    }

    private IntPtr _hwnd;
    private readonly DispatcherTimer _hoverPoll = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private DateTime? _hoverSince;
    private static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan RevealDelay = TimeSpan.FromMilliseconds(300);

    private void SetClickThrough(bool on)
    {
        if (_hwnd == IntPtr.Zero) return;
        var ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex = on ? ex | NativeMethods.WS_EX_TRANSPARENT : ex & ~(long)NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
    }

    private void PollHover()
    {
        if (_hidden || !NativeMethods.GetCursorPos(out var cp)) { _hoverSince = null; return; }
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget == null) return;
        var p = src.CompositionTarget.TransformFromDevice.Transform(new Point(cp.X, cp.Y));

        if (_mode == Mode.Expanded)
        {
            double l = Left + (Width - Notch.ActualWidth) / 2;
            bool inside = p.X >= l - 2 && p.X <= l + Notch.ActualWidth + 2 && p.Y >= Top && p.Y <= Top + Notch.ActualHeight + 2;
            if (inside || _dragging || Notch.ContextMenu?.IsOpen == true) _collapseTimer.Stop();
            else if (!_collapseTimer.IsEnabled) _collapseTimer.Start();
            return;
        }
        if (_mode != Mode.Collapsed) { _hoverSince = null; return; }

        bool autoHidden = !_shown;
        if (autoHidden && _fg == FgState.Fullscreen) { _hoverSince = null; return; }

        double left = Left + (Width - CollapsedSize.Width) / 2;
        double hotHeight = autoHidden ? 3 : CollapsedSize.Height;
        bool over = p.X >= left && p.X <= left + CollapsedSize.Width && p.Y >= Top && p.Y <= Top + hotHeight;

        if (!over) { _hoverSince = null; return; }
        _hoverSince ??= DateTime.Now;
        if (DateTime.Now - _hoverSince >= (autoHidden ? RevealDelay : HoverDelay))
        {
            _hoverSince = null;
            _collapseTimer.Stop();
            GoTo(Mode.Expanded);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        NativeMethods.UnregisterHotKey(_hwnd, HideHotkeyId);
        _audio.Dispose();
        base.OnClosed(e);
    }

    private void PlaceWindow()
    {
        Top = 0;
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
    }

private void GoTo(Mode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        SetClickThrough(mode == Mode.Collapsed);

        var target = mode switch
        {
            Mode.Expanded => ExpandedSize,
            Mode.Peek => PeekSize,
            _ => CollapsedSize,
        };
        _w.Target = target.Width;
        _h.Target = target.Height;

        bool opening = mode != Mode.Collapsed;
        foreach (var s in new[] { _w, _h })
        {
            s.Stiffness = opening ? 300 : 340;
            s.Damping = opening ? 27 : 36;
        }

        SetView(CollapsedView, mode == Mode.Collapsed, opening ? 0 : 140);
        SetView(PeekView, mode == Mode.Peek, 90);
        SetView(ExpandedView, mode == Mode.Expanded, 90);

        if (mode == Mode.Expanded)
        {
            _peekTimer.Stop();
            UpdateMedia();
            if (_tab == NotifTab) ClearBadge();
            OnTick();
        }

        UpdateEqualizer();
        StartAnimating();
        ApplyVisibility();
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

        _w.Step(dt);
        _h.Step(dt);
        _tabX.Step(dt);
        TabIndicatorX.X = _tabX.Value;

        if (_w.Settled && _h.Settled && _tabX.Settled)
        {
            _w.Snap();
            _h.Snap();
            _tabX.Snap();
            TabIndicatorX.X = _tabX.Value;
            CompositionTarget.Rendering -= OnFrame;
            _animating = false;
        }
        ApplyShape();
    }

    private void ApplyShape()
    {
        double w = Math.Max(40, _w.Value), h = Math.Max(20, _h.Value);
        Notch.Width = w;
        Notch.Height = h;

        double p = Math.Clamp((h - CollapsedSize.Height) / (ExpandedSize.Height - CollapsedSize.Height), 0, 1.15);
        double r = 17 + 15 * Math.Min(p, 1);
        Notch.CornerRadius = new CornerRadius(0, 0, r, r);

        Glow.Width = w + 36;
        Glow.Height = h + 44;
        Glow.Opacity = Math.Clamp(p, 0, 1) * 0.6;
    }

    private static void SetView(FrameworkElement view, bool visible, int delayMs)
    {
        view.IsHitTestVisible = visible;
        var a = visible
            ? new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { BeginTime = TimeSpan.FromMilliseconds(delayMs) }
            : new DoubleAnimation(0, TimeSpan.FromMilliseconds(100));
        a.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        view.BeginAnimation(OpacityProperty, a);
    }

private void Tab_Click(object sender, RoutedEventArgs e) => SelectTab(int.Parse((string)((Button)sender).Tag));

    private readonly Spring _tabX = new(0) { Stiffness = 380, Damping = 30 };
    private const double TabSlot = 46;
    private static readonly SolidColorBrush TabOn = new(Colors.White);
    private static readonly SolidColorBrush TabOff = new(Color.FromArgb(0x94, 0xFF, 0xFF, 0xFF));

    private void SelectTab(int i)
    {
        bool changed = i != _tab;
        _tab = i;
        for (int k = 0; k < _panels.Length; k++)
        {
            bool on = k == i;
            _panels[k].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            _tabBtns[k].Foreground = on ? TabOn : TabOff;
        }

        _tabX.Target = i * TabSlot;
        if (_mode == Mode.Expanded) StartAnimating(); else { _tabX.Snap(); TabIndicatorX.X = _tabX.Value; }
        if (changed && _mode == Mode.Expanded) AnimatePanelIn(_panels[i]);

        if (i == 3) BuildCalendar(force: true);
        if (i == 4) UpdateWeatherPanel();
        if (i == NotifTab) ClearBadge();
        UpdateEqualizer();
        OnTick();
    }

    private const int NotifTab = 5;

    private static void AnimatePanelIn(FrameworkElement panel)
    {
        var scale = new ScaleTransform(0.96, 0.96);
        var move = new TranslateTransform(0, 6);
        panel.RenderTransformOrigin = new Point(0.5, 0.5);
        panel.RenderTransform = new TransformGroup { Children = { scale, move } };

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(380);
        panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, dur) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 } });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, dur) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 } });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, dur) { EasingFunction = ease });
    }

private void UpdateWeatherPanel()
    {
        if (!_weather.HasData)
        {
            WxCity.Text = "Locating…";
            return;
        }
        WxCity.Text = _weather.City.Length > 0 ? _weather.City : "My Location";
        WxTemp.Text = Deg(_weather.Temperature);
        WxCond.Text = _weather.Condition;
        WxHiLo.Text = $"H:{Deg(_weather.High)}   L:{Deg(_weather.Low)}";

        double uv = _weather.UvIndex;
        string uvLevel = uv < 3 ? "Low" : uv < 6 ? "Moderate" : uv < 8 ? "High" : "Very High";
        WxUv.Text = $"UV {Math.Round(uv):0} {uvLevel}";
        WxWind.Text = $"{Math.Round(_weather.WindKmh):0} km/h";
        WxHum.Text = $"{_weather.Humidity}%";

        WxHourly.Children.Clear();
        for (int i = 0; i < _weather.Hourly.Count; i++)
        {
            var h = _weather.Hourly[i];
            var canvas = new Canvas { Width = 24, Height = 24 };
            WeatherIcons.Draw(canvas, h.Kind, h.IsDay);
            var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            cell.Children.Add(new TextBlock
            {
                Text = i == 0 ? "Now" : h.Time.ToString("HH"),
                Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)),
                FontSize = 11, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            cell.Children.Add(new Viewbox { Width = 20, Height = 20, Margin = new Thickness(0, 4, 0, 4), Child = canvas });
            cell.Children.Add(new TextBlock
            {
                Text = Deg(h.Temp),
                Foreground = Brushes.White,
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            WxHourly.Children.Add(cell);
        }
    }

    private static string Deg(double t) =>
        ((int)Math.Round(t, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + "°";

private void OnTick()
    {
        var now = DateTime.Now;
        PillTime.Text = now.ToString("HH:mm");

        _stats.UpdateBattery();
        UpdatePillBattery();

        if (_mode != Mode.Expanded) return;

        switch (_tab)
        {
            case 0:
                var (pos, dur) = _media.GetTimeline();
                if (!SeekSlider.IsMouseCaptureWithin)
                {
                    SeekSlider.Maximum = Math.Max(1, dur.TotalSeconds);
                    SeekSlider.Value = pos.TotalSeconds;
                    PosText.Text = Fmt(pos);
                }
                DurText.Text = Fmt(dur);
                break;
            case 2:
                UpdateSystemPanel();
                break;
            case 3:
                BigClock.Text = now.ToString("HH:mm");
                DateText.Text = now.ToString("dddd, MMMM d", En);
                BuildCalendar(force: false);
                break;
        }
    }

    private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

private void UpdatePillBattery()
    {
        int pct = _stats.BatteryPercent;
        if (pct < 0) { PillBattery.Visibility = Visibility.Collapsed; return; }
        PillBattery.Visibility = Visibility.Visible;

        const double inner = 24 - 2 * 1.2 - 2 * 1.4;
        PillBatFill.Width = Math.Max(1.5, inner * pct / 100.0);
        PillBatFill.Background = _stats.Charging
            ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58))
            : pct <= 20 ? new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)) : Brushes.White;
        PillBolt.Visibility = _stats.Charging ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RefreshWeatherAsync()
    {
        if (!await _weather.RefreshAsync(_settings.City) || !_weather.HasData) return;
        WeatherIcons.Draw(WeatherCanvas, _weather.Kind, _weather.IsDay);
        PillTemp.Text = Deg(_weather.Temperature);
        PillWeather.Visibility = Visibility.Visible;
        UpdateWeatherPanel();
    }

private void UpdateMedia()
    {
        TitleText.Text = _media.Title.Length > 0 ? _media.Title : "Nothing playing";
        ArtistText.Text = _media.Artist;
        ArtBrush.ImageSource = _media.Art;
        PlayIcon.Data = Geometry.Parse(_media.Playing
            ? "M6.5,4.5 H10 V19.5 H6.5 Z M14,4.5 H17.5 V19.5 H14 Z"
            : "M7,4.5 L19.5,12 L7,19.5 Z");

        var green = new SolidColorBrush(Color.FromRgb(0x1E, 0xD7, 0x60));
        ShuffleBtn.Foreground = _media.Shuffle ? green : Brushes.White;
        ShuffleDot.Visibility = _media.Shuffle ? Visibility.Visible : Visibility.Collapsed;
        ShuffleBtn.Opacity = _media.CanShuffle ? 1 : 0.35;
        bool rep = _media.Repeat != Windows.Media.MediaPlaybackAutoRepeatMode.None;
        RepeatBtn.Foreground = rep ? green : Brushes.White;
        RepeatBtn.Content = _media.Repeat == Windows.Media.MediaPlaybackAutoRepeatMode.Track ? "" : "";
        RepeatDot.Visibility = rep ? Visibility.Visible : Visibility.Collapsed;
        RepeatBtn.Opacity = _media.CanRepeat ? 1 : 0.35;
        SeekSlider.IsEnabled = _media.CanSeek;

        UpdateEqualizer();
        OnTick();
    }

    private bool _eqRunning;
    private static readonly double[] EqDurations = { 0.42, 0.55, 0.36, 0.61, 0.48 };

    private void UpdateEqualizer()
    {
        bool run = _media.Playing && _mode == Mode.Expanded && _tab == 0;
        if (run == _eqRunning) return;
        _eqRunning = run;
        int i = 0;
        foreach (System.Windows.Shapes.Rectangle bar in Equalizer.Children)
        {
            var st = (ScaleTransform)bar.RenderTransform;
            if (run)
            {
                var a = new DoubleAnimation(0.25, 1, TimeSpan.FromSeconds(EqDurations[i % EqDurations.Length]))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                    BeginTime = TimeSpan.FromMilliseconds(-90 * i),
                };
                Timeline.SetDesiredFrameRate(a, 30);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
            }
            else
            {
                st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.3, TimeSpan.FromMilliseconds(250)));
            }
            i++;
        }
    }

    private void Shuffle_Click(object s, RoutedEventArgs e) => _media.ToggleShuffle();
    private void Repeat_Click(object s, RoutedEventArgs e) => _media.CycleRepeat();

    private void Seek_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _media.Seek(TimeSpan.FromSeconds(SeekSlider.Value));
        PosText.Text = Fmt(TimeSpan.FromSeconds(SeekSlider.Value));
    }

    private void PlayPause_Click(object s, RoutedEventArgs e) => _media.PlayPause();
    private void Next_Click(object s, RoutedEventArgs e) => _media.Next();
    private void Prev_Click(object s, RoutedEventArgs e) => _media.Previous();

private void UpdateSystemPanel()
    {
        _stats.Update();

        int cpu = _stats.CpuPercent;
        CpuText.Text = cpu + "%";
        CpuSub.Text = cpu < 10 ? "Idle" : cpu < 35 ? "Light load" : cpu < 75 ? "Busy" : "Heavy load";
        SetRing(CpuRing, cpu);

        RamText.Text = _stats.RamPercent + "%";
        RamSub.Text = string.Format(CultureInfo.InvariantCulture, "{0:0.0} / {1:0} GB", _stats.MemUsedGB, _stats.MemTotalGB);
        SetRing(RamRing, _stats.RamPercent);

        int bat = _stats.BatteryPercent;
        if (bat >= 0)
        {
            BatText.Text = bat + "%";
            SetRing(BatRing, bat);
            BatLabel.Text = _stats.Charging ? "Charging" : "Battery";
            BatSub.Text = _stats.Charging
                ? (bat >= 100 ? "Fully charged" : "Plugged in")
                : _stats.TimeLeft is { } t ? $"{(int)t.TotalHours}h {t.Minutes}m left" : "On battery";
            BatRing.Stroke = new SolidColorBrush(!_stats.Charging && bat <= 20 ? Color.FromRgb(0xFF, 0x45, 0x3A) : Color.FromRgb(0x30, 0xD1, 0x58));
        }
        else
        {
            BatText.Text = "AC";
            SetRing(BatRing, 100);
            BatLabel.Text = "Power";
            BatSub.Text = "No battery";
        }

        if (!VolPill.IsMouseCaptured) SetVolumeUi(_audio.GetVolume() * 100);
    }

    private static void SetRing(System.Windows.Shapes.Path ring, double percent)
    {
        const double size = 52, stroke = 4.5;
        double r = (size - stroke) / 2, c = size / 2;
        double pct = Math.Clamp(percent / 100.0, 0, 0.9999);
        if (pct <= 0.001) { ring.Data = Geometry.Empty; return; }

        double angle = pct * 2 * Math.PI;
        var start = new Point(c, c - r);
        var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, pct > 0.5, SweepDirection.Clockwise, true));
        var g = new PathGeometry();
        g.Figures.Add(fig);
        g.Freeze();
        ring.Data = g;
    }

    private static readonly SolidColorBrush DarkInk = new(Color.FromRgb(0x11, 0x18, 0x27));

    private void SetVolumeUi(double pct)
    {
        pct = Math.Clamp(Math.Round(pct), 0, 100);
        double w = Math.Max(0, VolPill.ActualWidth - 2) * pct / 100.0;
        VolFill.Width = w;
        double rl = Math.Min(15, w / 2);
        VolFill.CornerRadius = new CornerRadius(rl, Math.Min(4, w / 2), Math.Min(4, w / 2), rl);
        VolText.Text = (int)pct + "%";

        bool leftDark = pct > 22;
        VolIcon.Stroke = leftDark ? DarkInk : Brushes.White;
        VolLabel.Foreground = leftDark ? DarkInk : Brushes.White;
        VolText.Foreground = pct > 88 ? DarkInk : Brushes.White;
    }

    private void SetVolumeFromMouse(MouseEventArgs e)
    {
        double pct = 100 * e.GetPosition(VolPill).X / Math.Max(1, VolPill.ActualWidth);
        pct = Math.Clamp(pct, 0, 100);
        _audio.SetVolume((float)(pct / 100));
        SetVolumeUi(pct);
    }

    private void VolPill_Down(object sender, MouseButtonEventArgs e) { VolPill.CaptureMouse(); SetVolumeFromMouse(e); e.Handled = true; }
    private void VolPill_Move(object sender, MouseEventArgs e) { if (VolPill.IsMouseCaptured) SetVolumeFromMouse(e); }
    private void VolPill_Up(object sender, MouseButtonEventArgs e) { VolPill.ReleaseMouseCapture(); }

    private void VolPill_Wheel(object sender, MouseWheelEventArgs e)
    {
        double pct = Math.Clamp(_audio.GetVolume() * 100 + (e.Delta > 0 ? 2 : -2), 0, 100);
        _audio.SetVolume((float)(pct / 100));
        SetVolumeUi(pct);
        e.Handled = true;
    }

private void BuildCalendar(bool force)
    {
        var today = DateTime.Today;
        if (!force && _calBuiltFor == today) return;
        _calBuiltFor = today;

        MonthText.Text = today.ToString("MMMM yyyy", En).ToUpper(En);
        CalGrid.Children.Clear();

        foreach (var h in new[] { "M", "T", "W", "T", "F", "S", "S" })
            CalGrid.Children.Add(Cell(h, dim: true, highlight: false));

        var first = new DateTime(today.Year, today.Month, 1);
        int offset = ((int)first.DayOfWeek + 6) % 7;
        for (int i = 0; i < offset; i++) CalGrid.Children.Add(new TextBlock());
        int days = DateTime.DaysInMonth(today.Year, today.Month);
        for (int d = 1; d <= days; d++)
            CalGrid.Children.Add(Cell(d.ToString(), dim: false, highlight: d == today.Day));
    }

    private UIElement Cell(string text, bool dim, bool highlight)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            FontWeight = highlight ? FontWeights.Bold : FontWeights.Normal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = highlight ? Brushes.White : (Brush)FindResource(dim ? "Dim" : "Fg"),
        };
        return new Border
        {
            Width = 18,
            Height = 15,
            CornerRadius = new CornerRadius(7.5),
            Background = highlight ? new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)) : Brushes.Transparent,
            Child = tb,
        };
    }

private async Task PollNotificationsAsync()
    {
        var res = await _notifs.PollAsync();
        if (res is not { } r) return;

        ShowNotifList(r.all);
        if (r.fresh.Count == 0) return;
        var latest = r.fresh.OrderByDescending(n => n.Time).First();

        if (!(_mode == Mode.Expanded && _tab == NotifTab))
        {
            _unseen += r.fresh.Count;
            NotifBadgeText.Text = _unseen > 9 ? "9+" : _unseen.ToString();
            NotifBadge.Visibility = Visibility.Visible;
        }

        if (_mode == Mode.Expanded || _hidden || (_fg == FgState.Fullscreen && _settings.AutoHide && !_forceShow)) return;

        PeekHeader.Text = latest.Header;
        PeekTitle.Text = latest.Title;
        PeekBody.Text = latest.Body;
        PeekBody.Visibility = latest.HasBody ? Visibility.Visible : Visibility.Collapsed;
        PeekIcon.ImageSource = latest.Icon;
        _peekItem = latest;
        GoTo(Mode.Peek);
        _peekTimer.Stop();
        _peekTimer.Interval = TimeSpan.FromSeconds(5);
        _peekTimer.Start();
    }

    private NotifItem? _peekItem;
    private readonly DispatcherTimer _peekExpandTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };

    private void OpenNotification(NotifItem item)
    {
        bool ok = NotificationActivator.Activate(item.Id);
        if (!ok) return;
        _notifs.Remove(item.Id);
        if (NotifList.ItemsSource is List<NotifItem> list)
            ShowNotifList(list.Where(n => n.Id != item.Id).ToList());
        _peekTimer.Stop();
        GoTo(Mode.Collapsed);
    }

    private void NotifList_Click(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        var container = ItemsControl.ContainerFromElement(NotifList, (DependencyObject)e.OriginalSource) as ListBoxItem;
        if (container?.DataContext is NotifItem item)
        {
            e.Handled = true;
            OpenNotification(item);
        }
    }

    private void ShowNotifList(List<NotifItem> all)
    {
        NotifList.ItemsSource = all;
        bool empty = all.Count == 0;
        NotifEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        NotifHeader.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        NotifCount.Text = all.Count == 1 ? "1 notification" : $"{all.Count} notifications";
    }

    private void NotifRemove_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (((Button)sender).Tag is not NotifItem item) return;
        _notifs.Remove(item.Id);
        if (NotifList.ItemsSource is List<NotifItem> list)
            ShowNotifList(list.Where(n => n.Id != item.Id).ToList());
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        _notifs.ClearAll();
        ShowNotifList(new List<NotifItem>());
        ClearBadge();
    }

    private void ClearBadge()
    {
        _unseen = 0;
        NotifBadge.Visibility = Visibility.Collapsed;
    }

private void OnDragEnterFiles(object sender, DragEventArgs e)
    {
        _collapseTimer.Stop();
        GoTo(Mode.Expanded);
        SelectTab(1);
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDropFiles(object sender, DragEventArgs e)
    {
        if (_dragging) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var f in files)
                if (!_shelf.Any(s => s.Path == f))
                    _shelf.Add(new ShelfItem(System.IO.Path.GetFileName(f), f));
        }
        _collapseTimer.Start();
    }

    private void ShelfRemove_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is ShelfItem item) _shelf.Remove(item);
        e.Handled = true;
    }

    private void Shelf_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ShelfList.SelectedItem is ShelfItem item)
        {
            try { Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true }); } catch { }
        }
    }

    private static bool IsInsideButton(DependencyObject? d)
    {
        while (d != null) { if (d is Button) return true; d = VisualTreeHelper.GetParent(d); }
        return false;
    }

    private void Shelf_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragItem = null;
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        var container = ItemsControl.ContainerFromElement(ShelfList, (DependencyObject)e.OriginalSource) as ListBoxItem;
        _dragItem = container?.DataContext as ShelfItem;
    }

    private void Shelf_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null) return;
        var diff = e.GetPosition(null) - _dragStart;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _dragItem;
        _dragItem = null;
        _dragging = true;
        try
        {
            DragDrop.DoDragDrop(ShelfList, new DataObject(DataFormats.FileDrop, new[] { item.Path }), DragDropEffects.Copy);
        }
        finally
        {
            _dragging = false;
            _collapseTimer.Start();
        }
    }

private void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoStart = AutoStartItem.IsChecked;
        _settings.Save();
        AutoStart.Apply(_settings.AutoStart);
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        _settings.Save();
        try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Settings.FilePath}\"") { UseShellExecute = true }); } catch { }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
