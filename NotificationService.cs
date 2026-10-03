using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynIsland;

public record IslandNotification(string AppName, string Title, string Body, DateTime Time);

public class NotificationService
{
    public event Action<IslandNotification>? Arrived;

    private string lastKey = "";
    private DateTime lastAt = DateTime.MinValue;
    private bool started;

    public async Task StartAsync()
    {
        Dbg.Log($"start enabled={SettingsStore.Current.NotificationsEnabled}");
        if (!SettingsStore.Current.NotificationsEnabled) return;
        if (started) return; // idempotent: safe to call from ApplySettings
        started = true;

        // Path 1 (best effort): official API. Requires package identity +
        // userNotificationListener capability, so it silently fails for a plain
        // unpackaged exe — the UIA watcher below is the real workhorse.
        UserNotificationListener? listener = null;
        try
        {
            listener = UserNotificationListener.Current;
            var access = await listener.RequestAccessAsync();
            Dbg.Log($"UserNotificationListener access={access}");
            if (access == UserNotificationListenerAccessStatus.Allowed)
            {
                try { listener.NotificationChanged += OnChanged; Dbg.Log("UserNotificationListener subscribed"); }
                catch (Exception ex)
                {
                    Dbg.Log("event subscribe failed, using poll fallback: " + ex.ToString().Split('\n')[0]);
                    _ = Task.Run(() => PollLoop(listener));
                }
            }
        }
        catch (Exception ex) { Dbg.Log("UserNotificationListener failed: " + ex.ToString()); }

        // Path 2: watch toast popup windows via UI Automation. No special
        // access required; works for any unpackaged desktop app.
        // Two halves: event watcher (instant) + 1.2 s top-level snapshot poll
        // (catches toasts whose open event we missed, or windows the shell reuses).
        try { _ = Task.Run(WatchToasts); } catch (Exception ex) { Dbg.Log("WatchToasts launch failed: " + ex.Message); }
        try { _ = Task.Run(ToastPollLoop); } catch (Exception ex) { Dbg.Log("ToastPollLoop launch failed: " + ex.Message); }
    }

    // ---------- Path 1: UserNotificationListener ----------
    private readonly HashSet<uint> seen = new();
    private readonly object gate = new();

    private void OnChanged(UserNotificationListener sender, UserNotificationChangedEventArgs e)
    {
        try
        {
            if (e.ChangeKind != UserNotificationChangedKind.Added) return;
            var n = sender.GetNotification(e.UserNotificationId);
            if (n == null) return;
            lock (gate)
            {
                if (!seen.Add(n.Id)) return;
                if (seen.Count > 200) seen.Clear();
            }
            ParseAndEmit(n);
        }
        catch { }
    }

    // ---------- Path 1b: poll fallback (event subscription needs package identity) ----------
    private async Task PollLoop(UserNotificationListener listener)
    {
        bool baselined = false;
        while (true)
        {
            try
            {
                await Task.Delay(3000);
                IReadOnlyList<UserNotification> list;
                try { list = await listener.GetNotificationsAsync(NotificationKinds.Toast); }
                catch (Exception ex) { Dbg.Log("poll query failed: " + ex.ToString().Split('\n')[0]); return; }
                int fresh = 0;
                foreach (var n in list)
                {
                    bool dup;
                    lock (gate)
                    {
                        dup = !seen.Add(n.Id);
                        if (seen.Count > 500) seen.Clear();
                    }
                    if (dup) continue;
                    if (!baselined) continue; // first pass: baseline, don't dump history
                    fresh++;
                    ParseAndEmit(n);
                }
                baselined = true;
                if (fresh > 0) Dbg.Log($"poll: {list.Count} total, {fresh} new");
            }
            catch (Exception ex) { Dbg.Log("poll loop: " + ex.ToString().Split('\n')[0]); return; }
        }
    }

    private void ParseAndEmit(UserNotification n)
    {
        try
        {
            string title = "", body = "";
            try
            {
                var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                if (binding != null)
                {
                    var texts = binding.GetTextElements().Select(t => t.Text ?? "").Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
                    if (texts.Count > 0) title = texts[0];
                    if (texts.Count > 1) body = string.Join("  ·  ", texts.Skip(1));
                }
            }
            catch { }
            string app = "Notification";
            try { app = n.AppInfo?.DisplayInfo?.DisplayName ?? app; } catch { }
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body)) return;
            if (app.Contains("DynIsland", StringComparison.OrdinalIgnoreCase)) return;
            Emit(app, title, body);
        }
        catch { }
    }
    // ---------- Path 2: UI Automation toast watcher ----------
    private void WatchToasts()
    {
        try
        {
            Automation.AddAutomationEventHandler(
                WindowPattern.WindowOpenedEvent,
                AutomationElement.RootElement,
                TreeScope.Children,
                OnWindowOpened);
            // Backup trigger: some shells surface popups via structure changes.
            Automation.AddStructureChangedEventHandler(
                AutomationElement.RootElement,
                TreeScope.Children,
                OnWindowOpened);
            Dbg.Log("uia watchers registered");
        }
        catch (Exception ex) { Dbg.Log("uia watchers failed: " + ex.Message); }
    }

    private void OnWindowOpened(object? sender, AutomationEventArgs e)
    {
        try
        {
            if (sender is not AutomationElement el) return;
            // Read on a worker thread after a beat: content populates just after open.
            _ = Task.Run(async () =>
            {
                await Task.Delay(450);
                TryReadToast(el);
            });
        }
        catch { }
    }

    /// <returns>true when the window yielded toast-shaped content (consumed).</returns>
    private bool TryReadToast(AutomationElement el)
    {
        try
        {
            string cls;
            System.Windows.Rect rect;
            int pid;
            try
            {
                cls = el.Current.ClassName ?? "";
                rect = el.Current.BoundingRectangle;
                pid = el.Current.ProcessId;
            }
            catch { return false; } // window already gone

            string proc;
            try { proc = Process.GetProcessById(pid).ProcessName; }
            catch { return false; }

            // Log every new small window so we can see what toasts look like here.
            if (rect.Width < 700 && rect.Height < 600)
                Dbg.Log($"window class={cls} proc={proc} size={rect.Width:F0}x{rect.Height:F0}");

            // Toast popups are small borderless XAML windows. Win10 uses
            // CoreWindow; Win11 may host them in an Explorer XAML island.
            bool toastCls = cls == "Windows.UI.Core.CoreWindow"
                || cls.Contains("XamlExplorerHost", StringComparison.OrdinalIgnoreCase);
            if (!toastCls) return false;
            if (rect.Width < 150 || rect.Width > 650 || rect.Height < 40 || rect.Height > 550) return false;

            var texts = el.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                .Cast<AutomationElement>()
                .Select(a => { try { return (a.Current.Name ?? "").Trim(); } catch { return ""; } })
                .Where(t => t.Length > 0 && t.Length <= 300)
                .Distinct()
                .ToList();
            Dbg.Log($"toast candidate proc={proc} texts={texts.Count} first=[{string.Join(" | ", texts.Take(3))}]");
            if (texts.Count < 2) return false;

            // Trailing timestamp ("10:42 AM", "now") is chrome, not content.
            if (IsTimestamp(texts[^1])) texts.RemoveAt(texts.Count - 1);
            if (texts.Count < 2) return false;

            string app = texts[0];
            string title = texts.Count > 1 ? texts[1] : "";
            string body = texts.Count > 2 ? string.Join("  ·  ", texts.Skip(2)) : "";
            if (app.Contains("DynIsland", StringComparison.OrdinalIgnoreCase)) return true;
            Emit(app, title, body);
            return true;
        }
        catch { return false; }
    }

    // ---------- Path 2b: snapshot poll — catches toasts the event watcher missed ----------
    private readonly Dictionary<IntPtr, DateTime> toastHandles = new();
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc f, IntPtr p);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private async Task ToastPollLoop()
    {
        while (true)
        {
            try { await Task.Delay(1200); ScanTopLevel(); }
            catch { }
        }
    }

    /// <summary>Snapshots top-level windows and reads any toast-shaped popup
    /// docked bottom-right (where Win10/11 show notification toasts).
    /// Geometry-first so it works whatever host class the shell uses.</summary>
    private void ScanTopLevel()
    {
        try
        {
            var wa = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea;
            if (wa == null) return;
            var wins = new List<IntPtr>();
            EnumWindows((h, _) => { wins.Add(h); return true; }, IntPtr.Zero);
            foreach (var h in wins)
            {
                try
                {
                    if (!IsWindowVisible(h)) continue;
                    if (!GetWindowRect(h, out var r)) continue;
                    int w = r.Right - r.Left, hh = r.Bottom - r.Top;
                    if (w < 150 || w > 650 || hh < 40 || hh > 550) continue;
                    // Toasts dock bottom-right above the taskbar; ignore everything else.
                    if (r.Right < wa.Value.Right - 100) continue;
                    if (r.Bottom > wa.Value.Bottom + 10 || r.Bottom < wa.Value.Bottom - 800) continue;
                    GetWindowThreadProcessId(h, out uint pid);
                    if (pid == (uint)Environment.ProcessId) continue; // our own island
                    lock (toastHandles)
                    {
                        if (toastHandles.TryGetValue(h, out var t) && (DateTime.UtcNow - t).TotalSeconds < 90) continue;
                    }
                    AutomationElement? el;
                    try { el = AutomationElement.FromHandle(h); }
                    catch { continue; }
                    if (el == null) continue;
                    if (TryReadToast(el))
                    {
                        lock (toastHandles)
                        {
                            toastHandles[h] = DateTime.UtcNow;
                            if (toastHandles.Count > 300) toastHandles.Clear();
                        }
                    }
                    // else: content not populated yet — retry on the next sweep.
                }
                catch { }
            }
        }
        catch { }
    }

    private static bool IsTimestamp(string t) =>
        t.Equals("now", StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(t, @"^\d{1,2}:\d{2}(\s?[AP]\.?M\.?)?$", RegexOptions.IgnoreCase);

    // ---------- shared emit with cross-source dedupe ----------
    private void Emit(string app, string title, string body)
    {
        app = Trunc(app.Trim(), 28);
        title = Trunc(title.Trim(), 80);
        body = Trunc(body.Trim(), 140);
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body)) return;
        string key = $"{app}|{title}|{body}";
        var now = DateTime.UtcNow;
        lock (gate)
        {
            if (key == lastKey && (now - lastAt).TotalSeconds < 4) return;
            lastKey = key; lastAt = now;
        }
        Dbg.Log($"emit app=[{app}] title=[{title}]");
        App.Current?.Dispatcher.BeginInvoke(() =>
            Arrived?.Invoke(new IslandNotification(app, title, body, DateTime.Now)));
    }

    private static string Trunc(string s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= n ? s : s[..(n - 1)] + "…";
    }
}
