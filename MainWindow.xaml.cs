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
using NotchWin.Services;

namespace NotchWin;

public record ShelfItem(string Name, string Path);

public partial class MainWindow : Window
{
    private const double CollapsedW = 230, CollapsedH = 34;
    private const double ExpandedW = 520, ExpandedH = 190;
    private const double WindowPad = 40;

    private readonly MediaService _media = new();
    private readonly AudioService _audio = new();
    private readonly SystemStats _stats = new();
    private readonly ObservableCollection<ShelfItem> _shelf = new();

    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    private FrameworkElement[] _panels = Array.Empty<FrameworkElement>();
    private Button[] _tabBtns = Array.Empty<Button>();

    private bool _expanded;
    private bool _dragging;
    private bool _updatingVol;
    private int _anim;
    private int _tab;
    private DateTime _calBuiltFor = DateTime.MinValue;

    private HwndSource? _hwndSource;
    private const int HotkeyId = 1;

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

        SetWindowSize(CollapsedW, CollapsedH);
        Top = 0;
        SelectTab(0);

        _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); if (!_dragging) Collapse(); };
        _tick.Tick += (_, _) => OnTick();

        MouseEnter += (_, _) => { _collapseTimer.Stop(); Expand(); };
        MouseLeave += (_, _) => { if (!_dragging) _collapseTimer.Start(); };
        DragEnter += OnDragEnterFiles;
        DragOver += (_, e) => { e.Effects = DragDropEffects.Copy; e.Handled = true; _collapseTimer.Stop(); };
        DragLeave += (_, _) => { if (!_dragging) _collapseTimer.Start(); };
        Drop += OnDropFiles;

        _media.Changed += () => Dispatcher.BeginInvoke(UpdateMedia);
        Loaded += async (_, _) =>
        {
            await _media.InitAsync();
            RefreshMic();
            OnTick();
            _tick.Start();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;

        var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));

        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);
        NativeMethods.RegisterHotKey(hwnd, HotkeyId, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x4D);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            _audio.ToggleMic();
            RefreshMic();
            handled = true;
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.UnregisterHotKey(hwnd, HotkeyId);
        _audio.Dispose();
        base.OnClosed(e);
    }

    private void SetWindowSize(double notchW, double notchH)
    {
        bool big = notchW > CollapsedW + 1;
        Width = big ? notchW + WindowPad : notchW;
        Height = big ? notchH + 10 : notchH;
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
    }

    private static DoubleAnimation Anim(double to, int ms = 280, int delay = 0) => new(to, TimeSpan.FromMilliseconds(ms))
    {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        BeginTime = TimeSpan.FromMilliseconds(delay),
    };

    private void Expand()
    {
        if (_expanded) return;
        _expanded = true;
        int gen = ++_anim;

        SetWindowSize(ExpandedW, ExpandedH);
        UpdateMedia();
        OnTick();

        Notch.BeginAnimation(WidthProperty, Anim(ExpandedW));
        Notch.BeginAnimation(HeightProperty, Anim(ExpandedH));
        CollapsedView.BeginAnimation(OpacityProperty, Anim(0, 120));
        ExpandedView.BeginAnimation(OpacityProperty, Anim(1, 200, 110));
        ExpandedView.IsHitTestVisible = true;
        _ = gen;
    }

    private void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;
        int gen = ++_anim;

        ExpandedView.IsHitTestVisible = false;
        ExpandedView.BeginAnimation(OpacityProperty, Anim(0, 120));
        CollapsedView.BeginAnimation(OpacityProperty, Anim(1, 200, 120));

        var w = Anim(CollapsedW, 260);
        w.Completed += (_, _) =>
        {
            if (gen == _anim && !_expanded) SetWindowSize(CollapsedW, CollapsedH);
        };
        Notch.BeginAnimation(WidthProperty, w);
        Notch.BeginAnimation(HeightProperty, Anim(CollapsedH, 260));
    }

    private void Tab_Click(object sender, RoutedEventArgs e) => SelectTab(int.Parse((string)((Button)sender).Tag));

    private void SelectTab(int i)
    {
        _tab = i;
        for (int k = 0; k < _panels.Length; k++)
        {
            _panels[k].Visibility = k == i ? Visibility.Visible : Visibility.Collapsed;
            _tabBtns[k].Background = k == i ? (Brush)FindResource("Chip") : Brushes.Transparent;
        }
        if (i == 3) BuildCalendar(force: true);
        if (i == 4) RefreshMic();
        OnTick();
    }

    private void OnTick()
    {
        var now = DateTime.Now;

        PillText.Text = _media.Playing && _media.Title.Length > 0 ? "♪ " + _media.Title : now.ToString("HH:mm");

        if (!_expanded) return;

        switch (_tab)
        {
            case 0:
                var (pos, dur) = _media.GetTimeline();
                MediaProgress.Value = dur.TotalSeconds > 0 ? 100 * pos.TotalSeconds / dur.TotalSeconds : 0;
                break;
            case 2:
                _stats.Update();
                CpuBar.Value = _stats.CpuPercent; CpuText.Text = _stats.CpuPercent + "%";
                RamBar.Value = _stats.RamPercent; RamText.Text = _stats.RamPercent + "%";
                if (_stats.BatteryPercent >= 0)
                {
                    BatBar.Value = _stats.BatteryPercent;
                    BatText.Text = _stats.BatteryPercent + "%";
                    BatLabel.Text = _stats.Charging ? "⚡ Baterija" : "Baterija";
                }
                else { BatBar.Value = 0; BatText.Text = "—"; BatLabel.Text = "Napajanje"; }
                _updatingVol = true;
                VolSlider.Value = _audio.GetVolume() * 100;
                VolText.Text = (int)VolSlider.Value + "%";
                _updatingVol = false;
                break;
            case 3:
                BigClock.Text = now.ToString("HH:mm:ss");
                DateText.Text = now.ToString("dddd, d. MMMM", CultureInfo.CurrentCulture);
                BuildCalendar(force: false);
                break;
        }
    }

    private void UpdateMedia()
    {
        TitleText.Text = _media.Title.Length > 0 ? _media.Title : "Ništa se ne pušta";
        ArtistText.Text = _media.Artist;
        ArtBrush.ImageSource = _media.Art;
        PlayBtn.Content = _media.Playing ? "⏸" : "▶";
        OnTick();
    }

    private void PlayPause_Click(object s, RoutedEventArgs e) => _media.PlayPause();
    private void Next_Click(object s, RoutedEventArgs e) => _media.Next();
    private void Prev_Click(object s, RoutedEventArgs e) => _media.Previous();

    private void Vol_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingVol) return;
        _audio.SetVolume((float)(e.NewValue / 100.0));
        VolText.Text = (int)e.NewValue + "%";
    }

    private void BuildCalendar(bool force)
    {
        var today = DateTime.Today;
        if (!force && _calBuiltFor == today) return;
        _calBuiltFor = today;

        MonthText.Text = today.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        CalGrid.Children.Clear();

        foreach (var h in new[] { "P", "U", "S", "Č", "P", "S", "N" })
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
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource(dim ? "Dim" : "Fg"),
        };
        return new Border
        {
            Height = 17,
            CornerRadius = new CornerRadius(8),
            Background = highlight ? (Brush)FindResource("Accent") : Brushes.Transparent,
            Child = tb,
        };
    }

    private void Mic_Click(object sender, RoutedEventArgs e) { _audio.ToggleMic(); RefreshMic(); }

    private void RefreshMic()
    {
        bool muted = _audio.IsMicMuted();
        MicBtn.Background = new SolidColorBrush(muted ? Color.FromRgb(0xFF, 0x45, 0x3A) : Color.FromRgb(0x30, 0xD1, 0x58));
        MicBtn.Content = muted ? "🔇" : "🎤";
        MicText.Text = muted ? "Mikrofon je ISKLJUČEN" : "Mikrofon je uključen";
        PillMicDot.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDragEnterFiles(object sender, DragEventArgs e)
    {
        _collapseTimer.Stop();
        Expand();
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
            var data = new DataObject(DataFormats.FileDrop, new[] { item.Path });
            DragDrop.DoDragDrop(ShelfList, data, DragDropEffects.Copy);
        }
        finally
        {
            _dragging = false;
            _collapseTimer.Start();
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
