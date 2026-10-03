using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Input;
using Microsoft.Win32;

namespace DynIsland;

public partial class MainWindow : Window
{
    private enum State { Idle, Music, Notification }
    private State current = State.Idle;
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer progressTick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer notifTimer = new();
    private readonly DispatcherTimer musicTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    // Hover intent: swallows edge jitter so rapid in/out can't spam transitions.
    private readonly DispatcherTimer hoverIn = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private readonly DispatcherTimer hoverOut = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly WeatherService weather = new();
    private readonly MediaService media = new();
    private readonly NotificationService notifs = new();
    private readonly CancellationTokenSource cts = new();

    // --- smooth timeline state: extrapolate monotonically from the last
    // server sample; only snap on track change / real seek / pause.
    private TrackInfo? track;
    private string trackKey = "";
    private TimeSpan shown = TimeSpan.Zero;   // displayed position (monotonic)
    private TimeSpan dur = TimeSpan.Zero;     // track length (kept separately: Spotify may report 0 briefly)
    private DateTime lastTick = DateTime.UtcNow;
    private int behindCount;                  // consecutive stale-behind samples (seek confirmation)
    private DateTime forceSnapUntil = DateTime.MinValue; // user pressed a transport button: trust server
    private bool? lastPlaying;                // last rendered play/pause icon state
    private bool hovered;

    public MainWindow()
    {
        InitializeComponent();
        ApplySettings();
        PositionTopCenter();
        SystemEvents.DisplaySettingsChanged += (_, _) => PositionTopCenter();
        // The pill is pinned to top-center: every size change recenters
        // synchronously, so the pill only ever grows/shrinks in place.
        SizeChanged += (_, _) => CenterNow();

        clock.Tick += (_, _) => UpdateClock();
        UpdateClock();
        clock.Start();

        progressTick.Tick += (_, _) => UpdateProgress();
        progressTick.Start();

        notifTimer.Tick += (_, _) => { notifTimer.Stop(); ShowForTrackOrIdle(); };
        musicTimer.Tick += (_, _) =>
        {
            musicTimer.Stop();
            // Settle back to the compact song pill while music keeps playing.
            if (!hovered && current == State.Music && track?.Playing == true)
                Show(State.Idle);
        };

        Loaded += async (_, _) =>
        {
            MakeOverlay();
            // Mutable border brush up front: the XAML brush may be frozen, which
            // would make the hover glow throw on first hover. Do it once here.
            try { Pill.BorderBrush = new SolidColorBrush(((SolidColorBrush)Pill.BorderBrush).Color); } catch { }
            PositionTopCenter();
            _ = weather.StartAsync(cts.Token);
            weather.Updated += w => Dispatcher.BeginInvoke(() => UpdateWeather(w));
            _ = weather.RefreshAsync().ContinueWith(_ => { var l = weather.Last; if (l != null) Dispatcher.BeginInvoke(() => UpdateWeather(l)); });
            notifs.Arrived += n => OnNotif(n);
            _ = notifs.StartAsync();
            media.Changed += OnTrack;
            if (SettingsStore.Current.MusicEnabled) await media.StartAsync();
            ResumeTimer();
            Dbg.Log($"render tier={RenderCapability.Tier >> 16} fastAnims={FastAnims}");
            AnimateIn();
        };
        MouseEnter += (_, _) => { hoverOut.Stop(); hoverIn.Stop(); hoverIn.Start(); };
        MouseLeave += (_, _) => { hoverIn.Stop(); hoverOut.Stop(); hoverOut.Start(); };
        hoverIn.Tick += (_, _) => { hoverIn.Stop(); Dbg.Log("hover in"); HoverExpand(true); };
        hoverOut.Tick += (_, _) => { hoverOut.Stop(); Dbg.Log("hover out"); HoverExpand(false); };
        idleDefer.Tick += (_, _) => { idleDefer.Stop(); DoShow(State.Idle); };
    }

    public void ApplySettings()
    {
        var s = SettingsStore.Current;
        // Fade the whole island in/out instead of popping visibility.
        bool wantVisible = s.IslandEnabled;
        if (wantVisible)
        {
            if (Visibility != Visibility.Visible)
            {
                Visibility = Visibility.Visible;
                BeginAnimation(OpacityProperty, null);
                Opacity = 0;
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, ScaledMs(250))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
        }
        else if (Visibility == Visibility.Visible)
        {
            var f = new DoubleAnimation(Opacity, 0, ScaledMs(180))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            f.Completed += (_, _) => { if (!SettingsStore.Current.IslandEnabled) Visibility = Visibility.Hidden; };
            BeginAnimation(OpacityProperty, f);
        }
        Pill.Opacity = s.IslandOpacity;
        Pill.CornerRadius = new CornerRadius(s.CornerRadius);
        Pill.LayoutTransform = new ScaleTransform(s.IslandScale, s.IslandScale);
        notifTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(s.NotificationSeconds, 2, 30));
        var btnVis = s.ShowControls ? Visibility.Visible : Visibility.Collapsed;
        PrevBtn.Visibility = PlayBtn.Visibility = NextBtn.Visibility = btnVis;
        if (!s.ShowArtwork) ArtImage.Source = null;
        else if (track?.Art != null) ArtImage.Source = track.Art;
        if (s.MusicEnabled) _ = media.StartAsync();
        else { track = null; trackKey = ""; musicTimer.Stop(); if (current == State.Music) Show(State.Idle); }
        if (s.NotificationsEnabled) _ = notifs.StartAsync();
        ReloadHotkey();
        RefreshIdleMode();
    }

    // ---- window overlay ----
    private void MakeOverlay()
    {
        var h = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    private double DpiX => PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;

    /// <summary>Exact screen-center X for the window's left edge anchor, in DIPs.</summary>
    private double CenterXDip()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null) return Left + ActualWidth / 2;
        return (screen.WorkingArea.Left + screen.WorkingArea.Width / 2.0) / DpiX;
    }

    private const double TopOffset = 24; // gap between screen top and window top

    /// <summary>Software rendering (tier 0/1: RDP, old GPUs, power-saver)
    /// makes layered-window resizes choppy regardless of easing — there we
    /// use short fades instead of full morphs.</summary>
    private static bool FastAnims => (RenderCapability.Tier >> 16) < 2;
    private TimeSpan ScaledMs(double baseMs)
    {
        double ms = FastAnims ? Math.Min(baseMs, 160) : baseMs;
        return TimeSpan.FromMilliseconds(ms / Math.Max(0.4, SettingsStore.Current.AnimationSpeed));
    }

    private void CenterNow()
    {
        try
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen == null) return;
            Left = CenterXDip() - ActualWidth / 2;
            Top = TopOffset;
        }
        catch { }
    }

    private void PositionTopCenter()
    {
        try { Dispatcher.BeginInvoke(CenterNow, DispatcherPriority.Loaded); }
        catch { }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var src = (HwndSource)PresentationSource.FromVisual(this);
            src?.AddHook(WndProc);
            // Global island toggle, combo stored in settings. Works from any app.
            ReloadHotkey();
        }
        catch (Exception ex) { Dbg.Log("hotkey register failed: " + ex.Message); }
        PositionTopCenter();
    }

    private uint regMods, regVk; // currently registered combo (avoids re-register churn)
    public bool HotkeyActive { get; private set; }

    /// <summary>(Re)registers the global toggle hotkey from settings.
    /// Skips when unchanged; reports conflicts via <see cref="HotkeyActive"/>.</summary>
    public void ReloadHotkey()
    {
        try
        {
            var h = new WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero) return;
            var s = SettingsStore.Current;
            if (s.HotkeyMods == regMods && s.HotkeyVk == regVk && HotkeyActive) return;
            try { UnregisterHotKey(h, HOTKEY_ID); } catch { }
            HotkeyActive = false;
            regMods = 0; regVk = 0;
            if (s.HotkeyMods != 0 && s.HotkeyVk != 0)
            {
                if (RegisterHotKey(h, HOTKEY_ID, s.HotkeyMods, s.HotkeyVk))
                { regMods = s.HotkeyMods; regVk = s.HotkeyVk; HotkeyActive = true; }
                else Dbg.Log($"hotkey {HotkeyText(s.HotkeyMods, s.HotkeyVk)} register failed (in use?)");
            }
        }
        catch (Exception ex) { Dbg.Log("hotkey reload failed: " + ex.Message); }
    }

    /// <summary>"Ctrl+Shift+D" style text for a mods+vk combo.</summary>
    public static string HotkeyText(uint mods, uint vk)
    {
        var parts = new List<string>();
        if ((mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & MOD_WIN) != 0) parts.Add("Win");
        parts.Add(KeyName(vk));
        return string.Join("+", parts);
    }

    private static string KeyName(uint vk)
    {
        if ((vk >= 'A' && vk <= 'Z') || (vk >= '0' && vk <= '9')) return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);
        return $"VK{vk:X2}";
    }

    /// <summary>Parses "Ctrl+Shift+D" / "Alt+F4" style text. Requires at
    /// least one modifier and a letter, digit, or F-key.</summary>
    public static bool TryParseHotkey(string text, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        var tokens = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return false;
        foreach (var t in tokens[..^1])
        {
            if (t.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || t.Equals("Control", StringComparison.OrdinalIgnoreCase)) mods |= MOD_CONTROL;
            else if (t.Equals("Shift", StringComparison.OrdinalIgnoreCase)) mods |= MOD_SHIFT;
            else if (t.Equals("Alt", StringComparison.OrdinalIgnoreCase)) mods |= MOD_ALT;
            else if (t.Equals("Win", StringComparison.OrdinalIgnoreCase) || t.Equals("Windows", StringComparison.OrdinalIgnoreCase)) mods |= MOD_WIN;
            else return false;
        }
        string k = tokens[^1].ToUpperInvariant();
        if (k.Length == 1 && ((k[0] >= 'A' && k[0] <= 'Z') || (k[0] >= '0' && k[0] <= '9'))) vk = k[0];
        else if (k.Length >= 2 && k[0] == 'F' && int.TryParse(k[1..], out int f) && f >= 1 && f <= 24) vk = (uint)(0x70 + f - 1);
        else return false;
        return mods != 0 && vk != 0;
    }

    private DateTime lastHotkey = DateTime.MinValue;
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            handled = true;
            // Key auto-repeat would flutter the toggle: debounce 400 ms.
            if ((DateTime.UtcNow - lastHotkey).TotalMilliseconds < 400) return IntPtr.Zero;
            lastHotkey = DateTime.UtcNow;
            Dispatcher.BeginInvoke(() =>
            {
                SettingsStore.Current.IslandEnabled = !SettingsStore.Current.IslandEnabled;
                SettingsStore.Save();
                Dbg.Log("hotkey toggled island to " + SettingsStore.Current.IslandEnabled);
                ApplySettings();
            });
        }
        return IntPtr.Zero;
    }

    // ---- clock / weather ----
    private void UpdateClock()
    {
        var s = SettingsStore.Current;
        var now = DateTime.Now;
        string fmt = s.Use24Hour ? (s.ShowSeconds ? "HH:mm:ss" : "HH:mm") : (s.ShowSeconds ? "h:mm:ss tt" : "h:mm tt");
        ClockText.Text = s.ShowTime ? now.ToString(fmt) : now.ToString("ddd d MMM");
        UpdateTimer();
    }

    private void UpdateWeather(WeatherInfo w)
    {
        WeatherGlyph.Text = w.Icon;
        WeatherText.Text = WeatherService.FormatTemp(w.TempC);
        WeatherCond.Text = string.IsNullOrEmpty(w.Place) ? w.Condition : $"{w.Condition} · {w.Place}";
    }

    /// <summary>Idle pill shows weather, or the compact song pill while music plays.</summary>
    private void RefreshIdleMode()
    {
        bool playing = track?.Playing == true;
        bool showWx = SettingsStore.Current.ShowWeather && !playing;
        MiniMusic.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        WeatherGroup.Visibility = showWx ? Visibility.Visible : Visibility.Collapsed;
        MusicDot.Visibility = track != null && !playing ? Visibility.Visible : Visibility.Collapsed;
        if (playing && track != null)
        {
            MiniTitle.Text = string.IsNullOrWhiteSpace(track.Title) ? "Unknown title" : track.Title;
            UpdateMiniLeft();
        }
    }

    private void UpdateMiniLeft()
    {
        if (track == null) return;
        var left = dur - shown;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        MiniLeft.Text = "−" + MediaService.Fmt(left);
    }

    // ---- countdown timer (native island timer: Windows exposes no API
    // for the Clock app's timers, so the island runs its own) ----
    private DateTime? timerEndsAt;
    private TimeSpan timerTotal;

    public bool IsTimerRunning => timerEndsAt.HasValue && timerEndsAt.Value > DateTime.UtcNow;
    public TimeSpan TimerRemaining => IsTimerRunning ? timerEndsAt!.Value - DateTime.UtcNow : TimeSpan.Zero;

    public void StartTimer(TimeSpan d)
    {
        if (d <= TimeSpan.Zero || d.TotalHours > 24) return;
        timerTotal = d;
        timerEndsAt = DateTime.UtcNow + d;
        SettingsStore.Current.TimerMinutes = d.TotalMinutes;
        SettingsStore.Current.TimerEndsAtUtc = timerEndsAt;
        SettingsStore.Save();
        UpdateTimer();
        MorphToContent();
    }

    public void CancelTimer()
    {
        timerEndsAt = null;
        SettingsStore.Current.TimerEndsAtUtc = null;
        SettingsStore.Save();
        UpdateTimer();
        MorphToContent();
    }

    private void ResumeTimer()
    {
        var ends = SettingsStore.Current.TimerEndsAtUtc;
        if (ends.HasValue && ends.Value > DateTime.UtcNow)
        {
            timerEndsAt = ends.Value;
            timerTotal = TimeSpan.FromMinutes(Math.Clamp(SettingsStore.Current.TimerMinutes, 1, 1440));
            UpdateTimer();
        }
        else if (ends.HasValue)
        {
            SettingsStore.Current.TimerEndsAtUtc = null;
            SettingsStore.Save();
        }
    }

    private void UpdateTimer()
    {
        if (IsTimerRunning)
        {
            TimerGroup.Visibility = Visibility.Visible;
            TimerText.Text = MediaService.Fmt(TimerRemaining);
        }
        else
        {
            if (timerEndsAt.HasValue) FinishTimer(); // reached zero since last tick
            TimerGroup.Visibility = Visibility.Collapsed;
        }
    }

    private void FinishTimer()
    {
        timerEndsAt = null;
        SettingsStore.Current.TimerEndsAtUtc = null;
        SettingsStore.Save();
        _ = Task.Run(() =>
        {
            try { for (int i = 0; i < 3; i++) { Console.Beep(880, 320); Thread.Sleep(180); } } catch { }
        });
        OnNotif(new IslandNotification("Timer", "Time's up",
            $"Your {MediaService.Fmt(timerTotal)} timer finished.", DateTime.Now));
    }

    // ---- media ----
    private void OnTrack(TrackInfo? t)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // Music integration off means "act as if nothing is playing".
            if (!SettingsStore.Current.MusicEnabled) t = null;
            bool wasPlaying = track?.Playing == true;
            track = t;
            if (t != null)
            {
                // Identity excludes duration: Spotify can report it as 0 briefly
                // or revise it, which must never look like a track change.
                string key = $"{t.Title}\0{t.Artist}\0{t.AppId}";
                bool isNew = key != trackKey;
                trackKey = key;
                if (t.Duration > TimeSpan.Zero) dur = t.Duration;

                if (isNew || !t.Playing)
                {
                    // New song or pause: trust the server sample exactly.
                    shown = ClampPos(t.Position, dur);
                    behindCount = 0;
                }
                else
                {
                    // Same song still playing: our local clock is smoother than the
                    // server's cadence. Forward jumps = real seek forward, snap at
                    // once. Backward jumps are usually stale samples, so demand
                    // confirmation (or a recent button press) before snapping.
                    double diff = (t.Position - shown).TotalSeconds;
                    if (diff > 2.5) { shown = ClampPos(t.Position, dur); behindCount = 0; }
                    else if (diff < -2.5)
                    {
                        behindCount++;
                        if (DateTime.UtcNow < forceSnapUntil || behindCount >= 2)
                        { shown = ClampPos(t.Position, dur); behindCount = 0; }
                    }
                    else behindCount = 0;
                }
                lastTick = DateTime.UtcNow;

                if (isNew)
                {
                    SongTitle.Text = string.IsNullOrWhiteSpace(t.Title) ? "Unknown title" : t.Title;
                    SongArtist.Text = string.IsNullOrWhiteSpace(t.Artist) ? FriendlyApp(t.AppId) : t.Artist;
                    SrcText.Text = FriendlyApp(t.AppId);
                    if (SettingsStore.Current.ShowArtwork && t.Art != null)
                    {
                        // Crossfade the artwork instead of swapping it instantly.
                        ArtImage.Source = t.Art;
                        ArtImage.BeginAnimation(OpacityProperty, null);
                        ArtImage.Opacity = 0;
                        ArtImage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, ScaledMs(300))
                        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                    }
                    else ArtImage.Source = SettingsStore.Current.ShowArtwork ? t.Art : null;
                }
                if (isNew || lastPlaying != t.Playing) { SetPlayIcon(t.Playing); lastPlaying = t.Playing; }

                RefreshIdleMode();
                UpdateProgressText();

                if (current != State.Notification && t.Playing && (!wasPlaying || isNew))
                {
                    // Expand for the new event, then settle back to the compact pill.
                    Show(State.Music);
                    musicTimer.Stop(); musicTimer.Start();
                }
                else if (!t.Playing && current == State.Music && !hovered)
                {
                    musicTimer.Stop();
                    Show(State.Idle);
                }
            }
            else
            {
                trackKey = "";
                musicTimer.Stop();
                RefreshIdleMode();
                if (current == State.Music) Show(State.Idle);
            }
        });
    }

    private static TimeSpan ClampPos(TimeSpan p, TimeSpan d)
    {
        if (p < TimeSpan.Zero) return TimeSpan.Zero;
        if (d > TimeSpan.Zero && p > d) return d;
        return p;
    }

    private static string FriendlyApp(string id)
    {
        if (id.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        if (id.Contains("chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (id.Contains("edge", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (id.Contains("firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        var parts = id.Split('.'); return parts.Length > 0 ? parts[^1] : id;
    }

    private void UpdateProgress()
    {
        if (track == null) return;
        var now = DateTime.UtcNow;
        // Monotonic local clock: always moves forward while playing, never jumps back.
        if (track.Playing)
            shown = ClampPos(shown + (now - lastTick), dur);
        lastTick = now;
        UpdateProgressText();
    }

    private void UpdateProgressText()
    {
        if (track == null) return;
        double total = Math.Max(1, dur.TotalSeconds);
        double targetVal = Math.Clamp(shown.TotalSeconds / total * 100, 0, 100);
        if (track.Playing && MusicView.Visibility == Visibility.Visible &&
            Math.Abs(Progress.Value - targetVal) > 0.05)
        {
            // Glide the bar instead of stepping it 4x/sec.
            Progress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty,
                new DoubleAnimation(targetVal, ScaledMs(300)));
        }
        else Progress.Value = targetVal;
        PosText.Text = MediaService.Fmt(shown);
        DurText.Text = MediaService.Fmt(dur);
        if (track.Playing) UpdateMiniLeft();
    }

    /// <summary>Swaps the play/pause button between Lucide vector icons.</summary>
    private void SetPlayIcon(bool playing)
    {
        PlayBtn.Content = new System.Windows.Shapes.Path
        {
            Data = (Geometry)FindResource(playing ? "LucidePause" : "LucidePlay"),
            Stroke = System.Windows.Media.Brushes.White,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform,
        };
    }

    private async void Play_Click(object s, RoutedEventArgs e) { forceSnapUntil = DateTime.UtcNow.AddSeconds(3); await media.ToggleAsync(); }
    private async void Next_Click(object s, RoutedEventArgs e) { forceSnapUntil = DateTime.UtcNow.AddSeconds(3); await media.NextAsync(); }
    private async void Prev_Click(object s, RoutedEventArgs e) { forceSnapUntil = DateTime.UtcNow.AddSeconds(3); await media.PrevAsync(); }

    // ---- notifications ----
    public void PreviewNotification() => OnNotif(new IslandNotification(
        "Preview App", "Hello from DynIsland", "This is how notifications will look.", DateTime.Now), force: true);

    private void OnNotif(IslandNotification n, bool force = false)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnNotif(n, force)); return; }
        Dbg.Log($"island received app=[{n.AppName}] title=[{n.Title}]");
        if (!SettingsStore.Current.NotificationsEnabled && !force) return;
        NotifApp.Text = n.AppName;
        NotifTitle.Text = string.IsNullOrWhiteSpace(n.Title) ? "(no title)" : n.Title;
        NotifBody.Text = n.Body;
        NotifLetter.Text = string.IsNullOrEmpty(n.AppName) ? "✉" : n.AppName.Trim()[..1].ToUpper();
        Show(State.Notification);
        notifTimer.Stop(); notifTimer.Start();
    }

    private void ShowForTrackOrIdle()
    {
        if (track != null && track.Playing && SettingsStore.Current.MusicEnabled) Show(State.Music);
        else Show(State.Idle);
    }

    private void HoverExpand(bool hover)
    {
        hovered = hover;
        AnimateHoverChrome(hover);
        if (hover)
        {
            musicTimer.Stop();
            if (current == State.Notification) return;
            if (track != null) { Show(State.Music); return; }
            var w = weather.Last;
            IdleDetail.Text = DateTime.Now.ToString("dddd, MMM d") +
                (w != null ? $"  ·  {w.Condition} {WeatherService.FormatTemp(w.TempC)}" +
                 (string.IsNullOrEmpty(w.Place) ? "" : $" · {w.Place}") : "") +
                (IsTimerRunning ? $"  ·  ⏱ {MediaService.Fmt(TimerRemaining)} left" : "");
            FadeDetail(true);
        }
        else
        {
            if (current == State.Music) { FadeDetail(false, instant: true); Show(State.Idle); }
            else FadeDetail(false);
        }
    }

    /// <summary>Hover chrome: the pill dips down a few px and its border
    /// glows brighter. Translate + brush color stay inside the window bounds,
    /// so unlike a scale-up they are never clipped by SizeToContent.</summary>
    private void AnimateHoverChrome(bool hover)
    {
        try
        {
        var dur = ScaledMs(260);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        HoverDip.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(HoverDip.Y, hover ? 5 : 0, dur) { EasingFunction = ease });
        // BorderBrush from XAML is frozen: swap in a mutable copy once, then animate its color.
        if (Pill.BorderBrush.IsFrozen)
            Pill.BorderBrush = new SolidColorBrush(((SolidColorBrush)Pill.BorderBrush).Color);
        if (Pill.BorderBrush is SolidColorBrush border)
        {
            var glow = new ColorAnimation(
                border.Color,
                hover ? System.Windows.Media.Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF) : System.Windows.Media.Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF),
                (Duration)dur) { EasingFunction = ease };
            border.BeginAnimation(SolidColorBrush.ColorProperty, glow);
        }
        }
        catch (Exception ex) { Dbg.Log("hover chrome failed: " + ex.Message); }
    }

    private int detailGen; // guards stale detail fade completions
    /// <summary>Fades/slides the idle hover detail row in or out. The pill
    /// morph runs alongside, so growth and reveal feel like one motion.
    /// Generation-guarded: hover spam can only redirect, never strand it.</summary>
    private void FadeDetail(bool show, bool instant = false)
    {
        int g = ++detailGen;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (show)
        {
            IdleDetail.Visibility = Visibility.Visible;
            MorphToContent();
            IdleDetail.BeginAnimation(OpacityProperty, null);
            DetailSlide.BeginAnimation(TranslateTransform.YProperty, null);
            IdleDetail.BeginAnimation(OpacityProperty,
                new DoubleAnimation(Math.Max(IdleDetail.Opacity, 0), 1, ScaledMs(260)) { EasingFunction = ease });
            DetailSlide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(DetailSlide.Y, 0, ScaledMs(260)) { EasingFunction = ease });
        }
        else
        {
            if (IdleDetail.Visibility != Visibility.Visible) return;
            if (instant)
            {
                IdleDetail.BeginAnimation(OpacityProperty, null);
                DetailSlide.BeginAnimation(TranslateTransform.YProperty, null);
                IdleDetail.Visibility = Visibility.Collapsed;
                IdleDetail.Opacity = 0;
                DetailSlide.Y = -4;
                return;
            }
            var fade = new DoubleAnimation(IdleDetail.Opacity, 0, ScaledMs(180)) { EasingFunction = ease };
            var slide = new DoubleAnimation(DetailSlide.Y, -4, ScaledMs(180)) { EasingFunction = ease };
            fade.Completed += (_, _) =>
            {
                if (g != detailGen) return;
                IdleDetail.Visibility = Visibility.Collapsed;
                DetailSlide.Y = -4;
                MorphToContent();
            };
            IdleDetail.BeginAnimation(OpacityProperty, fade);
            DetailSlide.BeginAnimation(TranslateTransform.YProperty, slide);
            // Shrink the pill immediately while the text fades: single coordinated motion.
            MorphToContent();
        }
    }

    /// <summary>Shows/hides the hover detail row, morphing only on real change.</summary>
    private void SetIdleDetail(bool show)
    {
        FadeDetail(show);
    }

    // ---- state + smooth morph animation ----
    // Slow two-beat transition: the outgoing view breathes out first, then the
    // pill morphs while the new view fades in across the full travel.
    // Atomic (one view visible at a time) + generation-guarded, so hover spam
    // can only ever redirect the motion, never stack or strand it.
    private int animGen;  // invalidates stale fade completions
    private int morphGen; // guards the size-animation release below
    private readonly DispatcherTimer idleDefer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private void Show(State s)
    {
        // Collapse is debounced: a 400 ms linger kills rapid Music<->Idle
        // strobing from flapping playback state. Expands and notifications
        // cancel the wait and act immediately.
        if (s == State.Idle && current != State.Idle)
        {
            idleDefer.Stop(); idleDefer.Start();
            return;
        }
        idleDefer.Stop();
        DoShow(s);
    }

    private void DoShow(State s)
    {
        if (current == s) return;
        int g = ++animGen;
        var from = FrontView();
        current = s;
        if (from == null) { SwapFinal(s); return; }
        from.BeginAnimation(OpacityProperty, null);
        var fadeOut = new DoubleAnimation(from.Opacity, 0, ScaledMs(150))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        fadeOut.Completed += (_, _) =>
        {
            if (g != animGen) return; // superseded by a newer transition
            SwapFinal(s);
        };
        from.BeginAnimation(OpacityProperty, fadeOut);
    }

    private FrameworkElement? FrontView()
    {
        if (IdleView.Visibility == Visibility.Visible) return IdleView;
        if (MusicView.Visibility == Visibility.Visible) return MusicView;
        if (NotifView.Visibility == Visibility.Visible) return NotifView;
        return null;
    }

    private void SwapFinal(State s)
    {
        var target = ViewOf(s);
        IdleView.Visibility = s == State.Idle ? Visibility.Visible : Visibility.Collapsed;
        MusicView.Visibility = s == State.Music ? Visibility.Visible : Visibility.Collapsed;
        NotifView.Visibility = s == State.Notification ? Visibility.Visible : Visibility.Collapsed;
        foreach (var v in new FrameworkElement[] { IdleView, MusicView, NotifView })
            if (v.Visibility != Visibility.Visible) v.Opacity = 1;
        target.Opacity = 0;
        MorphToContent(animateContent: true);
    }

    private FrameworkElement ViewOf(State s) => s switch
    {
        State.Idle => IdleView, State.Music => MusicView, _ => NotifView
    };

    /// <summary>
    /// Smoothly morphs the window between sizes instead of jumping:
    /// freeze current size, swap content, measure target, animate W/H, release.
    /// Restart-safe: a newer morph replaces the animations, and only the newest
    /// completion releases the explicit size — stale ones are ignored.
    /// </summary>
    private void MorphToContent(bool animateContent = false)
    {
        var dur = ScaledMs(420);
        // EaseInOut on size: gentle at both ends instead of Quartic's hard launch.
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        if (!IsLoaded || PresentationSource.FromVisual(this) == null)
        {
            if (animateContent)
            {
                var vv = ViewOf(current);
                vv.Opacity = 1;
            }
            PositionTopCenter();
            return;
        }

        double fromW = ActualWidth, fromH = ActualHeight;
        if (fromW <= 0 || fromH <= 0) { PositionTopCenter(); return; }

        // Freeze at current size (overrides SizeToContent while set).
        Width = fromW; Height = fromH;
        UpdateLayout();

        // Target = pill's desired size + outer grid padding (0 sides, 6 top + 14 bottom).
        // No Left animation: SizeChanged recenters every frame, so the pill's
        // center never deviates and growth is perfectly symmetric in place.
        Pill.Measure(new System.Windows.Size(Pill.MaxWidth, double.PositiveInfinity));
        double toW = Math.Min(Pill.DesiredSize.Width, Pill.MaxWidth);
        double toH = Pill.DesiredSize.Height + 20;

        // Already there: skip the animation entirely (avoids restart flicker).
        if (Math.Abs(fromW - toW) < 0.5 && Math.Abs(fromH - toH) < 0.5)
        {
            Width = double.NaN; Height = double.NaN;
            if (animateContent) MorphReveal(ViewOf(current));
            CenterNow();
            return;
        }

        int mg = ++morphGen;
        var wAnim = new DoubleAnimation(fromW, toW, dur) { EasingFunction = ease };
        var hAnim = new DoubleAnimation(fromH, toH, dur) { EasingFunction = ease };
        int done = 0;
        void Finished(object? _, EventArgs __)
        {
            if (mg != morphGen) return; // superseded: a newer morph owns the size
            if (System.Threading.Interlocked.Increment(ref done) < 2) return;
            // Release back to auto-size so future content changes measure correctly.
            Width = double.NaN; Height = double.NaN;
            CenterNow();
        }
        wAnim.Completed += Finished;
        hAnim.Completed += Finished;
        BeginAnimation(WidthProperty, wAnim);
        BeginAnimation(HeightProperty, hAnim);

        if (animateContent)
            MorphReveal(current switch
            {
                State.Idle => IdleView, State.Music => MusicView, _ => NotifView
            });
    }

    /// <summary>Entrance for the incoming view: fades in while rising 8px
    /// into place, across the whole pill morph. Fades from the current
    /// opacity so a restarted reveal never flashes.</summary>
    private void MorphReveal(FrameworkElement v)
    {
        var reveal = ScaledMs(420);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var shift = ShiftOf(v);
        double from = v.Opacity;
        if (from < 1)
            v.BeginAnimation(OpacityProperty, new DoubleAnimation(from, 1, reveal) { EasingFunction = ease });
        // The rise is skipped without GPU compositing: translate+fade+resize
        // at once is what stutters most in software rendering.
        if (shift != null && !FastAnims)
        {
            shift.BeginAnimation(TranslateTransform.YProperty, null);
            shift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(Math.Max(shift.Y, 0) + 8, 0, reveal) { EasingFunction = ease });
        }
        else shift?.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private TranslateTransform? ShiftOf(FrameworkElement v)
    {
        if (v == IdleView) return IdleShift;
        if (v == MusicView) return MusicShift;
        if (v == NotifView) return NotifShift;
        return null;
    }

    private void AnimateIn()
    {
        Opacity = 0;
        var f = new DoubleAnimation(0, 1, ScaledMs(450)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        BeginAnimation(OpacityProperty, f);
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h, int id);
    private const int HOTKEY_ID = 0xD171;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    protected override void OnClosed(EventArgs e)
    {
        try { UnregisterHotKey(new WindowInteropHelper(this).Handle, HOTKEY_ID); } catch { }
        cts.Cancel(); media.Dispose(); base.OnClosed(e);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int n);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int n, int v);
}
