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
    private bool _updatingVol;
    private int _tab;
    private int _unseen;
    private DateTime _calBuiltFor = DateTime.MinValue;

    private Point _dragStart;
    private ShelfItem? _dragItem;

    public MainWindow()
    {
        InitializeComponent();

        _panels = new FrameworkElement[] { Panel0, Panel1, Panel2, Panel3, Panel4 };
        _tabBtns = new[] { TabBtn0, TabBtn1, TabBtn2, TabBtn3, TabBtn4 };
        ShelfList.ItemsSource = _shelf;
        _shelf.CollectionChanged += (_, _) =>
            ShelfHint.Visibility = _shelf.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        AutoStartItem.IsChecked = _settings.AutoStart;
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
            if (_mode == Mode.Peek) SelectTab(4);
            GoTo(Mode.Expanded);
        };
        Notch.MouseLeave += (_, _) => { if (!_dragging) _collapseTimer.Start(); };
        DragEnter += OnDragEnterFiles;
        DragOver += (_, e) => { e.Effects = DragDropEffects.Copy; e.Handled = true; _collapseTimer.Stop(); };
        DragLeave += (_, _) => { if (!_dragging) _collapseTimer.Start(); };
        Drop += OnDropFiles;

        _media.Changed += () => Dispatcher.BeginInvoke(UpdateMedia);

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
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
    }

    protected override void OnClosed(EventArgs e)
    {
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
            if (_tab == 4) ClearBadge();
            OnTick();
        }

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

        _w.Step(dt);
        _h.Step(dt);

        if (_w.Settled && _h.Settled)
        {
            _w.Snap();
            _h.Snap();
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

        Glow.Width = w + 110;
        Glow.Height = h + 52;
        Glow.Opacity = Math.Clamp(p, 0, 1) * 0.85;
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

    private void SelectTab(int i)
    {
        _tab = i;
        var active = (Brush)FindResource("ActiveTab");
        var glow = (System.Windows.Media.Effects.Effect)FindResource("TabGlow");
        for (int k = 0; k < _panels.Length; k++)
        {
            bool on = k == i;
            _panels[k].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            _tabBtns[k].Background = on ? active : Brushes.Transparent;
            _tabBtns[k].Effect = on ? glow : null;
        }
        if (i == 3) BuildCalendar(force: true);
        if (i == 4) ClearBadge();
        OnTick();
    }

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
                MediaProgress.Value = dur.TotalSeconds > 0 ? 100 * pos.TotalSeconds / dur.TotalSeconds : 0;
                PosText.Text = dur.TotalSeconds > 0 ? Fmt(pos) : "";
                DurText.Text = dur.TotalSeconds > 0 ? "-" + Fmt(dur - pos) : "";
                break;
            case 2:
                _stats.Update();
                CpuBar.Value = _stats.CpuPercent; CpuText.Text = _stats.CpuPercent + "%";
                RamBar.Value = _stats.RamPercent; RamText.Text = _stats.RamPercent + "%";
                if (_stats.BatteryPercent >= 0)
                {
                    BatBar.Value = _stats.BatteryPercent;
                    BatText.Text = _stats.BatteryPercent + "%";
                    BatLabel.Text = _stats.Charging ? "Charging" : "Battery";
                }
                else { BatBar.Value = 0; BatText.Text = "—"; BatLabel.Text = "Power"; }
                if (!VolSlider.IsMouseCaptureWithin)
                {
                    _updatingVol = true;
                    VolSlider.Value = Math.Round(_audio.GetVolume() * 100);
                    _updatingVol = false;
                }
                VolText.Text = (int)VolSlider.Value + "%";
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
        PillTemp.Text = Math.Round(_weather.Temperature).ToString("0", CultureInfo.InvariantCulture) + "°";
        PillWeather.Visibility = Visibility.Visible;
    }

    private void UpdateMedia()
    {
        TitleText.Text = _media.Title.Length > 0 ? _media.Title : "Nothing playing";
        ArtistText.Text = _media.Artist;
        ArtBrush.ImageSource = _media.Art;
        PlayBtn.Content = _media.Playing ? "" : "";
        OnTick();
    }

    private void PlayPause_Click(object s, RoutedEventArgs e) => _media.PlayPause();
    private void Next_Click(object s, RoutedEventArgs e) => _media.Next();
    private void Prev_Click(object s, RoutedEventArgs e) => _media.Previous();

    private void Vol_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingVol || VolText == null) return;
        _audio.SetVolume((float)(e.NewValue / 100.0));
        VolText.Text = (int)e.NewValue + "%";
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

        if (!(_mode == Mode.Expanded && _tab == 4))
        {
            _unseen += r.fresh.Count;
            NotifBadgeText.Text = _unseen > 9 ? "9+" : _unseen.ToString();
            NotifBadge.Visibility = Visibility.Visible;
        }

        if (_mode == Mode.Expanded) return;

        PeekHeader.Text = latest.Header;
        PeekTitle.Text = latest.Title;
        PeekBody.Text = latest.Body;
        PeekBody.Visibility = latest.HasBody ? Visibility.Visible : Visibility.Collapsed;
        PeekIcon.ImageSource = latest.Icon;
        GoTo(Mode.Peek);
        _peekTimer.Stop();
        _peekTimer.Start();
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
