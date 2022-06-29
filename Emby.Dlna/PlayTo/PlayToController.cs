#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Dlna.Didl;
using Jellyfin.Data.Entities;
using Jellyfin.Data.Events;
using MediaBrowser.Controller.Dlna;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Photo = MediaBrowser.Controller.Entities.Photo;

namespace Emby.Dlna.PlayTo
{
    public class PlayToController : ISessionController, IDisposable
    {
        private readonly SessionInfo _session;
        private readonly ISessionManager _sessionManager;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private readonly IDlnaManager _dlnaManager;
        private readonly IUserManager _userManager;
        private readonly IImageProcessor _imageProcessor;
        private readonly IUserDataManager _userDataManager;
        private readonly ILocalizationManager _localization;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly IMediaEncoder _mediaEncoder;

        private readonly IDeviceDiscovery _deviceDiscovery;
        private readonly string _serverAddress;
        private readonly string _accessToken;

        private readonly List<PlaylistItem> _playlist = new List<PlaylistItem>();
        private Device _device;
        private int _currentPlaylistIndex;

        private bool _disposed;

        public PlayToController(
            SessionInfo session,
            ISessionManager sessionManager,
            ILibraryManager libraryManager,
            ILogger logger,
            IDlnaManager dlnaManager,
            IUserManager userManager,
            IImageProcessor imageProcessor,
            string serverAddress,
            string accessToken,
            IDeviceDiscovery deviceDiscovery,
            IUserDataManager userDataManager,
            ILocalizationManager localization,
            IMediaSourceManager mediaSourceManager,
            IMediaEncoder mediaEncoder)
        {
            _session = session;
            _sessionManager = sessionManager;
            _libraryManager = libraryManager;
            _logger = logger;
            _dlnaManager = dlnaManager;
            _userManager = userManager;
            _imageProcessor = imageProcessor;
            _serverAddress = serverAddress;
            _accessToken = accessToken;
            _deviceDiscovery = deviceDiscovery;
            _userDataManager = userDataManager;
            _localization = localization;
            _mediaSourceManager = mediaSourceManager;
            _mediaEncoder = mediaEncoder;
        }

        public bool IsSessionActive => !_disposed && _device != null;

        public bool SupportsMediaControl => IsSessionActive;

        public void Init(Device device)
        {
            _device = device;
            _device.OnDeviceUnavailable = OnDeviceUnavailable;
            _device.PlaybackStart += OnDevicePlaybackStart;
            _device.PlaybackProgress += OnDevicePlaybackProgress;
            _device.PlaybackStopped += OnDevicePlaybackStopped;
            _device.MediaChanged += OnDeviceMediaChanged;

            _device.Start();

            _deviceDiscovery.DeviceLeft += OnDeviceDiscoveryDeviceLeft;
        }

        /*
         * Send a message to the DLNA device to notify what is the next track in the playlist.
         */
        private async Task SendNextTrackMessage(int currentPlayListItemIndex, CancellationToken cancellationToken)
        {
            if (currentPlayListItemIndex >= 0 && currentPlayListItemIndex < _playlist.Count - 1)
            {
                // The current playing item is indeed in the play list and we are not yet at the end of the playlist.
                var nextItemIndex = currentPlayListItemIndex + 1;
                var nextItem = _playlist[nextItemIndex];

                // Send the SetNextAvTransport message.
                await _device.SetNextAvTransport(nextItem.StreamUrl, GetDlnaHeaders(nextItem), nextItem.Didl, cancellationToken).ConfigureAwait(false);
            }
        }

        private void OnDeviceUnavailable()
        {
            try
            {
                _sessionManager.ReportSessionEnded(_session.Id);
            }
            catch (Exception ex)
            {
                // Could throw if the session is already gone
                _logger.LogError(ex, "Error reporting the end of session {Id}", _session.Id);
            }
        }

        private void OnDeviceDiscoveryDeviceLeft(object sender, GenericEventArgs<UpnpDeviceInfo> e)
        {
            var info = e.Argument;

            if (!_disposed
                && info.Headers.TryGetValue("USN", out string usn)
                && usn.IndexOf(_device.Properties.UUID, StringComparison.OrdinalIgnoreCase) != -1
                && (usn.IndexOf("MediaRenderer:", StringComparison.OrdinalIgnoreCase) != -1
                    || (info.Headers.TryGetValue("NT", out string nt)
                        && nt.IndexOf("MediaRenderer:", StringComparison.OrdinalIgnoreCase) != -1)))
            {
                OnDeviceUnavailable();
            }
        }

        private async void OnDeviceMediaChanged(object sender, MediaChangedEventArgs e)
        {
            if (_disposed || string.IsNullOrEmpty(e.OldMediaInfo.Url))
            {
                return;
            }

            try
            {
                var streamInfo = StreamParams.ParseFromUrl(e.OldMediaInfo.Url, _libraryManager, _mediaSourceManager);
                if (streamInfo.Item != null)
                {
                    var positionTicks = GetProgressPositionTicks(streamInfo);

                    await ReportPlaybackStopped(streamInfo, positionTicks).ConfigureAwait(false);
                }

                streamInfo = StreamParams.ParseFromUrl(e.NewMediaInfo.Url, _libraryManager, _mediaSourceManager);
                if (streamInfo.Item == null)
                {
                    return;
                }

                var newItemProgress = GetProgressInfo(streamInfo);

                await _sessionManager.OnPlaybackStart(newItemProgress).ConfigureAwait(false);

                // Send a message to the DLNA device to notify what is the next track in the playlist.
                var currentItemIndex = _playlist.FindIndex(item => item.StreamInfo.ItemId.Equals(streamInfo.ItemId));
                if (currentItemIndex >= 0)
                {
                    _currentPlaylistIndex = currentItemIndex;
                }

                await SendNextTrackMessage(currentItemIndex, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reporting progress");
            }
        }

        private async void OnDevicePlaybackStopped(object sender, PlaybackStoppedEventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var streamInfo = StreamParams.ParseFromUrl(e.MediaInfo.Url, _libraryManager, _mediaSourceManager);

                if (streamInfo.Item == null)
                {
                    return;
                }

                var positionTicks = GetProgressPositionTicks(streamInfo);

                await ReportPlaybackStopped(streamInfo, positionTicks).ConfigureAwait(false);

                var mediaSource = await streamInfo.GetMediaSource(CancellationToken.None).ConfigureAwait(false);

                var duration = mediaSource == null
                    ? _device.Duration?.Ticks
                    : mediaSource.RunTimeTicks;

                var playedToCompletion = positionTicks.HasValue && positionTicks.Value == 0;

                if (!playedToCompletion && duration.HasValue && positionTicks.HasValue)
                {
                    double percent = positionTicks.Value;
                    percent /= duration.Value;

                    playedToCompletion = Math.Abs(1 - percent) <= .1;
                }

                if (playedToCompletion)
                {
                    await SetPlaylistIndex(_currentPlaylistIndex + 1).ConfigureAwait(false);
                }
                else
                {
                    _playlist.Clear();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reporting playback stopped");
            }
        }

        private async Task ReportPlaybackStopped(StreamParams streamInfo, long? positionTicks)
        {
            try
            {
                await _sessionManager.OnPlaybackStopped(new PlaybackStopInfo
                {
                    ItemId = streamInfo.ItemId,
                    SessionId = _session.Id,
                    PositionTicks = positionTicks,
                    MediaSourceId = streamInfo.MediaSourceId
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reporting progress");
            }
        }

        private async void OnDevicePlaybackStart(object sender, PlaybackStartEventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var info = StreamParams.ParseFromUrl(e.MediaInfo.Url, _libraryManager, _mediaSourceManager);

                if (info.Item != null)
                {
                    var progress = GetProgressInfo(info);

                    await _sessionManager.OnPlaybackStart(progress).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reporting progress");
            }
        }

        private async void OnDevicePlaybackProgress(object sender, PlaybackProgressEventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var mediaUrl = e.MediaInfo.Url;

                if (string.IsNullOrWhiteSpace(mediaUrl))
                {
                    return;
                }

                var info = StreamParams.ParseFromUrl(mediaUrl, _libraryManager, _mediaSourceManager);

                if (info.Item != null)
                {
                    var progress = GetProgressInfo(info);

                    await _sessionManager.OnPlaybackProgress(progress).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reporting progress");
            }
        }

        private long? GetProgressPositionTicks(StreamParams info)
        {
            var ticks = _device.Position.Ticks;

            if (!EnableClientSideSeek(info))
            {
                ticks += info.StartPositionTicks;
            }

            return ticks;
        }

        private PlaybackStartInfo GetProgressInfo(StreamParams info)
        {
            return new PlaybackStartInfo
            {
                ItemId = info.ItemId,
                SessionId = _session.Id,
                PositionTicks = GetProgressPositionTicks(info),
                IsMuted = _device.IsMuted,
                IsPaused = _device.IsPaused,
                MediaSourceId = info.MediaSourceId,
                AudioStreamIndex = info.AudioStreamIndex,
                SubtitleStreamIndex = info.SubtitleStreamIndex,
                VolumeLevel = _device.Volume,

                // TODO
                CanSeek = true,

                PlayMethod = info.IsDirectStream ? PlayMethod.DirectStream : PlayMethod.Transcode
            };
        }

        public Task SendPlayCommand(PlayRequest command, CancellationToken cancellationToken)
        {
            _logger.LogDebug("{0} - Received PlayRequest: {1}", _session.DeviceName, command.PlayCommand);

            var user = command.ControllingUserId.Equals(default)
                ? null :
                _userManager.GetUserById(command.ControllingUserId);

            var items = new List<BaseItem>();
            foreach (var id in command.ItemIds)
            {
                AddItemFromId(id, items);
            }

            var startIndex = command.StartIndex ?? 0;
            int len = items.Count - startIndex;
            if (startIndex > 0)
            {
                items = items.GetRange(startIndex, len);
            }

            var playlist = new PlaylistItem[len];

            // Not nullable enabled - so this is required.
            playlist[0] = CreatePlaylistItem(
                items[0],
                user,
                command.StartPositionTicks ?? 0,
                command.MediaSourceId ?? string.Empty,
                command.AudioStreamIndex,
                command.SubtitleStreamIndex);

            for (int i = 1; i < len; i++)
            {
                playlist[i] = CreatePlaylistItem(items[i], user, 0, string.Empty, null, null);
            }

            _logger.LogDebug("{0} - Playlist created", _session.DeviceName);

            if (command.PlayCommand == PlayCommand.PlayLast)
            {
                _playlist.AddRange(playlist);
            }

            if (command.PlayCommand == PlayCommand.PlayNext)
            {
                _playlist.AddRange(playlist);
            }

            if (!command.ControllingUserId.Equals(default))
            {
                _sessionManager.LogSessionActivity(
                    _session.Client,
                    _session.ApplicationVersion,
                    _session.DeviceId,
                    _session.DeviceName,
                    _session.RemoteEndPoint,
                    user);
            }

            return PlayItems(playlist, cancellationToken);
        }

        private Task SendPlaystateCommand(PlaystateRequest command, CancellationToken cancellationToken)
        {
            switch (command.Command)
            {
                case PlaystateCommand.Stop:
                    _playlist.Clear();
                    return _device.SetStop(CancellationToken.None);

                case PlaystateCommand.Pause:
                    return _device.SetPause(CancellationToken.None);

                case PlaystateCommand.Unpause:
                    return _device.SetPlay(CancellationToken.None);

                case PlaystateCommand.PlayPause:
                    return _device.IsPaused ? _device.SetPlay(CancellationToken.None) : _device.SetPause(CancellationToken.None);

                case PlaystateCommand.Seek:
                    return Seek(command.SeekPositionTicks ?? 0);

                case PlaystateCommand.NextTrack:
                    return SetPlaylistIndex(_currentPlaylistIndex + 1, cancellationToken);

                case PlaystateCommand.PreviousTrack:
                    return SetPlaylistIndex(_currentPlaylistIndex - 1, cancellationToken);
            }

            return Task.CompletedTask;
        }

        private async Task Seek(long newPosition)
        {
            var media = _device.CurrentMediaInfo;

            if (media != null)
            {
                var info = StreamParams.ParseFromUrl(media.Url, _libraryManager, _mediaSourceManager);

                if (info.Item != null && !EnableClientSideSeek(info))
                {
                    var user = _session.UserId.Equals(default)
                        ? null
                        : _userManager.GetUserById(_session.UserId);
                    var newItem = CreatePlaylistItem(info.Item, user, newPosition, info.MediaSourceId, info.AudioStreamIndex, info.SubtitleStreamIndex);

                    await _device.SetAvTransport(newItem.StreamUrl, GetDlnaHeaders(newItem), newItem.Didl, CancellationToken.None).ConfigureAwait(false);

                    // Send a message to the DLNA device to notify what is the next track in the play list.
                    var newItemIndex = _playlist.FindIndex(item => item.StreamUrl == newItem.StreamUrl);
                    await SendNextTrackMessage(newItemIndex, CancellationToken.None).ConfigureAwait(false);

                    return;
                }

                await SeekAfterTransportChange(newPosition, CancellationToken.None).ConfigureAwait(false);
            }
        }

        private bool EnableClientSideSeek(StreamParams info)
        {
            return info.IsDirectStream;
        }

        private bool EnableClientSideSeek(StreamInfo info)
        {
            return info.IsDirectStream;
        }

        private void AddItemFromId(Guid id, List<BaseItem> list)
        {
            var item = _libraryManager.GetItemById(id);
            if (item.MediaType == MediaType.Audio || item.MediaType == MediaType.Video)
            {
                list.Add(item);
            }
        }

        private PlaylistItem CreatePlaylistItem(
            BaseItem item,
            User user,
            long startPostionTicks,
            string mediaSourceId,
            int? audioStreamIndex,
            int? subtitleStreamIndex)
        {
            var deviceInfo = _device.Properties;

            var profile = _dlnaManager.GetProfile(deviceInfo.ToDeviceIdentification()) ??
                _dlnaManager.GetDefaultProfile();

            var mediaSources = item is IHasMediaSources
                ? _mediaSourceManager.GetStaticMediaSources(item, true, user).ToArray()
                : Array.Empty<MediaSourceInfo>();

            var playlistItem = GetPlaylistItem(item, mediaSources, profile, _session.DeviceId, mediaSourceId, audioStreamIndex, subtitleStreamIndex);
            playlistItem.StreamInfo.StartPositionTicks = startPostionTicks;

            playlistItem.StreamUrl = DidlBuilder.NormalizeDlnaMediaUrl(playlistItem.StreamInfo.ToUrl(_serverAddress, _accessToken));

            var itemXml = new DidlBuilder(
                profile,
                user,
                _imageProcessor,
                _serverAddress,
                _accessToken,
                _userDataManager,
                _localization,
                _mediaSourceManager,
                _logger,
                _mediaEncoder,
                _libraryManager)
                .GetItemDidl(item, user, null, _session.DeviceId, new Filter(), playlistItem.StreamInfo);

            playlistItem.Didl = itemXml;

            return playlistItem;
        }

        private string GetDlnaHeaders(PlaylistItem item)
        {
            var profile = item.Profile;
            var streamInfo = item.StreamInfo;

            if (streamInfo.MediaType == DlnaProfileType.Audio)
            {
                return ContentFeatureBuilder.BuildAudioHeader(
                        profile,
                        streamInfo.Container,
                        streamInfo.TargetAudioCodec.FirstOrDefault(),
                        streamInfo.TargetAudioBitrate,
                        streamInfo.TargetAudioSampleRate,
                        streamInfo.TargetAudioChannels,
                        streamInfo.TargetAudioBitDepth,
                        streamInfo.IsDirectStream,
                        streamInfo.RunTimeTicks ?? 0,
                        streamInfo.TranscodeSeekInfo);
            }

            if (streamInfo.MediaType == DlnaProfileType.Video)
            {
                var list = ContentFeatureBuilder.BuildVideoHeader(
                        profile,
                        streamInfo.Container,
                        streamInfo.TargetVideoCodec.FirstOrDefault(),
                        streamInfo.TargetAudioCodec.FirstOrDefault(),
                        streamInfo.TargetWidth,
                        streamInfo.TargetHeight,
                        streamInfo.TargetVideoBitDepth,
                        streamInfo.TargetVideoBitrate,
                        streamInfo.TargetTimestamp,
                        streamInfo.IsDirectStream,
                        streamInfo.RunTimeTicks ?? 0,
                        streamInfo.TargetVideoProfile,
                        streamInfo.TargetVideoRangeType,
                        streamInfo.TargetVideoLevel,
                        streamInfo.TargetFramerate ?? 0,
                        streamInfo.TargetPacketLength,
                        streamInfo.TranscodeSeekInfo,
                        streamInfo.IsTargetAnamorphic,
                        streamInfo.IsTargetInterlaced,
                        streamInfo.TargetRefFrames,
                        streamInfo.TargetVideoStreamCount,
                        streamInfo.TargetAudioStreamCount,
                        streamInfo.TargetVideoCodecTag,
                        streamInfo.IsTargetAVC);

                return list.FirstOrDefault();
            }

            return null;
        }

        private PlaylistItem GetPlaylistItem(BaseItem item, MediaSourceInfo[] mediaSources, DeviceProfile profile, string deviceId, string mediaSourceId, int? audioStreamIndex, int? subtitleStreamIndex)
        {
            if (string.Equals(item.MediaType, MediaType.Video, StringComparison.OrdinalIgnoreCase))
            {
                return new PlaylistItem
                {
                    StreamInfo = new StreamBuilder(_mediaEncoder, _logger).BuildVideoItem(new VideoOptions
                    {
                        ItemId = item.Id,
                        MediaSources = mediaSources,
                        Profile = profile,
                        DeviceId = deviceId,
                        MaxBitrate = profile.MaxStreamingBitrate,
                        MediaSourceId = mediaSourceId,
                        AudioStreamIndex = audioStreamIndex,
                        SubtitleStreamIndex = subtitleStreamIndex
                    }),

                    Profile = profile
                };
            }

            if (string.Equals(item.MediaType, MediaType.Audio, StringComparison.OrdinalIgnoreCase))
            {
                return new PlaylistItem
                {
                    StreamInfo = new StreamBuilder(_mediaEncoder, _logger).BuildAudioItem(new AudioOptions
                    {
                        ItemId = item.Id,
                        MediaSources = mediaSources,
                        Profile = profile,
                        DeviceId = deviceId,
                        MaxBitrate = profile.MaxStreamingBitrate,
                        MediaSourceId = mediaSourceId
                    }),

                    Profile = profile
                };
            }

            if (string.Equals(item.MediaType, MediaType.Photo, StringComparison.OrdinalIgnoreCase))
            {
                return PlaylistItemFactory.Create((Photo)item, profile);
            }

            throw new ArgumentException("Unrecognized item type.");
        }

        /// <summary>
        /// Plays the items.
        /// </summary>
        /// <param name="items">The items.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><c>true</c> on success.</returns>
        private async Task<bool> PlayItems(IEnumerable<PlaylistItem> items, CancellationToken cancellationToken = default)
        {
            _playlist.Clear();
            _playlist.AddRange(items);
            _logger.LogDebug("{0} - Playing {1} items", _session.DeviceName, _playlist.Count);

            await SetPlaylistIndex(0, cancellationToken).ConfigureAwait(false);
            return true;
        }

        private async Task SetPlaylistIndex(int index, CancellationToken cancellationToken = default)
        {
            if (index < 0 || index >= _playlist.Count)
            {
                _playlist.Clear();
                await _device.SetStop(cancellationToken).ConfigureAwait(false);
                return;
            }

            _currentPlaylistIndex = index;
            var currentitem = _playlist[index];

            await _device.SetAvTransport(currentitem.StreamUrl, GetDlnaHeaders(currentitem), currentitem.Didl, cancellationToken).ConfigureAwait(false);

            // Send a message to the DLNA device to notify what is the next track in the play list.
            await SendNextTrackMessage(index, cancellationToken).ConfigureAwait(false);

            var streamInfo = currentitem.StreamInfo;
            if (streamInfo.StartPositionTicks > 0 && EnableClientSideSeek(streamInfo))
            {
                await SeekAfterTransportChange(streamInfo.StartPositionTicks, CancellationToken.None).ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and optionally managed resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                _device.Dispose();
            }

            _device.PlaybackStart -= OnDevicePlaybackStart;
            _device.PlaybackProgress -= OnDevicePlaybackProgress;
            _device.PlaybackStopped -= OnDevicePlaybackStopped;
            _device.MediaChanged -= OnDeviceMediaChanged;
            _deviceDiscovery.DeviceLeft -= OnDeviceDiscoveryDeviceLeft;
            _device.OnDeviceUnavailable = null;
            _device = null;

            _disposed = true;
        }

        private Task SendGeneralCommand(GeneralCommand command, CancellationToken cancellationToken)
        {
            switch (command.Name)
            {
                case GeneralCommandType.VolumeDown:
                    return _device.VolumeDown(cancellationToken);
                case GeneralCommandType.VolumeUp:
                    return _device.VolumeUp(cancellationToken);
                case GeneralCommandType.Mute:
                    return _device.Mute(cancellationToken);
                case GeneralCommandType.Unmute:
                    return _device.Unmute(cancellationToken);
                case GeneralCommandType.ToggleMute:
                    return _device.ToggleMute(cancellationToken);
                case GeneralCommandType.SetAudioStreamIndex:
                    if (command.Arguments.TryGetValue("Index", out string index))
                    {
                        if (int.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out var val))
                        {
                            return SetAudioStreamIndex(val);
                        }

                        throw new ArgumentException("Unsupported SetAudioStreamIndex value supplied.");
                    }

                    throw new ArgumentException("SetAudioStreamIndex argument cannot be null");
                case GeneralCommandType.SetSubtitleStreamIndex:
                    if (command.Arguments.TryGetValue("Index", out index))
                    {
                        if (int.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out var val))
                        {
                            return SetSubtitleStreamIndex(val);
                        }

                        throw new ArgumentException("Unsupported SetSubtitleStreamIndex value supplied.");
                    }

                    throw new ArgumentException("SetSubtitleStreamIndex argument cannot be null");
                case GeneralCommandType.SetVolume:
                    if (command.Arguments.TryGetValue("Volume", out string vol))
                    {
                        if (int.TryParse(vol, NumberStyles.Integer, CultureInfo.InvariantCulture, out var volume))
                        {
                            return _device.SetVolume(volume, cancellationToken);
                        }

                        throw new ArgumentException("Unsupported volume value supplied.");
                    }

                    throw new ArgumentException("Volume argument cannot be null");
                default:
                    return Task.CompletedTask;
            }
        }

        private async Task SetAudioStreamIndex(int? newIndex)
        {
            var media = _device.CurrentMediaInfo;

            if (media != null)
            {
                var info = StreamParams.ParseFromUrl(media.Url, _libraryManager, _mediaSourceManager);

                if (info.Item != null)
                {
                    var newPosition = GetProgressPositionTicks(info) ?? 0;

                    var user = _session.UserId.Equals(default)
                        ? null
                        : _userManager.GetUserById(_session.UserId);
                    var newItem = CreatePlaylistItem(info.Item, user, newPosition, info.MediaSourceId, newIndex, info.SubtitleStreamIndex);

                    await _device.SetAvTransport(newItem.StreamUrl, GetDlnaHeaders(newItem), newItem.Didl, CancellationToken.None).ConfigureAwait(false);

                    // Send a message to the DLNA device to notify what is the next track in the play list.
                    var newItemIndex = _playlist.FindIndex(item => item.StreamUrl == newItem.StreamUrl);
                    await SendNextTrackMessage(newItemIndex, CancellationToken.None).ConfigureAwait(false);

                    if (EnableClientSideSeek(newItem.StreamInfo))
                    {
                        await SeekAfterTransportChange(newPosition, CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
        }

        private async Task SetSubtitleStreamIndex(int? newIndex)
        {
            var media = _device.CurrentMediaInfo;

            if (media != null)
            {
                var info = StreamParams.ParseFromUrl(media.Url, _libraryManager, _mediaSourceManager);

                if (info.Item != null)
                {
                    var newPosition = GetProgressPositionTicks(info) ?? 0;

                    var user = _session.UserId.Equals(default)
                        ? null
                        : _userManager.GetUserById(_session.UserId);
                    var newItem = CreatePlaylistItem(info.Item, user, newPosition, info.MediaSourceId, info.AudioStreamIndex, newIndex);

                    await _device.SetAvTransport(newItem.StreamUrl, GetDlnaHeaders(newItem), newItem.Didl, CancellationToken.None).ConfigureAwait(false);

                    // Send a message to the DLNA device to notify what is the next track in the play list.
                    var newItemIndex = _playlist.FindIndex(item => item.StreamUrl == newItem.StreamUrl);
                    await SendNextTrackMessage(newItemIndex, CancellationToken.None).ConfigureAwait(false);

                    if (EnableClientSideSeek(newItem.StreamInfo) && newPosition > 0)
                    {
                        await SeekAfterTransportChange(newPosition, CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
        }

        private async Task SeekAfterTransportChange(long positionTicks, CancellationToken cancellationToken)
        {
            const int MaxWait = 15000000;
            const int Interval = 500;

            var currentWait = 0;
            while (_device.TransportState != TransportState.PLAYING && currentWait < MaxWait)
            {
                await Task.Delay(Interval, cancellationToken).ConfigureAwait(false);
                currentWait += Interval;
            }

            await _device.Seek(TimeSpan.FromTicks(positionTicks), cancellationToken).ConfigureAwait(false);
        }

        private static int? GetIntValue(IReadOnlyDictionary<string, string> values, string name)
        {
            var value = values.GetValueOrDefault(name);

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
            {
                return result;
            }

            return null;
        }

        private static long GetLongValue(IReadOnlyDictionary<string, string> values, string name)
        {
            var value = values.GetValueOrDefault(name);

            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
            {
                return result;
            }

            return 0;
        }

        /// <inheritdoc />
        public Task SendMessage<T>(SessionMessageType name, Guid messageId, T data, CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().Name);
            }

            if (_device == null)
            {
                return Task.CompletedTask;
            }

            if (name == SessionMessageType.Play)
            {
                return SendPlayCommand(data as PlayRequest, cancellationToken);
            }

            if (name == SessionMessageType.Playstate)
            {
                return SendPlaystateCommand(data as PlaystateRequest, cancellationToken);
            }

            if (name == SessionMessageType.GeneralCommand)
            {
                return SendGeneralCommand(data as GeneralCommand, cancellationToken);
            }

            // Not supported or needed right now
            return Task.CompletedTask;
        }

        private class StreamParams
        {
            private MediaSourceInfo _mediaSource;
            private IMediaSourceManager _mediaSourceManager;

            public Guid ItemId { get; set; }

            public bool IsDirectStream { get; set; }

            public long StartPositionTicks { get; set; }

            public int? AudioStreamIndex { get; set; }

            public int? SubtitleStreamIndex { get; set; }

            public string DeviceProfileId { get; set; }

            public string DeviceId { get; set; }

            public string MediaSourceId { get; set; }

            public string LiveStreamId { get; set; }

            public BaseItem Item { get; set; }

            public async Task<MediaSourceInfo> GetMediaSource(CancellationToken cancellationToken)
            {
                if (_mediaSource != null)
                {
                    return _mediaSource;
                }

                if (Item is not IHasMediaSources)
                {
                    return null;
                }

                if (_mediaSourceManager != null)
                {
                    _mediaSource = await _mediaSourceManager.GetMediaSource(Item, MediaSourceId, LiveStreamId, false, cancellationToken).ConfigureAwait(false);
                }

                return _mediaSource;
            }

            private static Guid GetItemId(string url)
            {
                if (string.IsNullOrEmpty(url))
                {
                    throw new ArgumentNullException(nameof(url));
                }

                var parts = url.Split('/');

                for (var i = 0; i < parts.Length - 1; i++)
                {
                    var part = parts[i];

                    if (string.Equals(part, "audio", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(part, "videos", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Guid.TryParse(parts[i + 1], out var result))
                        {
                            return result;
                        }
                    }
                }

                return default;
            }

            public static StreamParams ParseFromUrl(string url, ILibraryManager libraryManager, IMediaSourceManager mediaSourceManager)
            {
                if (string.IsNullOrEmpty(url))
                {
                    throw new ArgumentNullException(nameof(url));
                }

                var request = new StreamParams
                {
                    ItemId = GetItemId(url)
                };

                if (request.ItemId.Equals(default))
                {
                    return request;
                }

                var index = url.IndexOf('?', StringComparison.Ordinal);
                if (index == -1)
                {
                    return request;
                }

                var query = url.Substring(index + 1);
                Dictionary<string, string> values = QueryHelpers.ParseQuery(query).ToDictionary(kv => kv.Key, kv => kv.Value.ToString());

                request.DeviceProfileId = values.GetValueOrDefault("DeviceProfileId");
                request.DeviceId = values.GetValueOrDefault("DeviceId");
                request.MediaSourceId = values.GetValueOrDefault("MediaSourceId");
                request.LiveStreamId = values.GetValueOrDefault("LiveStreamId");
                request.IsDirectStream = string.Equals("true", values.GetValueOrDefault("Static"), StringComparison.OrdinalIgnoreCase);
                request.AudioStreamIndex = GetIntValue(values, "AudioStreamIndex");
                request.SubtitleStreamIndex = GetIntValue(values, "SubtitleStreamIndex");
                request.StartPositionTicks = GetLongValue(values, "StartPositionTicks");

                request.Item = libraryManager.GetItemById(request.ItemId);

                request._mediaSourceManager = mediaSourceManager;

                return request;
            }
        }
    }
}
