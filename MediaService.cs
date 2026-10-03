using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media.Imaging;
using Windows.Foundation;
using Windows.Media.Control;

namespace DynIsland;

public record TrackInfo(string Title, string Artist, string AppId, TimeSpan Position, TimeSpan Duration, bool Playing, BitmapImage? Art, DateTime Stamp);

public class MediaService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? mgr;
    public event Action<TrackInfo?>? Changed;
    private System.Threading.Timer? poll;
    private string artKey = "";
    private BitmapImage? artCache;
    private bool started;

    public async Task StartAsync()
    {
        if (started) { await PushAsync(); return; }
        started = true;
        try { mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
        catch { started = false; return; }
        if (mgr == null) { started = false; return; }
        mgr.SessionsChanged += (_, _) => _ = PushAsync();
        mgr.CurrentSessionChanged += (_, _) => _ = PushAsync();
        HookSession(mgr.GetCurrentSession());
        // Backstop poll only; session events push updates on their own.
        poll = new System.Threading.Timer(_ => _ = PushAsync(), null, 3000, 3000);
        await PushAsync();
    }

    private GlobalSystemMediaTransportControlsSession? current;
    private void HookSession(GlobalSystemMediaTransportControlsSession? s)
    {
        if (current != null)
        {
            current.MediaPropertiesChanged -= OnMediaProp;
            current.PlaybackInfoChanged -= OnPlayback;
            current.TimelinePropertiesChanged -= OnTimeline;
        }
        current = s;
        if (current != null)
        {
            current.MediaPropertiesChanged += OnMediaProp;
            current.PlaybackInfoChanged += OnPlayback;
            current.TimelinePropertiesChanged += OnTimeline;
        }
    }
    private void OnMediaProp(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs a) => _ = PushAsync();
    private void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs a) => _ = PushAsync();
    private void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs a) => _ = PushAsync();

    private async Task PushAsync()
    {
        try
        {
            if (mgr == null) return;
            var sess = mgr.GetCurrentSession();
            HookSession(sess);
            if (sess == null) { Changed?.Invoke(null); return; }
            // Sample playback state FIRST: artwork decoding below awaits I/O and
            // would otherwise make the position sample stale by the time we send it.
            var info = sess.GetPlaybackInfo();
            var time = sess.GetTimelineProperties();
            bool playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            TimeSpan pos = time.Position, dur = time.EndTime - time.StartTime;
            var props = await sess.TryGetMediaPropertiesAsync();
            string title = props.Title ?? "", artist = props.Artist ?? "";
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist)) { Changed?.Invoke(null); return; }
            BitmapImage? art = null;
            string key = $"{title}\0{artist}";
            try
            {
                // Decode artwork once per track; reusing the cached image also
                // avoids flicker from reassigning Image.Source every poll.
                if (SettingsStore.Current.ShowArtwork && props.Thumbnail != null && key != artKey)
                {
                    using var ras = await props.Thumbnail.OpenReadAsync();
                    using var ms = new MemoryStream();
                    using var ns = ras.AsStreamForRead();
                    await ns.CopyToAsync(ms); ms.Seek(0, SeekOrigin.Begin);
                    var bmp = new BitmapImage();
                    bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.DecodePixelWidth = 96; bmp.StreamSource = ms; bmp.EndInit(); bmp.Freeze();
                    artCache = bmp; artKey = key;
                }
                art = key == artKey ? artCache : null;
            }
            catch { }
            Changed?.Invoke(new TrackInfo(title, artist, sess.SourceAppUserModelId, pos, dur, playing, art, DateTime.UtcNow));
        }
        catch { }
    }

    public async Task ToggleAsync() { try { var s = mgr?.GetCurrentSession(); if (s != null) await s.TryTogglePlayPauseAsync(); } catch { } }
    public async Task NextAsync() { try { var s = mgr?.GetCurrentSession(); if (s != null) await s.TrySkipNextAsync(); } catch { } }
    public async Task PrevAsync() { try { var s = mgr?.GetCurrentSession(); if (s != null) await s.TrySkipPreviousAsync(); } catch { } }

    public void Dispose() => poll?.Dispose();

    public static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{(int)t.Minutes:00}:{(int)t.Seconds:00}" : $"{(int)t.Minutes}:{(int)t.Seconds:00}";
}
