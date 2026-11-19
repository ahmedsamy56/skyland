using System.IO;
using System.Windows.Media.Imaging;
using Windows.Media.Control;

namespace DynamicIsland.Services;

public sealed record MediaSnapshot(string Title, string Artist, bool IsPlaying, BitmapImage? Art);

public sealed record TimelineSnapshot(TimeSpan Position, TimeSpan Duration);

/// <summary>
/// Wraps the Windows System Media Transport Controls (SMTC) so the UI never
/// touches WinRT directly. <see cref="Changed"/> may fire on a background thread.
/// </summary>
public sealed class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    private string? _artKey;
    private BitmapImage? _art;

    /// <summary>Raised when the session, track or playback state changes.</summary>
    public event EventHandler? Changed;

    public async Task InitializeAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += (_, _) => AttachSession();
        AttachSession();
    }

    private void AttachSession()
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnSessionChanged;
            _session.PlaybackInfoChanged -= OnSessionChanged;
        }

        _session = _manager?.GetCurrentSession();

        if (_session is not null)
        {
            _session.MediaPropertiesChanged += OnSessionChanged;
            _session.PlaybackInfoChanged += OnSessionChanged;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnSessionChanged(object? sender, object e) => Changed?.Invoke(this, EventArgs.Empty);

    public async Task<MediaSnapshot?> GetSnapshotAsync()
    {
        var session = _session;
        if (session is null) return null;

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();

            if (string.IsNullOrWhiteSpace(props.Title) && string.IsNullOrWhiteSpace(props.Artist))
                return null;

            // Reload the thumbnail only when the track changes.
            string key = $"{props.Title}|{props.Artist}|{props.AlbumTitle}";
            if (key != _artKey)
            {
                _art = await LoadThumbnailAsync(props.Thumbnail);
                _artKey = key;
            }

            bool playing = playback.PlaybackStatus ==
                           GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            return new MediaSnapshot(props.Title ?? "", props.Artist ?? "", playing, _art);
        }
        catch
        {
            // The session can vanish mid-call (player closed). Treat as "no media".
            return null;
        }
    }

    public TimelineSnapshot? GetTimeline()
    {
        var session = _session;
        if (session is null) return null;

        try
        {
            var t = session.GetTimelineProperties();
            var duration = t.EndTime - t.StartTime;
            if (duration <= TimeSpan.Zero) return null;

            var position = t.Position - t.StartTime;

            // SMTC updates Position only occasionally; extrapolate while playing.
            if (session.GetPlaybackInfo().PlaybackStatus ==
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                position += DateTimeOffset.Now - t.LastUpdatedTime;
            }

            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (position > duration) position = duration;
            return new TimelineSnapshot(position, duration);
        }
        catch
        {
            return null;
        }
    }

    public Task PlayPauseAsync() => RunAsync(s => s.TryTogglePlayPauseAsync().AsTask());
    public Task NextAsync() => RunAsync(s => s.TrySkipNextAsync().AsTask());
    public Task PreviousAsync() => RunAsync(s => s.TrySkipPreviousAsync().AsTask());

    private async Task RunAsync(Func<GlobalSystemMediaTransportControlsSession, Task> action)
    {
        var session = _session;
        if (session is null) return;

        try { await action(session); }
        catch { /* the player went away */ }
    }

    private static async Task<BitmapImage?> LoadThumbnailAsync(
        Windows.Storage.Streams.IRandomAccessStreamReference? reference)
    {
        if (reference is null) return null;

        try
        {
            using var winrtStream = await reference.OpenReadAsync();
            using var source = winrtStream.AsStreamForRead();
            var buffer = new MemoryStream();
            await source.CopyToAsync(buffer);
            buffer.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 160;
            image.StreamSource = buffer;
            image.EndInit();
            image.Freeze(); // usable from any thread
            return image;
        }
        catch
        {
            return null;
        }
    }
}
