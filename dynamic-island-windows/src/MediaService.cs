using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace DynamicIsland;

/// <summary>
/// Reads "now playing" from Windows (the same source as the media flyout when you press a volume key),
/// so it works with Spotify, browsers, the Media Player app and so on.
/// The Changed event fires on a background thread; the UI hops back to its own thread.
/// </summary>
public sealed class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _artKey = "";

    // Song position as last reported by the app, and when it was reported.
    private TimeSpan _position;
    private TimeSpan _duration;
    private DateTimeOffset _positionReportedAt;

    public event Action? Changed;

    public bool HasSession => _session is not null;
    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public bool IsPlaying { get; private set; }
    public ImageSource? Art { get; private set; }

    /// <summary>Whether the playing app lets you switch shuffle / repeat from outside (Spotify does; most websites don't).</summary>
    public bool CanShuffle { get; private set; }
    public bool CanRepeat { get; private set; }
    public bool ShuffleOn { get; private set; }

    /// <summary>0 = off, 1 = repeat this song, 2 = repeat the list.</summary>
    public int RepeatMode { get; private set; }

    /// <summary>The app that's playing, e.g. "Spotify" or "Google Chrome".</summary>
    public string SourceApp { get; private set; } = "";

    /// <summary>True when the app reports how long the song is (most music apps do; some websites don't).</summary>
    public bool HasTimeline => _session is not null && _duration > TimeSpan.FromSeconds(1);
    public TimeSpan Duration => _duration;

    /// <summary>Where the song is now. Apps only report position occasionally, so we count forward while playing.</summary>
    public TimeSpan Position
    {
        get
        {
            var position = _position;
            if (IsPlaying && _positionReportedAt.Year > 2000)
            {
                var sinceReport = DateTimeOffset.Now - _positionReportedAt;
                if (sinceReport > TimeSpan.Zero && sinceReport < TimeSpan.FromHours(6)) position += sinceReport;
            }
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (position > _duration) position = _duration;
            return position;
        }
    }

    public async Task InitAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => _ = AttachAsync();
            await AttachAsync();
        }
        catch
        {
            // Media info is a nice-to-have; the island works fine without it.
        }
    }

    private async Task AttachAsync()
    {
        try
        {
            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _session.TimelinePropertiesChanged -= OnTimelineChanged;
            }

            _session = _manager?.GetCurrentSession();

            if (_session is not null)
            {
                _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                _session.TimelinePropertiesChanged += OnTimelineChanged;
            }

            await RefreshAsync();
        }
        catch { }
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => _ = RefreshAsync();
    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => _ = RefreshAsync();
    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => ReadTimeline(sender);

    // While a jump is being confirmed, older position reports from the app are ignored so the bar doesn't flick back.
    private DateTime _holdPositionUntil;

    private void ReadTimeline(GlobalSystemMediaTransportControlsSession session)
    {
        if (DateTime.Now < _holdPositionUntil) return;
        try
        {
            var timeline = session.GetTimelineProperties();
            if (timeline is null) return;
            _duration = timeline.EndTime - timeline.StartTime;
            _position = timeline.Position - timeline.StartTime;
            _positionReportedAt = timeline.LastUpdatedTime;
        }
        catch
        {
            _duration = TimeSpan.Zero;
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var session = _session;
            if (session is null)
            {
                Title = Artist = "";
                IsPlaying = false;
                CanShuffle = CanRepeat = ShuffleOn = false;
                RepeatMode = 0;
                Art = null;
                _artKey = "";
                _duration = TimeSpan.Zero;
                Changed?.Invoke();
                return;
            }

            SourceApp = FriendlyAppName(session.SourceAppUserModelId);
            var props = await session.TryGetMediaPropertiesAsync();
            Title = props?.Title ?? "";
            Artist = props?.Artist ?? "";

            var playback = session.GetPlaybackInfo();
            bool reportedPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            // Just after you press play/pause, some apps still report the old state for a moment; keep showing what you chose.
            if (DateTime.Now < _holdPlayStateUntil && reportedPlaying != _wantPlaying) reportedPlaying = _wantPlaying;
            else _holdPlayStateUntil = DateTime.MinValue;
            IsPlaying = reportedPlaying;
            CanShuffle = playback?.Controls?.IsShuffleEnabled == true;
            CanRepeat = playback?.Controls?.IsRepeatEnabled == true;
            ShuffleOn = playback?.IsShuffleActive == true;
            RepeatMode = playback?.AutoRepeatMode switch
            {
                Windows.Media.MediaPlaybackAutoRepeatMode.Track => 1,
                Windows.Media.MediaPlaybackAutoRepeatMode.List => 2,
                _ => 0,
            };
            ReadTimeline(session);

            // Only reload the artwork when the song actually changes.
            string key = Title + "\u0001" + Artist;
            if (key != _artKey)
            {
                _artKey = key;
                var thumbnail = props?.Thumbnail;
                Art = thumbnail is null ? null : await LoadArtAsync(thumbnail);
            }

            Changed?.Invoke();
        }
        catch
        {
            // The app playing media can vanish mid-request; the next event will sort it out.
        }
    }

    private static async Task<ImageSource?> LoadArtAsync(Windows.Storage.Streams.IRandomAccessStreamReference reference)
    {
        try
        {
            using var winStream = await reference.OpenReadAsync();
            using var stream = winStream.AsStreamForRead();
            var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            memory.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 160;
            bitmap.StreamSource = memory;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static string FriendlyAppName(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        string lower = id.ToLowerInvariant();
        if (lower.Contains("spotify")) return "Spotify";
        if (lower.Contains("msedge")) return "Microsoft Edge";
        if (lower.Contains("chrome")) return "Google Chrome";
        if (lower.Contains("firefox") || lower == "308046b0af4a39cb") return "Firefox";
        if (lower.Contains("zunemusic") || lower.Contains("mediaplayer")) return "Media Player";
        if (lower.Contains("vlc")) return "VLC";

        // Fall back to something readable: "Foo.exe" -> "Foo", "Company.App_xyz!App" -> "App".
        string name = id;
        int bang = name.IndexOf('!');
        if (bang > 0) name = name[..bang];
        int underscore = name.IndexOf('_');
        if (underscore > 0) name = name[..underscore];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        int dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        return name;
    }

    private DateTime _holdPlayStateUntil;
    private bool _wantPlaying;

    /// <summary>
    /// Plays or pauses, based on what the button showed when you clicked it. Sends exactly one request
    /// (play, or pause) and nothing else: apps like Spotify report their state late, so checking and retrying
    /// would undo what you asked for.
    /// </summary>
    public async void PlayPause()
    {
        var session = _session;
        bool wantPlaying = !IsPlaying;

        // Freeze the song position where it is right now, so the time doesn't jump back to an old report.
        _position = Position;
        _positionReportedAt = DateTimeOffset.Now;
        _holdPositionUntil = DateTime.Now.AddSeconds(2);

        _wantPlaying = wantPlaying;
        _holdPlayStateUntil = DateTime.Now.AddSeconds(4);
        IsPlaying = wantPlaying;
        Changed?.Invoke();

        if (session is null)
        {
            Native.PressMediaKey(Native.VK_MEDIA_PLAY_PAUSE);
            return;
        }

        try
        {
            bool answer = wantPlaying ? await session.TryPlayAsync() : await session.TryPauseAsync();
            if (!answer) App.LogNote($"media {(wantPlaying ? "play" : "pause")}: '{SourceApp}' ({session.SourceAppUserModelId}) answered no");
        }
        catch (Exception ex)
        {
            App.LogNote($"media play/pause threw for '{SourceApp}': {ex.GetType().Name}: {ex.Message} — pressed the media key");
            Native.PressMediaKey(Native.VK_MEDIA_PLAY_PAUSE);
        }
    }

    private static bool? ReadIsPlaying(GlobalSystemMediaTransportControlsSession? session)
    {
        try
        {
            var status = session?.GetPlaybackInfo()?.PlaybackStatus;
            return status is null ? null : status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch { return null; }
    }

    public async void Next() => await SkipAsync(s => s.TrySkipNextAsync(), Native.VK_MEDIA_NEXT_TRACK, "next");
    public async void Previous() => await SkipAsync(s => s.TrySkipPreviousAsync(), Native.VK_MEDIA_PREV_TRACK, "previous");

    /// <summary>
    /// Next / previous. If the app says no, waits to see whether the song changes anyway (some apps say no but do it);
    /// only if it doesn't is the keyboard media key pressed, so a song is never skipped twice.
    /// </summary>
    private async Task SkipAsync(Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> command,
                                 byte mediaKey, string name)
    {
        var session = _session;
        if (session is null)
        {
            Native.PressMediaKey(mediaKey);
            return;
        }

        string before = Title + "\u0001" + Artist;
        bool answer = false;
        try { answer = await command(session); }
        catch (Exception ex) { App.LogNote($"media {name} threw for '{SourceApp}': {ex.GetType().Name}: {ex.Message}"); }

        if (answer) return;
        for (int i = 0; i < 8; i++)
        {
            await Task.Delay(200);
            if (Title + "\u0001" + Artist != before) return; // it worked after all
        }
        App.LogNote($"media {name}: '{SourceApp}' ({session.SourceAppUserModelId}) said no and the song didn't change — pressed the media key");
        Native.PressMediaKey(mediaKey);
    }

    public async void ToggleShuffle()
    {
        try { if (_session is not null) await _session.TryChangeShuffleActiveAsync(!ShuffleOn); } catch { }
    }

    /// <summary>Off → repeat the list → repeat this song → off, like most music apps.</summary>
    public async void CycleRepeat()
    {
        var next = RepeatMode switch
        {
            0 => Windows.Media.MediaPlaybackAutoRepeatMode.List,
            2 => Windows.Media.MediaPlaybackAutoRepeatMode.Track,
            _ => Windows.Media.MediaPlaybackAutoRepeatMode.None,
        };
        try { if (_session is not null) await _session.TryChangeAutoRepeatModeAsync(next); } catch { }
    }

    /// <summary>
    /// Jumps to a point in the song. Returns false only if the app says outright that it doesn't allow jumping.
    /// The bar stays where you dropped it for a few seconds while the app catches up.
    /// </summary>
    public async Task<bool> SeekAsync(TimeSpan position)
    {
        var session = _session;
        if (session is null) return false;
        try
        {
            var controls = session.GetPlaybackInfo()?.Controls;
            if (controls is not null && !controls.IsPlaybackPositionEnabled)
            {
                App.LogNote($"media seek: '{SourceApp}' ({session.SourceAppUserModelId}) says it doesn't allow jumping");
                return false;
            }

            var start = session.GetTimelineProperties()?.StartTime ?? TimeSpan.Zero;
            _position = position;
            _positionReportedAt = DateTimeOffset.Now;
            _holdPositionUntil = DateTime.Now.AddSeconds(3);
            Changed?.Invoke();

            bool answer = await session.TryChangePlaybackPositionAsync((start + position).Ticks);
            if (!answer)
                App.LogNote($"media seek: '{SourceApp}' ({session.SourceAppUserModelId}) answered no to jumping to {position:m\\:ss}");

            // Afterwards, note where the app says it is (helps work out what went wrong if it didn't move).
            await Task.Delay(1500);
            var now = session.GetTimelineProperties();
            if (now is not null)
            {
                var reported = now.Position - now.StartTime;
                if (Math.Abs((reported - position).TotalSeconds) > 5)
                    App.LogNote($"media seek: asked for {position:m\\:ss}, '{SourceApp}' reports {reported:m\\:ss} (updated {now.LastUpdatedTime:HH:mm:ss})");
            }
            return true;
        }
        catch (Exception ex)
        {
            _holdPositionUntil = DateTime.MinValue;
            App.LogNote($"media seek failed for '{SourceApp}': {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
