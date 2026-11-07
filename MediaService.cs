using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace Isle
{
    /// <summary>Momentaufnahme dessen, was gerade läuft.</summary>
    public sealed record MediaSnapshot
    {
        public string Title { get; init; } = "";
        public string Artist { get; init; } = "";
        public string AppName { get; init; } = "";
        public bool IsPlaying { get; init; }
        public bool CanPlayPause { get; init; }
        public bool CanNext { get; init; }
        public bool CanPrevious { get; init; }
        public bool CanSeek { get; init; }
        public TimeSpan Position { get; init; }
        public TimeSpan Duration { get; init; }
        public DateTimeOffset PositionUpdatedAt { get; init; }
        public byte[]? Thumbnail { get; init; }
        public string TrackKey => Title + "\u0001" + Artist;

        /// <summary>Position hochgerechnet auf jetzt (manche Apps wie Spotify melden sie nur selten).</summary>
        public TimeSpan EstimatedPosition
        {
            get
            {
                if (!IsPlaying || Duration <= TimeSpan.Zero) return Position;
                var est = Position + (DateTimeOffset.Now - PositionUpdatedAt);
                if (est < TimeSpan.Zero) return TimeSpan.Zero;
                return est > Duration ? Duration : est;
            }
        }
    }

    /// <summary>
    /// Liest die Windows-Mediensteuerung (dieselbe Quelle wie die Lautstärke-Einblendung von Windows).
    /// Funktioniert mit Spotify, Browsern (YouTube etc.), Windows Media Player, Apple Music u. v. m.
    /// </summary>
    public sealed class MediaService : IDisposable
    {
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _session;
        private readonly SemaphoreSlim _refreshLock = new(1, 1);
        private string _thumbKey = "";
        private byte[]? _thumbCache;

        public MediaSnapshot? Current { get; private set; }

        /// <summary>Wird auf einem Hintergrund-Thread ausgelöst.</summary>
        public event Action<MediaSnapshot?>? Changed;

        public async Task InitializeAsync()
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => AttachCurrent();
            _manager.SessionsChanged += (_, _) => AttachCurrent();
            AttachCurrent();
        }

        private void AttachCurrent()
        {
            var next = _manager?.GetCurrentSession();
            if (ReferenceEquals(next, _session))
            {
                _ = RefreshAsync();
                return;
            }

            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnSessionChanged;
                _session.PlaybackInfoChanged -= OnSessionChanged;
                _session.TimelinePropertiesChanged -= OnSessionChanged;
            }

            _session = next;

            if (_session != null)
            {
                _session.MediaPropertiesChanged += OnSessionChanged;
                _session.PlaybackInfoChanged += OnSessionChanged;
                _session.TimelinePropertiesChanged += OnSessionChanged;
            }

            _ = RefreshAsync();
        }

        private void OnSessionChanged(GlobalSystemMediaTransportControlsSession s, object e) => _ = RefreshAsync();

        public async Task RefreshAsync()
        {
            await _refreshLock.WaitAsync();
            try
            {
                var session = _session;
                if (session == null)
                {
                    Current = null;
                    Changed?.Invoke(null);
                    return;
                }

                var props = await session.TryGetMediaPropertiesAsync();
                var playback = session.GetPlaybackInfo();
                var timeline = session.GetTimelineProperties();
                var controls = playback?.Controls;

                string title = props?.Title ?? "";
                string artist = props?.Artist ?? "";
                if (string.IsNullOrEmpty(artist)) artist = props?.AlbumArtist ?? "";

                // Cover nur neu laden, wenn sich der Titel geändert hat
                string key = title + "\u0001" + artist;
                if (key != _thumbKey)
                {
                    _thumbKey = key;
                    _thumbCache = await ReadThumbnailAsync(props);
                }
                else if (_thumbCache == null)
                {
                    // Manche Apps liefern das Cover erst kurz nach dem Titel
                    _thumbCache = await ReadThumbnailAsync(props);
                }

                var duration = timeline != null ? timeline.EndTime - timeline.StartTime : TimeSpan.Zero;
                var position = timeline != null ? timeline.Position - timeline.StartTime : TimeSpan.Zero;

                Current = new MediaSnapshot
                {
                    Title = title,
                    Artist = artist,
                    AppName = FriendlyAppName(session.SourceAppUserModelId),
                    IsPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    CanPlayPause = controls?.IsPlayPauseToggleEnabled ?? false,
                    CanNext = controls?.IsNextEnabled ?? false,
                    CanPrevious = controls?.IsPreviousEnabled ?? false,
                    CanSeek = (controls?.IsPlaybackPositionEnabled ?? false) && duration > TimeSpan.Zero,
                    Duration = duration,
                    Position = position,
                    PositionUpdatedAt = timeline?.LastUpdatedTime ?? DateTimeOffset.Now,
                    Thumbnail = _thumbCache,
                };

                Changed?.Invoke(Current);
            }
            catch
            {
                // Die Media-API wirft gelegentlich, wenn eine App gerade schließt – einfach ignorieren.
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        private static async Task<byte[]?> ReadThumbnailAsync(GlobalSystemMediaTransportControlsSessionMediaProperties? props)
        {
            if (props?.Thumbnail == null) return null;
            try
            {
                using var winStream = await props.Thumbnail.OpenReadAsync();
                using var stream = winStream.AsStreamForRead();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                return ms.Length > 0 ? ms.ToArray() : null;
            }
            catch
            {
                return null;
            }
        }

        private static string FriendlyAppName(string? aumid)
        {
            if (string.IsNullOrWhiteSpace(aumid)) return "";
            string id = aumid.ToLowerInvariant();

            if (id.Contains("spotify")) return "Spotify";
            if (id.Contains("chrome")) return "Chrome";
            if (id.Contains("msedge")) return "Edge";
            if (id.Contains("firefox") || id == "308046b0af4a39cb") return "Firefox";
            if (id.Contains("opera")) return "Opera";
            if (id.Contains("brave")) return "Brave";
            if (id.Contains("zunemusic") || id.Contains("media.player")) return "Media Player";
            if (id.Contains("applemusic") || id.Contains("apple")) return "Apple Music";
            if (id.Contains("vlc")) return "VLC";
            if (id.Contains("discord")) return "Discord";
            if (id.Contains("tidal")) return "Tidal";
            if (id.Contains("deezer")) return "Deezer";

            // Fallback: "Irgendwas.exe" → "Irgendwas", Paketnamen kürzen
            string name = aumid;
            int bang = name.IndexOf('!');
            if (bang > 0) name = name[..bang];
            int us = name.IndexOf('_');
            if (us > 0) name = name[..us];
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            int dot = name.LastIndexOf('.');
            if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
            return name;
        }

        public async Task TogglePlayPauseAsync()
        {
            try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { }
        }

        public async Task NextAsync()
        {
            try { if (_session != null) await _session.TrySkipNextAsync(); } catch { }
        }

        public async Task PreviousAsync()
        {
            try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { }
        }

        public async Task SeekAsync(TimeSpan position)
        {
            try
            {
                if (_session == null) return;
                var start = _session.GetTimelineProperties()?.StartTime ?? TimeSpan.Zero;
                await _session.TryChangePlaybackPositionAsync((start + position).Ticks);
            }
            catch { }
        }

        public void Dispose()
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnSessionChanged;
                _session.PlaybackInfoChanged -= OnSessionChanged;
                _session.TimelinePropertiesChanged -= OnSessionChanged;
            }
            _refreshLock.Dispose();
        }
    }
}
