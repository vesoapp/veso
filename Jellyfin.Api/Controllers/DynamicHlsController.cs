using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Attributes;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Helpers;
using Jellyfin.Api.Models.PlaybackDtos;
using Jellyfin.Api.Models.StreamingDtos;
using Jellyfin.MediaEncoding.Hls.Playlist;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Dlna;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Controllers
{
    /// <summary>
    /// Dynamic hls controller.
    /// </summary>
    [Route("")]
    [Authorize(Policy = Policies.DefaultAuthorization)]
    public class DynamicHlsController : BaseJellyfinApiController
    {
        private const string DefaultVodEncoderPreset = "veryfast";
        private const string DefaultEventEncoderPreset = "superfast";
        private const TranscodingJobType TranscodingJobType = MediaBrowser.Controller.MediaEncoding.TranscodingJobType.Hls;

        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IDlnaManager _dlnaManager;
        private readonly IAuthorizationContext _authContext;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly IServerConfigurationManager _serverConfigurationManager;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly IFileSystem _fileSystem;
        private readonly IDeviceManager _deviceManager;
        private readonly TranscodingJobHelper _transcodingJobHelper;
        private readonly ILogger<DynamicHlsController> _logger;
        private readonly EncodingHelper _encodingHelper;
        private readonly IDynamicHlsPlaylistGenerator _dynamicHlsPlaylistGenerator;
        private readonly DynamicHlsHelper _dynamicHlsHelper;
        private readonly EncodingOptions _encodingOptions;

        /// <summary>
        /// Initializes a new instance of the <see cref="DynamicHlsController"/> class.
        /// </summary>
        /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
        /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
        /// <param name="dlnaManager">Instance of the <see cref="IDlnaManager"/> interface.</param>
        /// <param name="authContext">Instance of the <see cref="IAuthorizationContext"/> interface.</param>
        /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
        /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
        /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface.</param>
        /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
        /// <param name="deviceManager">Instance of the <see cref="IDeviceManager"/> interface.</param>
        /// <param name="transcodingJobHelper">Instance of the <see cref="TranscodingJobHelper"/> class.</param>
        /// <param name="logger">Instance of the <see cref="ILogger{DynamicHlsController}"/> interface.</param>
        /// <param name="dynamicHlsHelper">Instance of <see cref="DynamicHlsHelper"/>.</param>
        /// <param name="encodingHelper">Instance of <see cref="EncodingHelper"/>.</param>
        /// <param name="dynamicHlsPlaylistGenerator">Instance of <see cref="IDynamicHlsPlaylistGenerator"/>.</param>
        public DynamicHlsController(
            ILibraryManager libraryManager,
            IUserManager userManager,
            IDlnaManager dlnaManager,
            IAuthorizationContext authContext,
            IMediaSourceManager mediaSourceManager,
            IServerConfigurationManager serverConfigurationManager,
            IMediaEncoder mediaEncoder,
            IFileSystem fileSystem,
            IDeviceManager deviceManager,
            TranscodingJobHelper transcodingJobHelper,
            ILogger<DynamicHlsController> logger,
            DynamicHlsHelper dynamicHlsHelper,
            EncodingHelper encodingHelper,
            IDynamicHlsPlaylistGenerator dynamicHlsPlaylistGenerator)
        {
            _libraryManager = libraryManager;
            _userManager = userManager;
            _dlnaManager = dlnaManager;
            _authContext = authContext;
            _mediaSourceManager = mediaSourceManager;
            _serverConfigurationManager = serverConfigurationManager;
            _mediaEncoder = mediaEncoder;
            _fileSystem = fileSystem;
            _deviceManager = deviceManager;
            _transcodingJobHelper = transcodingJobHelper;
            _logger = logger;
            _dynamicHlsHelper = dynamicHlsHelper;
            _encodingHelper = encodingHelper;
            _dynamicHlsPlaylistGenerator = dynamicHlsPlaylistGenerator;

            _encodingOptions = serverConfigurationManager.GetEncodingOptions();
        }

        /// <summary>
        /// Gets a hls live stream.
        /// </summary>
        /// <param name="itemId">The item id.</param>
        /// <param name="container">The audio container.</param>
        /// <param name="static">Optional. If true, the original file will be streamed statically without any encoding. Use either no url extension or the original file extension. true/false.</param>
        /// <param name="params">The streaming parameters.</param>
        /// <param name="tag">The tag.</param>
        /// <param name="deviceProfileId">Optional. The dlna device profile id to utilize.</param>
        /// <param name="playSessionId">The play session id.</param>
        /// <param name="segmentContainer">The segment container.</param>
        /// <param name="segmentLength">The segment lenght.</param>
        /// <param name="minSegments">The minimum number of segments.</param>
        /// <param name="mediaSourceId">The media version id, if playing an alternate version.</param>
        /// <param name="deviceId">The device id of the client requesting. Used to stop encoding processes when needed.</param>
        /// <param name="audioCodec">Optional. Specify a audio codec to encode to, e.g. mp3. If omitted the server will auto-select using the url's extension. Options: aac, mp3, vorbis, wma.</param>
        /// <param name="enableAutoStreamCopy">Whether or not to allow automatic stream copy if requested values match the original source. Defaults to true.</param>
        /// <param name="allowVideoStreamCopy">Whether or not to allow copying of the video stream url.</param>
        /// <param name="allowAudioStreamCopy">Whether or not to allow copying of the audio stream url.</param>
        /// <param name="breakOnNonKeyFrames">Optional. Whether to break on non key frames.</param>
        /// <param name="audioSampleRate">Optional. Specify a specific audio sample rate, e.g. 44100.</param>
        /// <param name="maxAudioBitDepth">Optional. The maximum audio bit depth.</param>
        /// <param name="audioBitRate">Optional. Specify an audio bitrate to encode to, e.g. 128000. If omitted this will be left to encoder defaults.</param>
        /// <param name="audioChannels">Optional. Specify a specific number of audio channels to encode to, e.g. 2.</param>
        /// <param name="maxAudioChannels">Optional. Specify a maximum number of audio channels to encode to, e.g. 2.</param>
        /// <param name="profile">Optional. Specify a specific an encoder profile (varies by encoder), e.g. main, baseline, high.</param>
        /// <param name="level">Optional. Specify a level for the encoder profile (varies by encoder), e.g. 3, 3.1.</param>
        /// <param name="framerate">Optional. A specific video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
        /// <param name="maxFramerate">Optional. A specific maximum video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
        /// <param name="copyTimestamps">Whether or not to copy timestamps when transcoding with an offset. Defaults to false.</param>
        /// <param name="startTimeTicks">Optional. Specify a starting offset, in ticks. 1 tick = 10000 ms.</param>
        /// <param name="width">Optional. The fixed horizontal resolution of the encoded video.</param>
        /// <param name="height">Optional. The fixed vertical resolution of the encoded video.</param>
        /// <param name="videoBitRate">Optional. Specify a video bitrate to encode to, e.g. 500000. If omitted this will be left to encoder defaults.</param>
        /// <param name="subtitleStreamIndex">Optional. The index of the subtitle stream to use. If omitted no subtitles will be used.</param>
        /// <param name="subtitleMethod">Optional. Specify the subtitle delivery method.</param>
        /// <param name="maxRefFrames">Optional.</param>
        /// <param name="maxVideoBitDepth">Optional. The maximum video bit depth.</param>
        /// <param name="requireAvc">Optional. Whether to require avc.</param>
        /// <param name="deInterlace">Optional. Whether to deinterlace the video.</param>
        /// <param name="requireNonAnamorphic">Optional. Whether to require a non anamorphic stream.</param>
        /// <param name="transcodingMaxAudioChannels">Optional. The maximum number of audio channels to transcode.</param>
        /// <param name="cpuCoreLimit">Optional. The limit of how many cpu cores to use.</param>
        /// <param name="liveStreamId">The live stream id.</param>
        /// <param name="enableMpegtsM2TsMode">Optional. Whether to enable the MpegtsM2Ts mode.</param>
        /// <param name="videoCodec">Optional. Specify a video codec to encode to, e.g. h264. If omitted the server will auto-select using the url's extension. Options: h265, h264, mpeg4, theora, vp8, vp9, vpx (deprecated), wmv.</param>
        /// <param name="subtitleCodec">Optional. Specify a subtitle codec to encode to.</param>
        /// <param name="transcodeReasons">Optional. The transcoding reason.</param>
        /// <param name="audioStreamIndex">Optional. The index of the audio stream to use. If omitted the first audio stream will be used.</param>
        /// <param name="videoStreamIndex">Optional. The index of the video stream to use. If omitted the first video stream will be used.</param>
        /// <param name="context">Optional. The <see cref="EncodingContext"/>.</param>
        /// <param name="streamOptions">Optional. The streaming options.</param>
        /// <param name="maxWidth">Optional. The max width.</param>
        /// <param name="maxHeight">Optional. The max height.</param>
        /// <param name="enableSubtitlesInManifest">Optional. Whether to enable subtitles in the manifest.</param>
        /// <response code="200">Hls live stream retrieved.</response>
        /// <returns>A <see cref="FileResult"/> containing the hls file.</returns>
        [HttpGet("Videos/{itemId}/live.m3u8")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesPlaylistFile]
        public async Task<ActionResult> GetLiveHlsStream(
            [FromRoute, Required] Guid itemId,
            [FromQuery] string? container,
            [FromQuery] bool? @static,
            [FromQuery] string? @params,
            [FromQuery] string? tag,
            [FromQuery] string? deviceProfileId,
            [FromQuery] string? playSessionId,
            [FromQuery] string? segmentContainer,
            [FromQuery] int? segmentLength,
            [FromQuery] int? minSegments,
            [FromQuery] string? mediaSourceId,
            [FromQuery] string? deviceId,
            [FromQuery] string? audioCodec,
            [FromQuery] bool? enableAutoStreamCopy,
            [FromQuery] bool? allowVideoStreamCopy,
            [FromQuery] bool? allowAudioStreamCopy,
            [FromQuery] bool? breakOnNonKeyFrames,
            [FromQuery] int? audioSampleRate,
            [FromQuery] int? maxAudioBitDepth,
            [FromQuery] int? audioBitRate,
            [FromQuery] int? audioChannels,
            [FromQuery] int? maxAudioChannels,
            [FromQuery] string? profile,
            [FromQuery] string? level,
            [FromQuery] float? framerate,
            [FromQuery] float? maxFramerate,
            [FromQuery] bool? copyTimestamps,
            [FromQuery] long? startTimeTicks,
            [FromQuery] int? width,
            [FromQuery] int? height,
            [FromQuery] int? videoBitRate,
            [FromQuery] int? subtitleStreamIndex,
            [FromQuery] SubtitleDeliveryMethod? subtitleMethod,
            [FromQuery] int? maxRefFrames,
            [FromQuery] int? maxVideoBitDepth,
            [FromQuery] bool? requireAvc,
            [FromQuery] bool? deInterlace,
            [FromQuery] bool? requireNonAnamorphic,
            [FromQuery] int? transcodingMaxAudioChannels,
            [FromQuery] int? cpuCoreLimit,
            [FromQuery] string? liveStreamId,
            [FromQuery] bool? enableMpegtsM2TsMode,
            [FromQuery] string? videoCodec,
            [FromQuery] string? subtitleCodec,
            [FromQuery] string? transcodeReasons,
            [FromQuery] int? audioStreamIndex,
            [FromQuery] int? videoStreamIndex,
            [FromQuery] EncodingContext? context,
            [FromQuery] Dictionary<string, string> streamOptions,
            [FromQuery] int? maxWidth,
            [FromQuery] int? maxHeight,
            [FromQuery] bool? enableSubtitlesInManifest)
        {
            VideoRequestDto streamingRequest = new VideoRequestDto
            {
                Id = itemId,
                Container = container,
                Static = @static ?? false,
                Params = @params,
                Tag = tag,
                DeviceProfileId = deviceProfileId,
                PlaySessionId = playSessionId,
                SegmentContainer = segmentContainer,
                SegmentLength = segmentLength,
                MinSegments = minSegments,
                MediaSourceId = mediaSourceId,
                DeviceId = deviceId,
                AudioCodec = audioCodec,
                EnableAutoStreamCopy = enableAutoStreamCopy ?? true,
                AllowAudioStreamCopy = allowAudioStreamCopy ?? true,
                AllowVideoStreamCopy = allowVideoStreamCopy ?? true,
                BreakOnNonKeyFrames = breakOnNonKeyFrames ?? false,
                AudioSampleRate = audioSampleRate,
                MaxAudioChannels = maxAudioChannels,
                AudioBitRate = audioBitRate,
                MaxAudioBitDepth = maxAudioBitDepth,
                AudioChannels = audioChannels,
                Profile = profile,
                Level = level,
                Framerate = framerate,
                MaxFramerate = maxFramerate,
                CopyTimestamps = copyTimestamps ?? false,
                StartTimeTicks = startTimeTicks,
                Width = width,
                Height = height,
                VideoBitRate = videoBitRate,
                SubtitleStreamIndex = subtitleStreamIndex,
                SubtitleMethod = subtitleMethod ?? SubtitleDeliveryMethod.Encode,
                MaxRefFrames = maxRefFrames,
                MaxVideoBitDepth = maxVideoBitDepth,
                RequireAvc = requireAvc ?? false,
                DeInterlace = deInterlace ?? false,
                RequireNonAnamorphic = requireNonAnamorphic ?? false,
                TranscodingMaxAudioChannels = transcodingMaxAudioChannels,
                CpuCoreLimit = cpuCoreLimit,
                LiveStreamId = liveStreamId,
                EnableMpegtsM2TsMode = enableMpegtsM2TsMode ?? false,
                VideoCodec = videoCodec,
                SubtitleCodec = subtitleCodec,
                TranscodeReasons = transcodeReasons,
                AudioStreamIndex = audioStreamIndex,
                VideoStreamIndex = videoStreamIndex,
                Context = context ?? EncodingContext.Streaming,
                StreamOptions = streamOptions,
                MaxHeight = maxHeight,
                MaxWidth = maxWidth,
                EnableSubtitlesInManifest = enableSubtitlesInManifest ?? true
            };

            // CTS lifecycle is managed internally.
            var cancellationTokenSource = new CancellationTokenSource();
            // Due to CTS.Token calling ThrowIfDisposed (https://github.com/dotnet/runtime/issues/29970) we have to "cache" the token
            // since it gets disposed when ffmpeg exits
            var cancellationToken = cancellationTokenSource.Token;
            var state = await StreamingHelpers.GetStreamingState(
                    streamingRequest,
                    Request,
                    _authContext,
                    _mediaSourceManager,
                    _userManager,
                    _libraryManager,
                    _serverConfigurationManager,
                    _mediaEncoder,
                    _encodingHelper,
                    _dlnaManager,
                    _deviceManager,
                    _transcodingJobHelper,
                    TranscodingJobType,
                    cancellationToken)
                .ConfigureAwait(false);

            TranscodingJobDto? job = null;
            var playlistPath = Path.ChangeExtension(state.OutputFilePath, ".m3u8");

            if (!System.IO.File.Exists(playlistPath))
            {
                var transcodingLock = _transcodingJobHelper.GetTranscodingLock(playlistPath);
                await transcodingLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!System.IO.File.Exists(playlistPath))
                    {
                        // If the playlist doesn't already exist, startup ffmpeg
                        try
                        {
                            job = await _transcodingJobHelper.StartFfMpeg(
                                    state,
                                    playlistPath,
                                    GetCommandLineArguments(playlistPath, state, true, 0),
                                    Request,
                                    TranscodingJobType,
                                    cancellationTokenSource)
                                .ConfigureAwait(false);
                            job.IsLiveOutput = true;
                        }
                        catch
                        {
                            state.Dispose();
                            throw;
                        }

                        minSegments = state.MinSegments;
                        if (minSegments > 0)
                        {
                            await HlsHelpers.WaitForMinimumSegmentCount(playlistPath, minSegments, _logger, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    transcodingLock.Release();
                }
            }

            job ??= _transcodingJobHelper.OnTranscodeBeginRequest(playlistPath, TranscodingJobType);

            if (job != null)
            {
                _transcodingJobHelper.OnTranscodeEndRequest(job);
            }

            var playlistText = HlsHelpers.GetLivePlaylistText(playlistPath, state);

            return Content(playlistText, MimeTypes.GetMimeType("playlist.m3u8"));
        }

        /// <summary>
        /// Gets a video hls playlist stream.
        /// </summary>
        /// <param name="itemId">The item id.</param>
        /// <param name="static">Optional. If true, the original file will be streamed statically without any encoding. Use either no url extension or the original file extension. true/false.</param>
        /// <param name="params">The streaming parameters.</param>
        /// <param name="tag">The tag.</param>
        /// <param name="deviceProfileId">Optional. The dlna device profile id to utilize.</param>
        /// <param name="playSessionId">The play session id.</param>
        /// <param name="segmentContainer">The segment container.</param>
        /// <param name="segmentLength">The segment length.</param>
        /// <param name="minSegments">The minimum number of segments.</param>
        /// <param name="mediaSourceId">The media version id, if playing an alternate version.</param>
        /// <param name="deviceId">The device id of the client requesting. Used to stop encoding processes when needed.</param>
        /// <param name="audioCodec">Optional. Specify a audio codec to encode to, e.g. mp3. If omitted the server will auto-select using the url's extension. Options: aac, mp3, vorbis, wma.</param>
        /// <param name="enableAutoStreamCopy">Whether or not to allow automatic stream copy if requested values match the original source. Defaults to true.</param>
        /// <param name="allowVideoStreamCopy">Whether or not to allow copying of the video stream url.</param>
        /// <param name="allowAudioStreamCopy">Whether or not to allow copying of the audio stream url.</param>
        /// <param name="breakOnNonKeyFrames">Optional. Whether to break on non key frames.</param>
        /// <param name="audioSampleRate">Optional. Specify a specific audio sample rate, e.g. 44100.</param>
        /// <param name="maxAudioBitDepth">Optional. The maximum audio bit depth.</param>
        /// <param name="audioBitRate">Optional. Specify an audio bitrate to encode to, e.g. 128000. If omitted this will be left to encoder defaults.</param>
        /// <param name="audioChannels">Optional. Specify a specific number of audio channels to encode to, e.g. 2.</param>
        /// <param name="maxAudioChannels">Optional. Specify a maximum number of audio channels to encode to, e.g. 2.</param>
        /// <param name="profile">Optional. Specify a specific an encoder profile (varies by encoder), e.g. main, baseline, high.</param>
        /// <param name="level">Optional. Specify a level for the encoder profile (varies by encoder), e.g. 3, 3.1.</param>
        /// <param name="framerate">Optional. A specific video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
        /// <param name="maxFramerate">Optional. A specific maximum video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
        /// <param name="copyTimestamps">Whether or not to copy timestamps when transcoding with an offset. Defaults to false.</param>
        /// <param name="startTimeTicks">Optional. Specify a starting offset, in ticks. 1 tick = 10000 ms.</param>
        /// <param name="width">Optional. The fixed horizontal resolution of the encoded video.</param>
        /// <param name="height">Optional. The fixed vertical resolution of the encoded video.</param>
        /// <param name="maxWidth">Optional. The maximum horizontal resolution of the encoded video.</param>
        /// <param name="maxHeight">Optional. The maximum vertical resolution of the encoded video.</param>
        /// <param name="videoBitRate">Optional. Specify a video bitrate to encode to, e.g. 500000. If omitted this will be left to encoder defaults.</param>
        /// <param name="subtitleStreamIndex">Optional. The index of the subtitle stream to use. If omitted no subtitles will be used.</param>
        /// <param name="subtitleMethod">Optional. Specify the subtitle delivery method.</param>
        /// <param name="maxRefFrames">Optional.</param>
        /// <param name="maxVideoBitDepth">Optional. The maximum video bit depth.</param>
        /// <param name="requireAvc">Optional. Whether to require avc.</param>
        /// <param name="deInterlace">Optional. Whether to deinterlace the video.</param>
        /// <param name="requireNonAnamorphic">Optional. Whether to require a non anamorphic stream.</param>
        /// <param name="transcodingMaxAudioChannels">Optional. The maximum number of audio channels to transcode.</param>
        /// <param name="cpuCoreLimit">Optional. The limit of how many cpu cores to use.</param>
        /// <param name="liveStreamId">The live stream id.</param>
        /// <param name="enableMpegtsM2TsMode">Optional. Whether to enable the MpegtsM2Ts mode.</param>
        /// <param name="videoCodec">Optional. Specify a video codec to encode to, e.g. h264. If omitted the server will auto-select using the url's extension. Options: h265, h264, mpeg4, theora, vp8, vp9, vpx (deprecated), wmv.</param>
        /// <param name="subtitleCodec">Optional. Specify a subtitle codec to encode to.</param>
        /// <param name="transcodeReasons">Optional. The transcoding reason.</param>
        /// <param name="audioStreamIndex">Optional. The index of the audio stream to use. If omitted the first audio stream will be used.</param>
        /// <param name="videoStreamIndex">Optional. The index of the video stream to use. If omitted the first video stream will be used.</param>
        /// <param name="context">Optional. The <see cref="EncodingContext"/>.</param>
        /// <param name="streamOptions">Optional. The streaming options.</param>
        /// <param name="enableAdaptiveBitrateStreaming">Enable adaptive bitrate streaming.</param>
        /// <response code="200">Video stream returned.</response>
        /// <returns>A <see cref="FileResult"/> containing the playlist file.</returns>
        [HttpGet("Videos/{itemId}/master.m3u8")]
        [HttpHead("Videos/{itemId}/master.m3u8", Name = "HeadMasterHlsVideoPlaylist")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesPlaylistFile]
        public async Task<ActionResult> GetMasterHlsVideoPlaylist(
            [FromRoute, Required] Guid itemId,
            [FromQuery] bool? @static,
            [FromQuery] string? @params,
            [FromQuery] string? tag,
            [FromQuery] string? deviceProfileId,
            [FromQuery] string? playSessionId,
            [FromQuery] string? segmentContainer,
            [FromQuery] int? segmentLength,
            [FromQuery] int? minSegments,
            [FromQuery, Required] string mediaSourceId,
            [FromQuery] string? deviceId,
            [FromQuery] string? audioCodec,
            [FromQuery] bool? enableAutoStreamCopy,
            [FromQuery] bool? allowVideoStreamCopy,
            [FromQuery] bool? allowAudioStreamCopy,
            [FromQuery] bool? breakOnNonKeyFrames,
            [FromQuery] int? audioSampleRate,
            [FromQuery] int? maxAudioBitDepth,
            [FromQuery] int? audioBitRate,
            [FromQuery] int? audioChannels,
            [FromQuery] int? maxAudioChannels,
            [FromQuery] string? profile,
            [FromQuery] string? level,
            [FromQuery] float? framerate,
            [FromQuery] float? maxFramerate,
            [FromQuery] bool? copyTimestamps,
            [FromQuery] long? startTimeTicks,
            [FromQuery] int? width,
            [FromQuery] int? height,
            [FromQuery] int? maxWidth,
            [FromQuery] int? maxHeight,
            [FromQuery] int? videoBitRate,
            [FromQuery] int? subtitleStreamIndex,
            [FromQuery] SubtitleDeliveryMethod? subtitleMethod,
            [FromQuery] int? maxRefFrames,
            [FromQuery] int? maxVideoBitDepth,
            [FromQuery] bool? requireAvc,
            [FromQuery] bool? deInterlace,
            [FromQuery] bool? requireNonAnamorphic,
            [FromQuery] int? transcodingMaxAudioChannels,
            [FromQuery] int? cpuCoreLimit,
            [FromQuery] string? liveStreamId,
            [FromQuery] bool? enableMpegtsM2TsMode,
            [FromQuery] string? videoCodec,
            [FromQuery] string? subtitleCodec,
            [FromQuery] string? transcodeReasons,
            [FromQuery] int? audioStreamIndex,
            [FromQuery] int? videoStreamIndex,
            [FromQuery] EncodingContext? context,
            [FromQuery] Dictionary<string, string> streamOptions,
            [FromQuery] bool enableAdaptiveBitrateStreaming = true)
        {
            var streamingRequest = new HlsVideoRequestDto
            {
                Id = itemId,
                Static = @static ?? false,
                Params = @params,
                Tag = tag,
                DeviceProfileId = deviceProfileId,
                PlaySessionId = playSessionId,
                SegmentContainer = segmentContainer,
                SegmentLength = segmentLength,
                MinSegments = minSegments,
                MediaSourceId = mediaSourceId,
                DeviceId = deviceId,
                AudioCodec = audioCodec,
                EnableAutoStreamCopy = enableAutoStreamCopy ?? true,
                AllowAudioStreamCopy = allowAudioStreamCopy ?? true,
                AllowVideoStreamCopy = allowVideoStreamCopy ?? true,
                BreakOnNonKeyFrames = breakOnNonKeyFrames ?? false,
                AudioSampleRate = audioSampleRate,
                MaxAudioChannels = maxAudioChannels,
                AudioBitRate = audioBitRate,
                MaxAudioBitDepth = maxAudioBitDepth,
                AudioChannels = audioChannels,
                Profile = profile,
                Level = level,
                Framerate = framerate,
                MaxFramerate = maxFramerate,
                CopyTimestamps = copyTimestamps ?? false,
                StartTimeTicks = startTimeTicks,
                Width = width,
                Height = height,
                MaxWidth = maxWidth,
                MaxHeight = maxHeight,
                VideoBitRate = videoBitRate,
                SubtitleStreamIndex = subtitleStreamIndex,
                SubtitleMethod = subtitleMethod ?? SubtitleDeliveryMethod.Encode,
                MaxRefFrames = maxRefFrames,
                MaxVideoBitDepth = maxVideoBitDepth,
                RequireAvc = requireAvc ?? false,
                DeInterlace = deInterlace ?? false,
                RequireNonAnamorphic = requireNonAnamorphic ?? false,
                TranscodingMaxAudioChannels = transcodingMaxAudioChannels,
                CpuCoreLimit = cpuCoreLimit,
                LiveStreamId = liveStreamId,
                EnableMpegtsM2TsMode = enableMpegtsM2TsMode ?? false,
                VideoCodec = videoCodec,
                SubtitleCodec = subtitleCodec,
                TranscodeReasons = transcodeReasons,
                AudioStreamIndex = audioStreamIndex,
                VideoStreamIndex = videoStreamIndex,
                Context = context ?? EncodingContext.Streaming,
                StreamOptions = streamOptions,
                EnableAdaptiveBitrateStreaming = enableAdaptiveBitrateStreaming
            };

            return await _dynamicHlsHelper.GetMasterHlsPlaylist(TranscodingJobType, streamingRequest, enableAdaptiveBitrateStreaming).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets an audio hls playlist stream.
        /// </summary>
        /// <param name="itemId">The item id.</param>
        /// <param name="static">Optional. If true, the original file will be streamed statically without any encoding. Use either no url extension or the original file extension. true/false.</param>
        /// <param name="params">The streaming parameters.</param>
        /// <param name="tag">The tag.</param>
        /// <param name="deviceProfileId">Optional. The dlna device profile id to utilize.</param>
        /// <param name="playSessionId">The play session id.</param>
        /// <param name="segmentContainer">The segment container.</param>
        /// <param name="segmentLength">The segment length.</param>
        /// <param name="minSegments">The minimum number of segments.</param>
        /// <param name="mediaSourceId">The media version id, if playing an alternate version.</param>
        /// <param name="deviceId">The device id of the client requesting. Used to stop encoding processes when needed.</param>
        /// <param name="audioCodec">Optional. Specify a audio codec to encode to, e.g. mp3. If omitted the server will auto-select using the url's extension. Options: aac, mp3, vorbis, wma.</param>
        /// <param name="enableAutoStreamCopy">Whether or not to allow automatic stream copy if requested values match the original source. Defaults to true.</param>
        /// <param name="allowVideoStreamCopy">Whether or not to allow copying of the video stream url.</param>
        /// <param name="allowAudioStreamCopy">Whether or not to allow copying of the audio stream url.</param>
        /// <param name="breakOnNonKeyFrames">Optional. Whether to break on non key frames.</param>
        /// <param name="audioSampleRate">Optional. Specify a specific audio sample rate, e.g. 44100.</param>
        /// <param name="maxAudioBitDepth">Optional. The maximum audio bit depth.</param>
        /// <param name="maxStreamingBitrate">Optional. The maximum streaming bitrate.</param>
        /// <param name="audioBitRate">Optional. Specify an audio bitrate to encode to, e.g. 128000. If omitted this will be left to encoder defaults.</param>
        /// <param name="audioChannels">Optional. Specify a specific number of audio channels to encode to, e.g. 2.</param>
        /// <param name="maxAudioChannels">Optional. Specify a maximum number of audio channels to encode to, e.g. 2.</param>
        /// <param name="profile">Optional. Specify a specific an encoder profile (varies by encoder), e.g. main, baseline, high.</param>
        /// <param name="level">Optional. Specify a level for the encoder profile (varies by encoder), e.g. 3, 3.1.</param>
        /// <param name="framerate">Optional. A specific video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
        /// <param name="maxFramerate">Optional. A specific maximum video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
        /// <param name="copyTimestamps">Whether or not to copy timestamps when transcoding with an offset. Defaults to false.</param>
        /// <param name="startTimeTicks">Optional. Specify a starting offset, in ticks. 1 tick = 10000 ms.</param>
        /// <param name="width">Optional. The fixed horizontal resolution of the encoded video.</param>
        /// <param name="height">Optional. The fixed vertical resolution of the encoded video.</param>
        /// <param name="videoBitRate">Optional. Specify a video bitrate to encode to, e.g. 500000. If omitted this will be left to encoder defaults.</param>
        /// <param name="subtitleStreamIndex">Optional. The index of the subtitle stream to use. If omitted no subtitles will be used.</param>
        /// <param name="subtitleMethod">Optional. Specify the subtitle delivery method.</param>
        /// <param name="maxRefFrames">Optional.</param>
        /// <param name="maxVideoBitDepth">Optional. The maximum video bit depth.</param>
        /// <param name="requireAvc">Optional. Whether to require avc.</param>
        /// <param name="deInterlace">Optional. Whether to deinterlace the video.</param>
        /// <param name="requireNonAnamorphic">Optional. Whether to require a non anamorphic stream.</param>
        /// <param name="transcodingMaxAudioChannels">Optional. The maximum number of audio channels to transcode.</param>
        /// <param name="cpuCoreLimit">Optional. The limit of how many cpu cores to use.</param>
        /// <param name="liveStreamId">The live stream id.</param>
        /// <param name="enableMpegtsM2TsMode">Optional. Whether to enable the MpegtsM2Ts mode.</param>
        /// <param name="videoCodec">Optional. Specify a video codec to encode to, e.g. h264. If omitted the server will auto-select using the url's extension. Options: h265, h264, mpeg4, theora, vp8, vp9, vpx (deprecated), wmv.</param>
        /// <param name="subtitleCodec">Optional. Specify a subtitle codec to encode to.</param>
        /// <param name="transcodeReasons">Optional. The transcoding reason.</param>
        /// <param name="audioStreamIndex">Optional. The index of the audio stream to use. If omitted the first audio stream will be used.</param>
        /// <param name="videoStreamIndex">Optional. The index of the video stream to use. If omitted the first video stream will be used.</param>
        /// <param name="context">Optional. The <see cref="EncodingContext"/>.</param>
        /// <param name="streamOptions">Optional. The streaming options.</param>
        /// <param name="enableAdaptiveBitrateStreaming">Enable adaptive bitrate streaming.</param>
        /// <response code="200">Audio stream returned.</response>
        /// <returns>A <see cref="FileResult"/> containing the playlist file.</returns>
        [HttpGet("Audio/{itemId}/master.m3u8")]
        [HttpHead("Audio/{itemId}/master.m3u8", Name = "HeadMasterHlsAudioPlaylist")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesPlaylistFile]
        public async Task<ActionResult> GetMasterHlsAudioPlaylist(
            [FromRoute, Required] Guid itemId,
            [FromQuery] bool? @static,
            [FromQuery] string? @params,
            [FromQuery] string? tag,
            [FromQuery] string? deviceProfileId,
            [FromQuery] string? playSessionId,
            [FromQuery] string? segmentContainer,
            [FromQuery] int? segmentLength,
            [FromQuery] int? minSegments,
            [FromQuery, Required] string mediaSourceId,
            [FromQuery] string? deviceId,
            [FromQuery] string? audioCodec,
            [FromQuery] bool? enableAutoStreamCopy,
            [FromQuery] bool? allowVideoStreamCopy,
            [FromQuery] bool? allowAudioStreamCopy,
            [FromQuery] bool? breakOnNonKeyFrames,
            [FromQuery] int? audioSampleRate,
            [FromQuery] int? maxAudioBitDepth,
            [FromQuery] int? maxStreamingBitrate,
            [FromQuery] int? audioBitRate,
            [FromQuery] int? audioChannels,
            [FromQuery] int? maxAudioChannels,
            [FromQuery] string? profile,
            [FromQuery] string? level,
            [FromQuery] float? framerate,
            [FromQuery] float? maxFramerate,
            [FromQuery] bool? copyTimestamps,
            [FromQuery] long? startTimeTicks,
            [FromQuery] int? width,
            [FromQuery] int? height,
            [FromQuery] int? videoBitRate,
            [FromQuery] int? subtitleStreamIndex,
            [FromQuery] SubtitleDeliveryMethod? subtitleMethod,
            [FromQuery] int? maxRefFrames,
            [FromQuery] int? maxVideoBitDepth,
            [FromQuery] bool? requireAvc,
            [FromQuery] bool? deInterlace,
            [FromQuery] bool? requireNonAnamorphic,
            [FromQuery] int? transcodingMaxAudioChannels,
            [FromQuery] int? cpuCoreLimit,
            [FromQuery] string? liveStreamId,
            [FromQuery] bool? enableMpegtsM2TsMode,
            [FromQuery] string? videoCodec,
            [FromQuery] string? subtitleCodec,
            [FromQuery] string? transcodeReasons,
            [FromQuery] int? audioStreamIndex,
            [FromQuery] int? videoStreamIndex,
            [FromQuery] EncodingContext? context,
            [FromQuery] Dictionary<string, string> streamOptions,
            [FromQuery] bool enableAdaptiveBitrateStreaming = true)
        {
            var streamingRequest = new HlsAudioRequestDto
            {
                Id = itemId,
                Static = @static ?? false,
                Params = @params,
                Tag = tag,
                DeviceProfileId = deviceProfileId,
                PlaySessionId = playSessionId,
                SegmentContainer = segmentContainer,
                SegmentLength = segmentLength,
                MinSegments = minSegments,
                MediaSourceId = mediaSourceId,
                DeviceId = deviceId,
                AudioCodec = audioCodec,
                EnableAutoStreamCopy = enableAutoStreamCopy ?? true,
                AllowAudioStreamCopy = allowAudioStreamCopy ?? true,
                AllowVideoStreamCopy = allowVideoStreamCopy ?? true,
                BreakOnNonKeyFrames = breakOnNonKeyFrames ?? false,
                AudioSampleRate = audioSampleRate,
                MaxAudioChannels = maxAudioChannels,
                AudioBitRate = audioBitRate ?? maxStreamingBitrate,
                MaxAudioBitDepth = maxAudioBitDepth,
                AudioChannels = audioChannels,
                Profile = profile,
                Level = level,
                Framerate = framerate,
                MaxFramerate = maxFramerate,
                CopyTimestamps = copyTimestamps ?? false,
                StartTimeTicks = startTimeTicks,
                Width = width,
                Height = height,
                VideoBitRate = videoBitRate,
                SubtitleStreamIndex = subtitleStreamIndex,
                SubtitleMethod = subtitleMethod ?? SubtitleDeliveryMethod.Encode,
                MaxRefFrames = maxRefFrames,
                MaxVideoBitDepth = maxVideoBitDepth,
                RequireAvc = requireAvc ?? false,
                DeInterlace = deInterlace ?? false,
                RequireNonAnamorphic = requireNonAnamorphic ?? false,
                TranscodingMaxAudioChannels = transcodingMaxAudioChannels,
                CpuCoreLimit = cpuCoreLimit,
                LiveStreamId = liveStreamId,
                EnableMpegtsM2TsMode = enableMpegtsM2TsMode ?? false,
                VideoCodec = videoCodec,
                SubtitleCodec = subtitleCodec,
                TranscodeReasons = transcodeReasons,
                AudioStreamIndex = audioStreamIndex,
                VideoStreamIndex = videoStreamIndex,
                Context = context ?? EncodingContext.Streaming,
                StreamOptions = streamOptions,
                EnableAdaptiveBitrateStreaming = enableAdaptiveBitrateStreaming
            };

            return await _dynamicHlsHelper.GetMasterHlsPlaylist(TranscodingJobType, streamingRequest, enableAdaptiveBitrateStreaming).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets a video stream using HTTP live streaming.
        /// </summary>
        /// <param name="itemId">The item id.</param>
        /// <param name="static">Optional. If true, the original file will be streamed statically without any encoding. Use either no url extension or the original file extension. true/false.</param>
        /// <param name="params">The streaming parameters.</param>
        /// <param name="tag">The tag.</param>
        /// <param name="deviceProfileId">Optional. The dlna device profile id to utilize.</param>
        /// <param name="playSessionId">The play session id.</param>
        /// <param name="segmentContainer">The segment container.</param>
        /// <param name="segmentLength">The segment length.</param>
        /// <param name="minSegments">The minimum number of segments.</param>
        /// <param name="mediaSourceId">The media version id, if playing an alternate version.</param>
        /// <param name="deviceId">The device id of the client requesting. Used to stop encoding processes when needed.</param>
        /// <param name="audioCodec">Optional. Specify a audio codec to encode to, e.g. mp3. If omitted the server will auto-select using the url's extension. Options: aac, mp3, vorbis, wma.</param>
        /// <param name="enableAutoStreamCopy">Whether or not to allow automatic stream copy if requested values match the original source. Defaults to true.</param>
        /// <param name="allowVideoStreamCopy">Whether or not to allow copying of the video stream url.</param>
        /// <param name="allowAudioStreamCopy">Whether or not to allow copying of the audio stream url.</param>
        /// <param name="breakOnNonKeyFrames">Optional. Whether to break on non key frames.</param>
        /// <param name="audioSampleRate">Optional. Specify a specific audio sample rate, e.g. 44100.</param>
        /// <param name="maxAudioBitDepth">Optional. The maximum audio bit depth.</param>
        /// <param name="audioBitRate">Optional. Specify an audio bitrate to encode to, e.g. 128000. If omitted this will be left to encoder defaults.</param>
        /// <param name="audioChannels">Optional. Specify a specific number of audio channels to encode to, e.g. 2.</param>
        /// <param name="maxAudioChannels">Optional. Specify a maximum number of audio channels to encode to, e.g. 2.</param>
        /// <param name="profile">Optional. Specify a specific an encoder profile (varies by encoder), e.g. main, baseline, high.</param>
        /// <param name="level">Optional. Specify a level for the encoder profile (varies by encoder), e.g. 3, 3.1.</param>
        /// <param name="framerate">Optional. A specific video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
        /// <param name="maxFramerate">Optional. A specific maximum video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
        /// <param name="copyTimestamps">Whether or not to copy timestamps when transcoding with an offset. Defaults to false.</param>
        /// <param name="startTimeTicks">Optional. Specify a starting offset, in ticks. 1 tick = 10000 ms.</param>
        /// <param name="width">Optional. The fixed horizontal resolution of the encoded video.</param>
        /// <param name="height">Optional. The fixed vertical resolution of the encoded video.</param>
        /// <param name="maxWidth">Optional. The maximum horizontal resolution of the encoded video.</param>
        /// <param name="maxHeight">Optional. The maximum vertical resolution of the encoded video.</param>
        /// <param name="videoBitRate">Optional. Specify a video bitrate to encode to, e.g. 500000. If omitted this will be left to encoder defaults.</param>
        /// <param name="subtitleStreamIndex">Optional. The index of the subtitle stream to use. If omitted no subtitles will be used.</param>
        /// <param name="subtitleMethod">Optional. Specify the subtitle delivery method.</param>
        /// <param name="maxRefFrames">Optional.</param>
        /// <param name="maxVideoBitDepth">Optional. The maximum video bit depth.</param>
        /// <param name="requireAvc">Optional. Whether to require avc.</param>
        /// <param name="deInterlace">Optional. Whether to deinterlace the video.</param>
        /// <param name="requireNonAnamorphic">Optional. Whether to require a non anamorphic stream.</param>
        /// <param name="transcodingMaxAudioChannels">Optional. The maximum number of audio channels to transcode.</param>
        /// <param name="cpuCoreLimit">Optional. The limit of how many cpu cores to use.</param>
        /// <param name="liveStreamId">The live stream id.</param>
        /// <param name="enableMpegtsM2TsMode">Optional. Whether to enable the MpegtsM2Ts mode.</param>
        /// <param name="videoCodec">Optional. Specify a video codec to encode to, e.g. h264. If omitted the server will auto-select using the url's extension. Options: h265, h264, mpeg4, theora, vp8, vp9, vpx (deprecated), wmv.</param>
        /// <param name="subtitleCodec">Optional. Specify a subtitle codec to encode to.</param>
        /// <param name="transcodeReasons">Optional. The transcoding reason.</param>
        /// <param name="audioStreamIndex">Optional. The index of the audio stream to use. If omitted the first audio stream will be used.</param>
        /// <param name="videoStreamIndex">Optional. The index of the video stream to use. If omitted the first video stream will be used.</param>
        /// <param name="context">Optional. The <see cref="EncodingContext"/>.</param>
        /// <param name="streamOptions">Optional. The streaming options.</param>
        /// <response code="200">Video stream returned.</response>
        /// <returns>A <see cref="FileResult"/> containing the audio file.</returns>
        [HttpGet("Videos/{itemId}/main.m3u8")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesPlaylistFile]
        public async Task<ActionResult> GetVariantHlsVideoPlaylist(
            [FromRoute, Required] Guid itemId,
            [FromQuery] bool? @static,
            [FromQuery] string? @params,
            [FromQuery] string? tag,
            [FromQuery] string? deviceProfileId,
            [FromQuery] string? playSessionId,
            [FromQuery] string? segmentContainer,
            [FromQuery] int? segmentLength,
            [FromQuery] int? minSegments,
            [FromQuery] string? mediaSourceId,
            [FromQuery] string? deviceId,
            [FromQuery] string? audioCodec,
            [FromQuery] bool? enableAutoStreamCopy,
            [FromQuery] bool? allowVideoStreamCopy,
            [FromQuery] bool? allowAudioStreamCopy,
            [FromQuery] bool? breakOnNonKeyFrames,
            [FromQuery] int? audioSampleRate,
            [FromQuery] int? maxAudioBitDepth,
            [FromQuery] int? audioBitRate,
            [FromQuery] int? audioChannels,
            [FromQuery] int? maxAudioChannels,
            [FromQuery] string? profile,
            [FromQuery] string? level,
            [FromQuery] float? framerate,
            [FromQuery] float? maxFramerate,
            [FromQuery] bool? copyTimestamps,
            [FromQuery] long? startTimeTicks,
            [FromQuery] int? width,
            [FromQuery] int? height,
            [FromQuery] int? maxWidth,
            [FromQuery] int? maxHeight,
            [FromQuery] int? videoBitRate,
            [FromQuery] int? subtitleStreamIndex,
            [FromQuery] SubtitleDeliveryMethod? subtitleMethod,
            [FromQuery] int? maxRefFrames,
            [FromQuery] int? maxVideoBitDepth,
            [FromQuery] bool? requireAvc,
            [FromQuery] bool? deInterlace,
            [FromQuery] bool? requireNonAnamorphic,
            [FromQuery] int? transcodingMaxAudioChannels,
            [FromQuery] int? cpuCoreLimit,
            [FromQuery] string? liveStreamId,
            [FromQuery] bool? enableMpegtsM2TsMode,
            [FromQuery] string? videoCodec,
            [FromQuery] string? subtitleCodec,
            [FromQuery] string? transcodeReasons,
            [FromQuery] int? audioStreamIndex,
            [FromQuery] int? videoStreamIndex,
            [FromQuery] EncodingContext? context,
            [FromQuery] Dictionary<string, string> streamOptions)
        {
            using var cancellationTokenSource = new CancellationTokenSource();
            var streamingRequest = new VideoRequestDto
            {
                Id = itemId,
                Static = @static ?? false,
                Params = @params,
                Tag = tag,
                DeviceProfileId = deviceProfileId,
                PlaySessionId = playSessionId,
                SegmentContainer = segmentContainer,
                SegmentLength = segmentLength,
                MinSegments = minSegments,
                MediaSourceId = mediaSourceId,
                DeviceId = deviceId,
                AudioCodec = audioCodec,
                EnableAutoStreamCopy = enableAutoStreamCopy ?? true,
                AllowAudioStreamCopy = allowAudioStreamCopy ?? true,
                AllowVideoStreamCopy = allowVideoStreamCopy ?? true,
                BreakOnNonKeyFrames = breakOnNonKeyFrames ?? false,
                AudioSampleRate = audioSampleRate,
                MaxAudioChannels = maxAudioChannels,
                AudioBitRate = audioBitRate,
                MaxAudioBitDepth = maxAudioBitDepth,
                AudioChannels = audioChannels,
                Profile = profile,
                Level = level,
                Framerate = framerate,
                MaxFramerate = maxFramerate,
                CopyTimestamps = copyTimestamps ?? false,
                StartTimeTicks = startTimeTicks,
                Width = width,
                Height = height,
                MaxWidth = maxWidth,
                MaxHeight = maxHeight,
                VideoBitRate = videoBitRate,
                SubtitleStreamIndex = subtitleStreamIndex,
                SubtitleMethod = subtitleMethod ?? SubtitleDeliveryMethod.Encode,
                MaxRefFrames = maxRefFrames,
                MaxVideoBitDepth = maxVideoBitDepth,
                RequireAvc = requireAvc ?? false,
                DeInterlace = deInterlace ?? false,
                RequireNonAnamorphic = requireNonAnamorphic ?? false,
                TranscodingMaxAudioChannels = transcodingMaxAudioChannels,
                CpuCoreLimit = cpuCoreLimit,
                LiveStreamId = liveStreamId,
                EnableMpegtsM2TsMode = enableMpegtsM2TsMode ?? false,
                VideoCodec = videoCodec,
                SubtitleCodec = subtitleCodec,
                TranscodeReasons = transcodeReasons,
                AudioStreamIndex = audioStreamIndex,
                VideoStreamIndex = videoStreamIndex,
                Context = context ?? EncodingContext.Streaming,
                StreamOptions = streamOptions
            };

            return await GetVariantPlaylistInternal(streamingRequest, cancellationTokenSource)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Gets an audio stream using HTTP live streaming.
        /// </summary>
        /// <param name="itemId">The item id.</param>
        /// <param name="static">Optional. If true, the original file will be streamed statically without any encoding. Use either no url extension or the original file extension. true/false.</param>
        /// <param name="params">The streaming parameters.</param>
        /// <param name="tag">The tag.</param>
        /// <param name="deviceProfileId">Optional. The dlna device profile id to utilize.</param>
        /// <param name="playSessionId">The play session id.</param>
        /// <param name="segmentContainer">The segment container.</param>
        /// <param name="segmentLength">The segment length.</param>
        /// <param name="minSegments">The minimum number of segments.</param>
        /// <param name="mediaSourceId">The media version id, if playing an alternate version.</param>
        /// <param name="deviceId">The device id of the client requesting. Used to stop encoding processes when needed.</param>
        /// <param name="audioCodec">Optional. Specify a audio codec to encode to, e.g. mp3. If omitted the server will auto-select using the url's extension. Options: aac, mp3, vorbis, wma.</param>
        /// <param name="enableAutoStreamCopy">Whether or not to allow automatic stream copy if requested values match the original source. Defaults to true.</param>
        /// <param name="allowVideoStreamCopy">Whether or not to allow copying of the video stream url.</param>
        /// <param name="allowAudioStreamCopy">Whether or not to allow copying of the audio stream url.</param>
        /// <param name="breakOnNonKeyFrames">Optional. Whether to break on non key frames.</param>
        /// <param name="audioSampleRate">Optional. Specify a specific audio sample rate, e.g. 44100.</param>
        /// <param name="maxAudioBitDepth">Optional. The maximum audio bit depth.</param>
        /// <param name="maxStreamingBitrate">Optional. The maximum streaming bitrate.</param>
        /// <param name="audioBitRate">Optional. Specify an audio bitrate to encode to, e.g. 128000. If omitted this will be left to encoder defaults.</param>
        /// <param name="audioChannels">Optional. Specify a specific number of audio channels to encode to, e.g. 2.</param>
        /// <param name="maxAudioChannels">Optional. Specify a maximum number of audio channels to encode to, e.g. 2.</param>
        /// <param name="profile">Optional. Specify a specific an encoder profile (varies by encoder), e.g. main, baseline, high.</param>
        /// <param name="level">Optional. Specify a level for the encoder profile (varies by encoder), e.g. 3, 3.1.</param>
        /// <param name="framerate">Optional. A specific video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
        /// <param name="maxFramerate">Optional. A specific maximum video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
        /// <param name="copyTimestamps">Whether or not to copy timestamps when transcoding with an offset. Defaults to false.</param>
        /// <param name="startTimeTicks">Optional. Specify a starting offset, in ticks. 1 tick = 10000 ms.</param>
        /// <param name="width">Optional. The fixed horizontal resolution of the encoded video.</param>
        /// <param name="height">Optional. The fixed vertical resolution of the encoded video.</param>
        /// <param name="videoBitRate">Optional. Specify a video bitrate to encode to, e.g. 500000. If omitted this will be left to encoder defaults.</param>
        /// <param name="subtitleStreamIndex">Optional. The index of the subtitle stream to use. If omitted no subtitles will be used.</param>
        /// <param name="subtitleMethod">Optional. Specify the subtitle delivery method.</param>
        /// <param name="maxRefFrames">Optional.</param>
        /// <param name="maxVideoBitDepth">Optional. The maximum video bit depth.</param>
        /// <param name="requireAvc">Optional. Whether to require avc.</param>
        /// <param name="deInterlace">Optional. Whether to deinterlace the video.</param>
        /// <param name="requireNonAnamorphic">Optional. Whether to require a non anamorphic stream.</param>
        /// <param name="transcodingMaxAudioChannels">Optional. The maximum number of audio channels to transcode.</param>
        /// <param name="cpuCoreLimit">Optional. The limit of how many cpu cores to use.</param>
        /// <param name="liveStreamId">The live stream id.</param>
        /// <param name="enableMpegtsM2TsMode">Optional. Whether to enable the MpegtsM2Ts mode.</param>
        /// <param name="videoCodec">Optional. Specify a video codec to encode to, e.g. h264. If omitted the server will auto-select using the url's extension. Options: h265, h264, mpeg4, theora, vpx, wmv.</param>
        /// <param name="subtitleCodec">Optional. Specify a subtitle codec to encode to.</param>
        /// <param name="transcodeReasons">Optional. The transcoding reason.</param>
        /// <param name="audioStreamIndex">Optional. The index of the audio stream to use. If omitted the first audio stream will be used.</param>
        /// <param name="videoStreamIndex">Optional. The index of the video stream to use. If omitted the first video stream will be used.</param>
        /// <param name="context">Optional. The <see cref="EncodingContext"/>.</param>
        /// <param name="streamOptions">Optional. The streaming options.</param>
        /// <response code="200">Audio stream returned.</response>
        /// <returns>A <see cref="FileResult"/> containing the audio file.</returns>
        [HttpGet("Audio/{itemId}/main.m3u8")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesPlaylistFile]
        public async Task<ActionResult> GetVariantHlsAudioPlaylist(
            [FromRoute, Required] Guid itemId,
            [FromQuery] bool? @static,
            [FromQuery] string? @params,
            [FromQuery] string? tag,
            [FromQuery] string? deviceProfileId,
            [FromQuery] string? playSessionId,
            [FromQuery] string? segmentContainer,
            [FromQuery] int? segmentLength,
            [FromQuery] int? minSegments,
            [FromQuery] string? mediaSourceId,
            [FromQuery] string? deviceId,
            [FromQuery] string? audioCodec,
            [FromQuery] bool? enableAutoStreamCopy,
            [FromQuery] bool? allowVideoStreamCopy,
            [FromQuery] bool? allowAudioStreamCopy,
            [FromQuery] bool? breakOnNonKeyFrames,
            [FromQuery] int? audioSampleRate,
            [FromQuery] int? maxAudioBitDepth,
            [FromQuery] int? maxStreamingBitrate,
            [FromQuery] int? audioBitRate,
            [FromQuery] int? audioChannels,
            [FromQuery] int? maxAudioChannels,
            [FromQuery] string? profile,
            [FromQuery] string? level,
            [FromQuery] float? framerate,
            [FromQuery] float? maxFramerate,
            [FromQuery] bool? copyTimestamps,
            [FromQuery] long? startTimeTicks,
            [FromQuery] int? width,
            [FromQuery] int? height,
            [FromQuery] int? videoBitRate,
            [FromQuery] int? subtitleStreamIndex,
            [FromQuery] SubtitleDeliveryMethod? subtitleMethod,
            [FromQuery] int? maxRefFrames,
            [FromQuery] int? maxVideoBitDepth,
            [FromQuery] bool? requireAvc,
            [FromQuery] bool? deInterlace,
            [FromQuery] bool? requireNonAnamorphic,
            [FromQuery] int? transcodingMaxAudioChannels,
            [FromQuery] int? cpuCoreLimit,
            [FromQuery] string? liveStreamId,
            [FromQuery] bool? enableMpegtsM2TsMode,
            [FromQuery] string? videoCodec,
            [FromQuery] string? subtitleCodec,
            [FromQuery] string? transcodeReasons,
            [FromQuery] int? audioStreamIndex,
            [FromQuery] int? videoStreamIndex,
            [FromQuery] EncodingContext? context,
            [FromQuery] Dictionary<string, string> streamOptions)
        {
            using var cancellationTokenSource = new CancellationTokenSource();
            var streamingRequest = new StreamingRequestDto
            {
                Id = itemId,
                Static = @static ?? false,
                Params = @params,
                Tag = tag,
                DeviceProfileId = deviceProfileId,
                PlaySessionId = playSessionId,
                SegmentContainer = segmentContainer,
                SegmentLength = segmentLength,
                MinSegments = minSegments,
                MediaSourceId = mediaSourceId,
                DeviceId = deviceId,
                AudioCodec = audioCodec,
                EnableAutoStreamCopy = enableAutoStreamCopy ?? true,
                AllowAudioStreamCopy = allowAudioStreamCopy ?? true,
                AllowVideoStreamCopy = allowVideoStreamCopy ?? true,
                BreakOnNonKeyFrames = breakOnNonKeyFrames ?? false,
                AudioSampleRate = audioSampleRate,
                MaxAudioChannels = maxAudioChannels,
                AudioBitRate = audioBitRate ?? maxStreamingBitrate,
                MaxAudioBitDepth = maxAudioBitDepth,
                AudioChannels = audioChannels,
                Profile = profile,
                Level = level,
                Framerate = framerate,
                MaxFramerate = maxFramerate,
                CopyTimestamps = copyTimestamps ?? false,
                StartTimeTicks = startTimeTicks,
                Width = width,
                Height = height,
                VideoBitRate = videoBitRate,
                SubtitleStreamIndex = subtitleStreamIndex,
                SubtitleMethod = subtitleMethod ?? SubtitleDeliveryMethod.Encode,
                MaxRefFrames = maxRefFrames,
                MaxVideoBitDepth = maxVideoBitDepth,
                RequireAvc = requireAvc ?? false,
                DeInterlace = deInterlace ?? false,
                RequireNonAnamorphic = requireNonAnamorphic ?? false,
                TranscodingMaxAudioChannels = transcodingMaxAudioChannels,
                CpuCoreLimit = cpuCoreLimit,
                LiveStreamId = liveStreamId,
                EnableMpegtsM2TsMode = enableMpegtsM2TsMode ?? false,
                VideoCodec = videoCodec,
                SubtitleCodec = subtitleCodec,
                TranscodeReasons = transcodeReasons,
                AudioStreamIndex = audioStreamIndex,
                VideoStreamIndex = videoStreamIndex,
                Context = context ?? EncodingContext.Streaming,
                StreamOptions = streamOptions
            };

            return await GetVariantPlaylistInternal(streamingRequest, cancellationTokenSource)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Gets a video stream using HTTP live streaming.
        /// </summary>
        /// <param name="itemId">The item id.</param>
        /// <param name="playlistId">The playlist id.</param>
        /// <param name="segmentId">The segment id.</param>
        /// <param name="container">The video container. Possible values are: ts, webm, asf, wmv, ogv, mp4, m4v, mkv, mpeg, mpg, avi, 3gp, wmv, wtv, m2ts, mov, iso, flv. </param>
        /// <param name="runtimeTicks">The position of the requested segment in ticks.</param>
        /// <param name="actualSegmentLengthTicks">The length of the requested segment in ticks.</param>
        /// <param name="static">Optional. If true, the original file will be streamed statically without any encoding. Use either no url extension or the original file extension. true/false.</param>
        /// <param name="params">The streaming parameters.</param>
        /// <param name="tag">The tag.</param>
        /// <param name="deviceProfileId">Optional. The dlna device profile id to utilize.</param>
        /// <param name="playSessionId">The play session id.</param>
        /// <param name="segmentContainer">The segment container.</param>
        /// <param name="segmentLength">The desired segment length.</param>
        /// <param name="minSegments">The minimum number of segments.</param>
        /// <param name="mediaSourceId">The media version id, if playing an alternate version.</param>
        /// <param name="deviceId">The device id of the client requesting. Used to stop encoding processes when needed.</param>
        /// <param name="audioCodec">Optional. Specify a audio codec to encode to, e.g. mp3. If omitted the server will auto-select using the url's extension. Options: aac, mp3, vorbis, wma.</param>
        /// <param name="enableAutoStreamCopy">Whether or not to allow automatic stream copy if requested values match the original source. Defaults to true.</param>
        /// <param name="allowVideoStreamCopy">Whether or not to allow copying of the video stream url.</param>
        /// <param name="allowAudioStreamCopy">Whether or not to allow copying of the audio stream url.</param>
        /// <param name="breakOnNonKeyFrames">Optional. Whether to break on non key frames.</param>
        /// <param name="audioSampleRate">Optional. Specify a specific audio sample rate, e.g. 44100.</param>
        /// <param name="maxAudioBitDepth">Optional. The maximum audio bit depth.</param>
        /// <param name="audioBitRate">Optional. Specify an audio bitrate to encode to, e.g. 128000. If omitted this will be left to encoder defaults.</param>
        /// <param name="audioChannels">Optional. Specify a specific number of audio channels to encode to, e.g. 2.</param>
        /// <param name="maxAudioChannels">Optional. Specify a maximum number of audio channels to encode to, e.g. 2.</param>
        /// <param name="profile">Optional. Specify a specific an encoder profile (varies by encoder), e.g. main, baseline, high.</param>
        /// <param name="level">Optional. Specify a level for the encoder profile (varies by encoder), e.g. 3, 3.1.</param>
        /// <param name="framerate">Optional. A specific video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
        /// <param name="maxFramerate">Optional. A specific maximum video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
        /// <param name="copyTimestamps">Whether or not to copy timestamps when transcoding with an offset. Defaults to false.</param>
        /// <param name="startTimeTicks">Optional. Specify a starting offset, in ticks. 1 tick = 10000 ms.</param>
        /// <param name="width">Optional. The fixed horizontal resolution of the encoded video.</param>
        /// <param name="height">Optional. The fixed vertical resolution of the encoded video.</param>
        /// <param name="maxWidth">Optional. The maximum horizontal resolution of the encoded video.</param>
        /// <param name="maxHeight">Optional. The maximum vertical resolution of the encoded video.</param>
        /// <param name="videoBitRate">Optional. Specify a video bitrate to encode to, e.g. 500000. If omitted this will be left to encoder defaults.</param>
        /// <param name="subtitleStreamIndex">Optional. The index of the subtitle stream to use. If omitted no subtitles will be used.</param>
        /// <param name="subtitleMethod">Optional. Specify the subtitle delivery method.</param>
        /// <param name="maxRefFrames">Optional.</param>
        /// <param name="maxVideoBitDepth">Optional. The maximum video bit depth.</param>
        /// <param name="requireAvc">Optional. Whether to require avc.</param>
        /// <param name="deInterlace">Optional. Whether to deinterlace the video.</param>
        /// <param name="requireNonAnamorphic">Optional. Whether to require a non anamorphic stream.</param>
        /// <param name="transcodingMaxAudioChannels">Optional. The maximum number of audio channels to transcode.</param>
        /// <param name="cpuCoreLimit">Optional. The limit of how many cpu cores to use.</param>
        /// <param name="liveStreamId">The live stream id.</param>
        /// <param name="enableMpegtsM2TsMode">Optional. Whether to enable the MpegtsM2Ts mode.</param>
        /// <param name="videoCodec">Optional. Specify a video codec to encode to, e.g. h264. If omitted the server will auto-select using the url's extension. Options: h265, h264, mpeg4, theora, vp8, vp9, vpx (deprecated), wmv.</param>
        /// <param name="subtitleCodec">Optional. Specify a subtitle codec to encode to.</param>
        /// <param name="transcodeReasons">Optional. The transcoding reason.</param>
        /// <param name="audioStreamIndex">Optional. The index of the audio stream to use. If omitted the first audio stream will be used.</param>
        /// <param name="videoStreamIndex">Optional. The index of the video stream to use. If omitted the first video stream will be used.</param>
        /// <param name="context">Optional. The <see cref="EncodingContext"/>.</param>
        /// <param name="streamOptions">Optional. The streaming options.</param>
        /// <response code="200">Video stream returned.</response>
        /// <returns>A <see cref="FileResult"/> containing the audio file.</returns>
        [HttpGet("Videos/{itemId}/hls1/{playlistId}/{segmentId}.{container}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesVideoFile]
        [SuppressMessage("Microsoft.Performance", "CA1801:ReviewUnusedParameters", MessageId = "playlistId", Justification = "Imported from ServiceStack")]
        public async Task<ActionResult> GetHlsVideoSegment(
            [FromRoute, Required] Guid itemId,
            [FromRoute, Required] string playlistId,
            [FromRoute, Required] int segmentId,
            [FromRoute, Required] string container,
            [FromQuery, Required] long runtimeTicks,
            [FromQuery, Required] long actualSegmentLengthTicks,
            [FromQuery] bool? @static,
            [FromQuery] string? @params,
            [FromQuery] string? tag,
            [FromQuery] string? deviceProfileId,
            [FromQuery] string? playSessionId,
            [FromQuery] string? segmentContainer,
            [FromQuery] int? segmentLength,
            [FromQuery] int? minSegments,
            [FromQuery] string? mediaSourceId,
            [FromQuery] string? deviceId,
            [FromQuery] string? audioCodec,
            [FromQuery] bool? enableAutoStreamCopy,
            [FromQuery] bool? allowVideoStreamCopy,
            [FromQuery] bool? allowAudioStreamCopy,
            [FromQuery] bool? breakOnNonKeyFrames,
            [FromQuery] int? audioSampleRate,
            [FromQuery] int? maxAudioBitDepth,
            [FromQuery] int? audioBitRate,
            [FromQuery] int? audioChannels,
            [FromQuery] int? maxAudioChannels,
            [FromQuery] string? profile,
            [FromQuery] string? level,
            [FromQuery] float? framerate,
            [FromQuery] float? maxFramerate,
            [FromQuery] bool? copyTimestamps,
            [FromQuery] long? startTimeTicks,
            [FromQuery] int? width,
            [FromQuery] int? height,
            [FromQuery] int? maxWidth,
            [FromQuery] int? maxHeight,
            [FromQuery] int? videoBitRate,
            [FromQuery] int? subtitleStreamIndex,
            [FromQuery] SubtitleDeliveryMethod? subtitleMethod,
            [FromQuery] int? maxRefFrames,
            [FromQuery] int? maxVideoBitDepth,
            [FromQuery] bool? requireAvc,
            [FromQuery] bool? deInterlace,
            [FromQuery] bool? requireNonAnamorphic,
            [FromQuery] int? transcodingMaxAudioChannels,
            [FromQuery] int? cpuCoreLimit,
            [FromQuery] string? liveStreamId,
            [FromQuery] bool? enableMpegtsM2TsMode,
            [FromQuery] string? videoCodec,
            [FromQuery] string? subtitleCodec,
            [FromQuery] string? transcodeReasons,
            [FromQuery] int? audioStreamIndex,
            [FromQuery] int? videoStreamIndex,
            [FromQuery] EncodingContext? context,
            [FromQuery] Dictionary<string, string> streamOptions)
        {
            var streamingRequest = new VideoRequestDto
            {
                Id = itemId,
                CurrentRuntimeTicks = runtimeTicks,
                ActualSegmentLengthTicks = actualSegmentLengthTicks,
                Container = container,
                Static = @static ?? false,
                Params = @params,
                Tag = tag,
                DeviceProfileId = deviceProfileId,
                PlaySessionId = playSessionId,
                SegmentContainer = segmentContainer,
                SegmentLength = segmentLength,
                MinSegments = minSegments,
                MediaSourceId = mediaSourceId,
                DeviceId = deviceId,
                AudioCodec = audioCodec,
                EnableAutoStreamCopy = enableAutoStreamCopy ?? true,
                AllowAudioStreamCopy = allowAudioStreamCopy ?? true,
                AllowVideoStreamCopy = allowVideoStreamCopy ?? true,
                BreakOnNonKeyFrames = breakOnNonKeyFrames ?? false,
                AudioSampleRate = audioSampleRate,
                MaxAudioChannels = maxAudioChannels,
                AudioBitRate = audioBitRate,
                MaxAudioBitDepth = maxAudioBitDepth,
                AudioChannels = audioChannels,
                Profile = profile,
                Level = level,
                Framerate = framerate,
                MaxFramerate = maxFramerate,
                CopyTimestamps = copyTimestamps ?? false,
                StartTimeTicks = startTimeTicks,
                Width = width,
                Height = height,
                MaxWidth = maxWidth,
                MaxHeight = maxHeight,
                VideoBitRate = videoBitRate,
                SubtitleStreamIndex = subtitleStreamIndex,
                SubtitleMethod = subtitleMethod ?? SubtitleDeliveryMethod.Encode,
                MaxRefFrames = maxRefFrames,
                MaxVideoBitDepth = maxVideoBitDepth,
                RequireAvc = requireAvc ?? false,
                DeInterlace = deInterlace ?? false,
                RequireNonAnamorphic = requireNonAnamorphic ?? false,
                TranscodingMaxAudioChannels = transcodingMaxAudioChannels,
                CpuCoreLimit = cpuCoreLimit,
                LiveStreamId = liveStreamId,
                EnableMpegtsM2TsMode = enableMpegtsM2TsMode ?? false,
                VideoCodec = videoCodec,
                SubtitleCodec = subtitleCodec,
                TranscodeReasons = transcodeReasons,
                AudioStreamIndex = audioStreamIndex,
                VideoStreamIndex = videoStreamIndex,
                Context = context ?? EncodingContext.Streaming,
                StreamOptions = streamOptions
            };

            return await GetDynamicSegment(streamingRequest, segmentId)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Gets a video stream using HTTP live streaming.
        /// </summary>
        /// <param name="itemId">The item id.</param>
        /// <param name="playlistId">The playlist id.</param>
        /// <param name="segmentId">The segment id.</param>
        /// <param name="container">The video container. Possible values are: ts, webm, asf, wmv, ogv, mp4, m4v, mkv, mpeg, mpg, avi, 3gp, wmv, wtv, m2ts, mov, iso, flv. </param>
        /// <param name="runtimeTicks">The position of the requested segment in ticks.</param>
        /// <param name="actualSegmentLengthTicks">The length of the requested segment in ticks.</param>
        /// <param name="static">Optional. If true, the original file will be streamed statically without any encoding. Use either no url extension or the original file extension. true/false.</param>
        /// <param name="params">The streaming parameters.</param>
        /// <param name="tag">The tag.</param>
        /// <param name="deviceProfileId">Optional. The dlna device profile id to utilize.</param>
        /// <param name="playSessionId">The play session id.</param>
        /// <param name="segmentContainer">The segment container.</param>
        /// <param name="segmentLength">The segment length.</param>
        /// <param name="minSegments">The minimum number of segments.</param>
        /// <param name="mediaSourceId">The media version id, if playing an alternate version.</param>
        /// <param name="deviceId">The device id of the client requesting. Used to stop encoding processes when needed.</param>
        /// <param name="audioCodec">Optional. Specify a audio codec to encode to, e.g. mp3. If omitted the server will auto-select using the url's extension. Options: aac, mp3, vorbis, wma.</param>
        /// <param name="enableAutoStreamCopy">Whether or not to allow automatic stream copy if requested values match the original source. Defaults to true.</param>
        /// <param name="allowVideoStreamCopy">Whether or not to allow copying of the video stream url.</param>
        /// <param name="allowAudioStreamCopy">Whether or not to allow copying of the audio stream url.</param>
        /// <param name="breakOnNonKeyFrames">Optional. Whether to break on non key frames.</param>
        /// <param name="audioSampleRate">Optional. Specify a specific audio sample rate, e.g. 44100.</param>
        /// <param name="maxAudioBitDepth">Optional. The maximum audio bit depth.</param>
        /// <param name="maxStreamingBitrate">Optional. The maximum streaming bitrate.</param>
        /// <param name="audioBitRate">Optional. Specify an audio bitrate to encode to, e.g. 128000. If omitted this will be left to encoder defaults.</param>
        /// <param name="audioChannels">Optional. Specify a specific number of audio channels to encode to, e.g. 2.</param>
        /// <param name="maxAudioChannels">Optional. Specify a maximum number of audio channels to encode to, e.g. 2.</param>
        /// <param name="profile">Optional. Specify a specific an encoder profile (varies by encoder), e.g. main, baseline, high.</param>
        /// <param name="level">Optional. Specify a level for the encoder profile (varies by encoder), e.g. 3, 3.1.</param>
        /// <param name="framerate">Optional. A specific video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
        /// <param name="maxFramerate">Optional. A specific maximum video framerate to encode to, e.g. 23.976. Generally this should be omitted unless the device has specific requirements.</param>
        /// <param name="copyTimestamps">Whether or not to copy timestamps when transcoding with an offset. Defaults to false.</param>
        /// <param name="startTimeTicks">Optional. Specify a starting offset, in ticks. 1 tick = 10000 ms.</param>
        /// <param name="width">Optional. The fixed horizontal resolution of the encoded video.</param>
        /// <param name="height">Optional. The fixed vertical resolution of the encoded video.</param>
        /// <param name="videoBitRate">Optional. Specify a video bitrate to encode to, e.g. 500000. If omitted this will be left to encoder defaults.</param>
        /// <param name="subtitleStreamIndex">Optional. The index of the subtitle stream to use. If omitted no subtitles will be used.</param>
        /// <param name="subtitleMethod">Optional. Specify the subtitle delivery method.</param>
        /// <param name="maxRefFrames">Optional.</param>
        /// <param name="maxVideoBitDepth">Optional. The maximum video bit depth.</param>
        /// <param name="requireAvc">Optional. Whether to require avc.</param>
        /// <param name="deInterlace">Optional. Whether to deinterlace the video.</param>
        /// <param name="requireNonAnamorphic">Optional. Whether to require a non anamorphic stream.</param>
        /// <param name="transcodingMaxAudioChannels">Optional. The maximum number of audio channels to transcode.</param>
        /// <param name="cpuCoreLimit">Optional. The limit of how many cpu cores to use.</param>
        /// <param name="liveStreamId">The live stream id.</param>
        /// <param name="enableMpegtsM2TsMode">Optional. Whether to enable the MpegtsM2Ts mode.</param>
        /// <param name="videoCodec">Optional. Specify a video codec to encode to, e.g. h264. If omitted the server will auto-select using the url's extension. Options: h265, h264, mpeg4, theora, vpx, wmv.</param>
        /// <param name="subtitleCodec">Optional. Specify a subtitle codec to encode to.</param>
        /// <param name="transcodeReasons">Optional. The transcoding reason.</param>
        /// <param name="audioStreamIndex">Optional. The index of the audio stream to use. If omitted the first audio stream will be used.</param>
        /// <param name="videoStreamIndex">Optional. The index of the video stream to use. If omitted the first video stream will be used.</param>
        /// <param name="context">Optional. The <see cref="EncodingContext"/>.</param>
        /// <param name="streamOptions">Optional. The streaming options.</param>
        /// <response code="200">Video stream returned.</response>
        /// <returns>A <see cref="FileResult"/> containing the audio file.</returns>
        [HttpGet("Audio/{itemId}/hls1/{playlistId}/{segmentId}.{container}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesAudioFile]
        [SuppressMessage("Microsoft.Performance", "CA1801:ReviewUnusedParameters", MessageId = "playlistId", Justification = "Imported from ServiceStack")]
        public async Task<ActionResult> GetHlsAudioSegment(
            [FromRoute, Required] Guid itemId,
            [FromRoute, Required] string playlistId,
            [FromRoute, Required] int segmentId,
            [FromRoute, Required] string container,
            [FromQuery, Required] long runtimeTicks,
            [FromQuery, Required] long actualSegmentLengthTicks,
            [FromQuery] bool? @static,
            [FromQuery] string? @params,
            [FromQuery] string? tag,
            [FromQuery] string? deviceProfileId,
            [FromQuery] string? playSessionId,
            [FromQuery] string? segmentContainer,
            [FromQuery] int? segmentLength,
            [FromQuery] int? minSegments,
            [FromQuery] string? mediaSourceId,
            [FromQuery] string? deviceId,
            [FromQuery] string? audioCodec,
            [FromQuery] bool? enableAutoStreamCopy,
            [FromQuery] bool? allowVideoStreamCopy,
            [FromQuery] bool? allowAudioStreamCopy,
            [FromQuery] bool? breakOnNonKeyFrames,
            [FromQuery] int? audioSampleRate,
            [FromQuery] int? maxAudioBitDepth,
            [FromQuery] int? maxStreamingBitrate,
            [FromQuery] int? audioBitRate,
            [FromQuery] int? audioChannels,
            [FromQuery] int? maxAudioChannels,
            [FromQuery] string? profile,
            [FromQuery] string? level,
            [FromQuery] float? framerate,
            [FromQuery] float? maxFramerate,
            [FromQuery] bool? copyTimestamps,
            [FromQuery] long? startTimeTicks,
            [FromQuery] int? width,
            [FromQuery] int? height,
            [FromQuery] int? videoBitRate,
            [FromQuery] int? subtitleStreamIndex,
            [FromQuery] SubtitleDeliveryMethod? subtitleMethod,
            [FromQuery] int? maxRefFrames,
            [FromQuery] int? maxVideoBitDepth,
            [FromQuery] bool? requireAvc,
            [FromQuery] bool? deInterlace,
            [FromQuery] bool? requireNonAnamorphic,
            [FromQuery] int? transcodingMaxAudioChannels,
            [FromQuery] int? cpuCoreLimit,
            [FromQuery] string? liveStreamId,
            [FromQuery] bool? enableMpegtsM2TsMode,
            [FromQuery] string? videoCodec,
            [FromQuery] string? subtitleCodec,
            [FromQuery] string? transcodeReasons,
            [FromQuery] int? audioStreamIndex,
            [FromQuery] int? videoStreamIndex,
            [FromQuery] EncodingContext? context,
            [FromQuery] Dictionary<string, string> streamOptions)
        {
            var streamingRequest = new StreamingRequestDto
            {
                Id = itemId,
                Container = container,
                CurrentRuntimeTicks = runtimeTicks,
                ActualSegmentLengthTicks = actualSegmentLengthTicks,
                Static = @static ?? false,
                Params = @params,
                Tag = tag,
                DeviceProfileId = deviceProfileId,
                PlaySessionId = playSessionId,
                SegmentContainer = segmentContainer,
                SegmentLength = segmentLength,
                MinSegments = minSegments,
                MediaSourceId = mediaSourceId,
                DeviceId = deviceId,
                AudioCodec = audioCodec,
                EnableAutoStreamCopy = enableAutoStreamCopy ?? true,
                AllowAudioStreamCopy = allowAudioStreamCopy ?? true,
                AllowVideoStreamCopy = allowVideoStreamCopy ?? true,
                BreakOnNonKeyFrames = breakOnNonKeyFrames ?? false,
                AudioSampleRate = audioSampleRate,
                MaxAudioChannels = maxAudioChannels,
                AudioBitRate = audioBitRate ?? maxStreamingBitrate,
                MaxAudioBitDepth = maxAudioBitDepth,
                AudioChannels = audioChannels,
                Profile = profile,
                Level = level,
                Framerate = framerate,
                MaxFramerate = maxFramerate,
                CopyTimestamps = copyTimestamps ?? false,
                StartTimeTicks = startTimeTicks,
                Width = width,
                Height = height,
                VideoBitRate = videoBitRate,
                SubtitleStreamIndex = subtitleStreamIndex,
                SubtitleMethod = subtitleMethod ?? SubtitleDeliveryMethod.Encode,
                MaxRefFrames = maxRefFrames,
                MaxVideoBitDepth = maxVideoBitDepth,
                RequireAvc = requireAvc ?? false,
                DeInterlace = deInterlace ?? false,
                RequireNonAnamorphic = requireNonAnamorphic ?? false,
                TranscodingMaxAudioChannels = transcodingMaxAudioChannels,
                CpuCoreLimit = cpuCoreLimit,
                LiveStreamId = liveStreamId,
                EnableMpegtsM2TsMode = enableMpegtsM2TsMode ?? false,
                VideoCodec = videoCodec,
                SubtitleCodec = subtitleCodec,
                TranscodeReasons = transcodeReasons,
                AudioStreamIndex = audioStreamIndex,
                VideoStreamIndex = videoStreamIndex,
                Context = context ?? EncodingContext.Streaming,
                StreamOptions = streamOptions
            };

            return await GetDynamicSegment(streamingRequest, segmentId)
                .ConfigureAwait(false);
        }

        private async Task<ActionResult> GetVariantPlaylistInternal(StreamingRequestDto streamingRequest, CancellationTokenSource cancellationTokenSource)
        {
            using var state = await StreamingHelpers.GetStreamingState(
                    streamingRequest,
                    Request,
                    _authContext,
                    _mediaSourceManager,
                    _userManager,
                    _libraryManager,
                    _serverConfigurationManager,
                    _mediaEncoder,
                    _encodingHelper,
                    _dlnaManager,
                    _deviceManager,
                    _transcodingJobHelper,
                    TranscodingJobType,
                    cancellationTokenSource.Token)
                .ConfigureAwait(false);

            var request = new CreateMainPlaylistRequest(
                state.MediaPath,
                state.SegmentLength * 1000,
                state.RunTimeTicks ?? 0,
                state.Request.SegmentContainer ?? string.Empty,
                "hls1/main/",
                Request.QueryString.ToString(),
                EncodingHelper.IsCopyCodec(state.OutputVideoCodec));
            var playlist = _dynamicHlsPlaylistGenerator.CreateMainPlaylist(request);

            return new FileContentResult(Encoding.UTF8.GetBytes(playlist), MimeTypes.GetMimeType("playlist.m3u8"));
        }

        private async Task<ActionResult> GetDynamicSegment(StreamingRequestDto streamingRequest, int segmentId)
        {
            if ((streamingRequest.StartTimeTicks ?? 0) > 0)
            {
                throw new ArgumentException("StartTimeTicks is not allowed.");
            }

            // CTS lifecycle is managed internally.
            var cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;

            var state = await StreamingHelpers.GetStreamingState(
                    streamingRequest,
                    Request,
                    _authContext,
                    _mediaSourceManager,
                    _userManager,
                    _libraryManager,
                    _serverConfigurationManager,
                    _mediaEncoder,
                    _encodingHelper,
                    _dlnaManager,
                    _deviceManager,
                    _transcodingJobHelper,
                    TranscodingJobType,
                    cancellationToken)
                .ConfigureAwait(false);

            var playlistPath = Path.ChangeExtension(state.OutputFilePath, ".m3u8");

            var segmentPath = GetSegmentPath(state, playlistPath, segmentId);

            var segmentExtension = EncodingHelper.GetSegmentFileExtension(state.Request.SegmentContainer);

            TranscodingJobDto? job;

            if (System.IO.File.Exists(segmentPath))
            {
                job = _transcodingJobHelper.OnTranscodeBeginRequest(playlistPath, TranscodingJobType);
                _logger.LogDebug("returning {0} [it exists, try 1]", segmentPath);
                return await GetSegmentResult(state, playlistPath, segmentPath, segmentExtension, segmentId, job, cancellationToken).ConfigureAwait(false);
            }

            var transcodingLock = _transcodingJobHelper.GetTranscodingLock(playlistPath);
            await transcodingLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            var released = false;
            var startTranscoding = false;

            try
            {
                if (System.IO.File.Exists(segmentPath))
                {
                    job = _transcodingJobHelper.OnTranscodeBeginRequest(playlistPath, TranscodingJobType);
                    transcodingLock.Release();
                    released = true;
                    _logger.LogDebug("returning {0} [it exists, try 2]", segmentPath);
                    return await GetSegmentResult(state, playlistPath, segmentPath, segmentExtension, segmentId, job, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var currentTranscodingIndex = GetCurrentTranscodingIndex(playlistPath, segmentExtension);
                    var segmentGapRequiringTranscodingChange = 24 / state.SegmentLength;

                    if (segmentId == -1)
                    {
                        _logger.LogDebug("Starting transcoding because fmp4 init file is being requested");
                        startTranscoding = true;
                        segmentId = 0;
                    }
                    else if (currentTranscodingIndex == null)
                    {
                        _logger.LogDebug("Starting transcoding because currentTranscodingIndex=null");
                        startTranscoding = true;
                    }
                    else if (segmentId < currentTranscodingIndex.Value)
                    {
                        _logger.LogDebug("Starting transcoding because requestedIndex={0} and currentTranscodingIndex={1}", segmentId, currentTranscodingIndex);
                        startTranscoding = true;
                    }
                    else if (segmentId - currentTranscodingIndex.Value > segmentGapRequiringTranscodingChange)
                    {
                        _logger.LogDebug("Starting transcoding because segmentGap is {0} and max allowed gap is {1}. requestedIndex={2}", segmentId - currentTranscodingIndex.Value, segmentGapRequiringTranscodingChange, segmentId);
                        startTranscoding = true;
                    }

                    if (startTranscoding)
                    {
                        // If the playlist doesn't already exist, startup ffmpeg
                        try
                        {
                            await _transcodingJobHelper.KillTranscodingJobs(streamingRequest.DeviceId, streamingRequest.PlaySessionId, p => false)
                                .ConfigureAwait(false);

                            if (currentTranscodingIndex.HasValue)
                            {
                                DeleteLastFile(playlistPath, segmentExtension, 0);
                            }

                            streamingRequest.StartTimeTicks = streamingRequest.CurrentRuntimeTicks;

                            state.WaitForPath = segmentPath;
                            job = await _transcodingJobHelper.StartFfMpeg(
                                state,
                                playlistPath,
                                GetCommandLineArguments(playlistPath, state, false, segmentId),
                                Request,
                                TranscodingJobType,
                                cancellationTokenSource).ConfigureAwait(false);
                        }
                        catch
                        {
                            state.Dispose();
                            throw;
                        }

                        // await WaitForMinimumSegmentCount(playlistPath, 1, cancellationTokenSource.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        job = _transcodingJobHelper.OnTranscodeBeginRequest(playlistPath, TranscodingJobType);
                        if (job?.TranscodingThrottler != null)
                        {
                            await job.TranscodingThrottler.UnpauseTranscoding().ConfigureAwait(false);
                        }
                    }
                }
            }
            finally
            {
                if (!released)
                {
                    transcodingLock.Release();
                }
            }

            _logger.LogDebug("returning {0} [general case]", segmentPath);
            job ??= _transcodingJobHelper.OnTranscodeBeginRequest(playlistPath, TranscodingJobType);
            return await GetSegmentResult(state, playlistPath, segmentPath, segmentExtension, segmentId, job, cancellationToken).ConfigureAwait(false);
        }

        private static double[] GetSegmentLengths(StreamState state)
            => GetSegmentLengthsInternal(state.RunTimeTicks ?? 0, state.SegmentLength);

        internal static double[] GetSegmentLengthsInternal(long runtimeTicks, int segmentlength)
        {
            var segmentLengthTicks = TimeSpan.FromSeconds(segmentlength).Ticks;
            var wholeSegments = runtimeTicks / segmentLengthTicks;
            var remainingTicks = runtimeTicks % segmentLengthTicks;

            var segmentsLen = wholeSegments + (remainingTicks == 0 ? 0 : 1);
            var segments = new double[segmentsLen];
            for (int i = 0; i < wholeSegments; i++)
            {
                segments[i] = segmentlength;
            }

            if (remainingTicks != 0)
            {
                segments[^1] = TimeSpan.FromTicks(remainingTicks).TotalSeconds;
            }

            return segments;
        }

        private string GetCommandLineArguments(string outputPath, StreamState state, bool isEventPlaylist, int startNumber)
        {
            var videoCodec = _encodingHelper.GetVideoEncoder(state, _encodingOptions);
            var threads = EncodingHelper.GetNumberOfThreads(state, _encodingOptions, videoCodec);

            if (state.BaseRequest.BreakOnNonKeyFrames)
            {
                // FIXME: this is actually a workaround, as ideally it really should be the client which decides whether non-keyframe
                //        breakpoints are supported; but current implementation always uses "ffmpeg input seeking" which is liable
                //        to produce a missing part of video stream before first keyframe is encountered, which may lead to
                //        awkward cases like a few starting HLS segments having no video whatsoever, which breaks hls.js
                _logger.LogInformation("Current HLS implementation doesn't support non-keyframe breaks but one is requested, ignoring that request");
                state.BaseRequest.BreakOnNonKeyFrames = false;
            }

            var mapArgs = state.IsOutputVideo ? _encodingHelper.GetMapArgs(state) : string.Empty;

            var directory = Path.GetDirectoryName(outputPath) ?? throw new ArgumentException($"Provided path ({outputPath}) is not valid.", nameof(outputPath));
            var outputFileNameWithoutExtension = Path.GetFileNameWithoutExtension(outputPath);
            var outputPrefix = Path.Combine(directory, outputFileNameWithoutExtension);
            var outputExtension = EncodingHelper.GetSegmentFileExtension(state.Request.SegmentContainer);
            var outputTsArg = outputPrefix + "%d" + outputExtension;

            var segmentFormat = string.Empty;
            var segmentContainer = outputExtension.TrimStart('.');
            var inputModifier = _encodingHelper.GetInputModifier(state, _encodingOptions, segmentContainer);

            if (string.Equals(segmentContainer, "ts", StringComparison.OrdinalIgnoreCase))
            {
                segmentFormat = "mpegts";
            }
            else if (string.Equals(segmentContainer, "mp4", StringComparison.OrdinalIgnoreCase))
            {
                var outputFmp4HeaderArg = OperatingSystem.IsWindows() switch
                {
                    // on Windows, the path of fmp4 header file needs to be configured
                    true => " -hls_fmp4_init_filename \"" + outputPrefix + "-1" + outputExtension + "\"",
                    // on Linux/Unix, ffmpeg generate fmp4 header file to m3u8 output folder
                    false => " -hls_fmp4_init_filename \"" + outputFileNameWithoutExtension + "-1" + outputExtension + "\""
                };

                segmentFormat = "fmp4" + outputFmp4HeaderArg;
            }
            else
            {
                _logger.LogError("Invalid HLS segment container: {SegmentContainer}, default to mpegts", segmentContainer);
                segmentFormat = "mpegts";
            }

            var maxMuxingQueueSize = _encodingOptions.MaxMuxingQueueSize > 128
                ? _encodingOptions.MaxMuxingQueueSize.ToString(CultureInfo.InvariantCulture)
                : "128";

            var baseUrlParam = string.Empty;
            if (isEventPlaylist)
            {
                baseUrlParam = string.Format(
                    CultureInfo.InvariantCulture,
                    " -hls_base_url \"hls/{0}/\"",
                    Path.GetFileNameWithoutExtension(outputPath));
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} -map_metadata -1 -map_chapters -1 -threads {2} {3} {4} {5} -copyts -avoid_negative_ts disabled -max_muxing_queue_size {6} -f hls -max_delay 5000000 -hls_time {7} -hls_segment_type {8} -start_number {9}{10} -hls_segment_filename \"{12}\" -hls_playlist_type {11} -hls_list_size 0 -y \"{13}\"",
                inputModifier,
                _encodingHelper.GetInputArgument(state, _encodingOptions, segmentContainer),
                threads,
                mapArgs,
                GetVideoArguments(state, startNumber, isEventPlaylist),
                GetAudioArguments(state),
                maxMuxingQueueSize,
                state.SegmentLength.ToString(CultureInfo.InvariantCulture),
                segmentFormat,
                startNumber.ToString(CultureInfo.InvariantCulture),
                baseUrlParam,
                isEventPlaylist ? "event" : "vod",
                outputTsArg,
                outputPath).Trim();
        }

        /// <summary>
        /// Gets the audio arguments for transcoding.
        /// </summary>
        /// <param name="state">The <see cref="StreamState"/>.</param>
        /// <returns>The command line arguments for audio transcoding.</returns>
        private string GetAudioArguments(StreamState state)
        {
            if (state.AudioStream == null)
            {
                return string.Empty;
            }

            var audioCodec = _encodingHelper.GetAudioEncoder(state);

            if (!state.IsOutputVideo)
            {
                if (EncodingHelper.IsCopyCodec(audioCodec))
                {
                    var bitStreamArgs = EncodingHelper.GetAudioBitStreamArguments(state, state.Request.SegmentContainer, state.MediaSource.Container);

                    return "-acodec copy -strict -2" + bitStreamArgs;
                }

                var audioTranscodeParams = string.Empty;

                audioTranscodeParams += "-acodec " + audioCodec;

                if (state.OutputAudioBitrate.HasValue)
                {
                    audioTranscodeParams += " -ab " + state.OutputAudioBitrate.Value.ToString(CultureInfo.InvariantCulture);
                }

                if (state.OutputAudioChannels.HasValue)
                {
                    audioTranscodeParams += " -ac " + state.OutputAudioChannels.Value.ToString(CultureInfo.InvariantCulture);
                }

                if (state.OutputAudioSampleRate.HasValue)
                {
                    audioTranscodeParams += " -ar " + state.OutputAudioSampleRate.Value.ToString(CultureInfo.InvariantCulture);
                }

                audioTranscodeParams += " -vn";
                return audioTranscodeParams;
            }

            // flac and opus are experimental in mp4 muxer
            var strictArgs = string.Empty;

            if (string.Equals(state.ActualOutputAudioCodec, "flac", StringComparison.OrdinalIgnoreCase)
                || string.Equals(state.ActualOutputAudioCodec, "opus", StringComparison.OrdinalIgnoreCase))
            {
                strictArgs = " -strict -2";
            }

            if (EncodingHelper.IsCopyCodec(audioCodec))
            {
                var videoCodec = _encodingHelper.GetVideoEncoder(state, _encodingOptions);
                var bitStreamArgs = EncodingHelper.GetAudioBitStreamArguments(state, state.Request.SegmentContainer, state.MediaSource.Container);
                var copyArgs = "-codec:a:0 copy" + bitStreamArgs + strictArgs;

                if (EncodingHelper.IsCopyCodec(videoCodec) && state.EnableBreakOnNonKeyFrames(videoCodec))
                {
                    return copyArgs + " -copypriorss:a:0 0";
                }

                return copyArgs;
            }

            var args = "-codec:a:0 " + audioCodec + strictArgs;

            var channels = state.OutputAudioChannels;

            if (channels.HasValue)
            {
                args += " -ac " + channels.Value;
            }

            var bitrate = state.OutputAudioBitrate;

            if (bitrate.HasValue)
            {
                args += " -ab " + bitrate.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (state.OutputAudioSampleRate.HasValue)
            {
                args += " -ar " + state.OutputAudioSampleRate.Value.ToString(CultureInfo.InvariantCulture);
            }

            args += _encodingHelper.GetAudioFilterParam(state, _encodingOptions);

            return args;
        }

        /// <summary>
        /// Gets the video arguments for transcoding.
        /// </summary>
        /// <param name="state">The <see cref="StreamState"/>.</param>
        /// <param name="startNumber">The first number in the hls sequence.</param>
        /// <param name="isEventPlaylist">Whether the playlist is EVENT or VOD.</param>
        /// <returns>The command line arguments for video transcoding.</returns>
        private string GetVideoArguments(StreamState state, int startNumber, bool isEventPlaylist)
        {
            if (state.VideoStream == null)
            {
                return string.Empty;
            }

            if (!state.IsOutputVideo)
            {
                return string.Empty;
            }

            var codec = _encodingHelper.GetVideoEncoder(state, _encodingOptions);

            var args = "-codec:v:0 " + codec;

            if (string.Equals(state.ActualOutputVideoCodec, "h265", StringComparison.OrdinalIgnoreCase)
                || string.Equals(state.ActualOutputVideoCodec, "hevc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(codec, "h265", StringComparison.OrdinalIgnoreCase)
                || string.Equals(codec, "hevc", StringComparison.OrdinalIgnoreCase))
            {
                if (EncodingHelper.IsCopyCodec(codec)
                    && (string.Equals(state.VideoStream.VideoRangeType, "DOVI", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(state.VideoStream.CodecTag, "dovi", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(state.VideoStream.CodecTag, "dvh1", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(state.VideoStream.CodecTag, "dvhe", StringComparison.OrdinalIgnoreCase)))
                {
                    // Prefer dvh1 to dvhe
                    args += " -tag:v:0 dvh1 -strict -2";
                }
                else
                {
                    // Prefer hvc1 to hev1
                    args += " -tag:v:0 hvc1";
                }
            }

            // if  (state.EnableMpegtsM2TsMode)
            // {
            //     args += " -mpegts_m2ts_mode 1";
            // }

            // See if we can save come cpu cycles by avoiding encoding.
            if (EncodingHelper.IsCopyCodec(codec))
            {
                // If h264_mp4toannexb is ever added, do not use it for live tv.
                if (state.VideoStream != null && !string.Equals(state.VideoStream.NalLengthSize, "0", StringComparison.OrdinalIgnoreCase))
                {
                    string bitStreamArgs = EncodingHelper.GetBitStreamArgs(state.VideoStream);
                    if (!string.IsNullOrEmpty(bitStreamArgs))
                    {
                        args += " " + bitStreamArgs;
                    }
                }

                args += " -start_at_zero";
            }
            else
            {
                args += _encodingHelper.GetVideoQualityParam(state, codec, _encodingOptions, isEventPlaylist ? DefaultEventEncoderPreset : DefaultVodEncoderPreset);

                // Set the key frame params for video encoding to match the hls segment time.
                args += _encodingHelper.GetHlsVideoKeyFrameArguments(state, codec, state.SegmentLength, isEventPlaylist, startNumber);

                // Currenly b-frames in libx265 breaks the FMP4-HLS playback on iOS, disable it for now.
                if (string.Equals(codec, "libx265", StringComparison.OrdinalIgnoreCase))
                {
                    args += " -bf 0";
                }

                // args += " -mixed-refs 0 -refs 3 -x264opts b_pyramid=0:weightb=0:weightp=0";

                // video processing filters.
                args += _encodingHelper.GetVideoProcessingFilterParam(state, _encodingOptions, codec);

                // -start_at_zero is necessary to use with -ss when seeking,
                // otherwise the target position cannot be determined.
                if (state.SubtitleStream != null)
                {
                    // Disable start_at_zero for external graphical subs
                    if (!(state.SubtitleStream.IsExternal && !state.SubtitleStream.IsTextSubtitleStream))
                    {
                        args += " -start_at_zero";
                    }
                }
            }

            // TODO why was this not enabled for VOD?
            if (isEventPlaylist)
            {
                args += " -flags -global_header";
            }

            if (!string.IsNullOrEmpty(state.OutputVideoSync))
            {
                args += " -vsync " + state.OutputVideoSync;
            }

            args += _encodingHelper.GetOutputFFlags(state);

            return args;
        }

        private string GetSegmentPath(StreamState state, string playlist, int index)
        {
            var folder = Path.GetDirectoryName(playlist) ?? throw new ArgumentException($"Provided path ({playlist}) is not valid.", nameof(playlist));
            var filename = Path.GetFileNameWithoutExtension(playlist);

            return Path.Combine(folder, filename + index.ToString(CultureInfo.InvariantCulture) + EncodingHelper.GetSegmentFileExtension(state.Request.SegmentContainer));
        }

        private async Task<ActionResult> GetSegmentResult(
            StreamState state,
            string playlistPath,
            string segmentPath,
            string segmentExtension,
            int segmentIndex,
            TranscodingJobDto? transcodingJob,
            CancellationToken cancellationToken)
        {
            var segmentExists = System.IO.File.Exists(segmentPath);
            if (segmentExists)
            {
                if (transcodingJob != null && transcodingJob.HasExited)
                {
                    // Transcoding job is over, so assume all existing files are ready
                    _logger.LogDebug("serving up {0} as transcode is over", segmentPath);
                    return GetSegmentResult(state, segmentPath, transcodingJob);
                }

                var currentTranscodingIndex = GetCurrentTranscodingIndex(playlistPath, segmentExtension);

                // If requested segment is less than transcoding position, we can't transcode backwards, so assume it's ready
                if (segmentIndex < currentTranscodingIndex)
                {
                    _logger.LogDebug("serving up {0} as transcode index {1} is past requested point {2}", segmentPath, currentTranscodingIndex, segmentIndex);
                    return GetSegmentResult(state, segmentPath, transcodingJob);
                }
            }

            var nextSegmentPath = GetSegmentPath(state, playlistPath, segmentIndex + 1);
            if (transcodingJob != null)
            {
                while (!cancellationToken.IsCancellationRequested && !transcodingJob.HasExited)
                {
                    // To be considered ready, the segment file has to exist AND
                    // either the transcoding job should be done or next segment should also exist
                    if (segmentExists)
                    {
                        if (transcodingJob.HasExited || System.IO.File.Exists(nextSegmentPath))
                        {
                            _logger.LogDebug("Serving up {SegmentPath} as it deemed ready", segmentPath);
                            return GetSegmentResult(state, segmentPath, transcodingJob);
                        }
                    }
                    else
                    {
                        segmentExists = System.IO.File.Exists(segmentPath);
                        if (segmentExists)
                        {
                            continue; // avoid unnecessary waiting if segment just became available
                        }
                    }

                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }

                if (!System.IO.File.Exists(segmentPath))
                {
                    _logger.LogWarning("cannot serve {0} as transcoding quit before we got there", segmentPath);
                }
                else
                {
                    _logger.LogDebug("serving {0} as it's on disk and transcoding stopped", segmentPath);
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
            else
            {
                _logger.LogWarning("cannot serve {0} as it doesn't exist and no transcode is running", segmentPath);
            }

            return GetSegmentResult(state, segmentPath, transcodingJob);
        }

        private ActionResult GetSegmentResult(StreamState state, string segmentPath, TranscodingJobDto? transcodingJob)
        {
            var segmentEndingPositionTicks = state.Request.CurrentRuntimeTicks + state.Request.ActualSegmentLengthTicks;

            Response.OnCompleted(() =>
            {
                _logger.LogDebug("Finished serving {SegmentPath}", segmentPath);
                if (transcodingJob != null)
                {
                    transcodingJob.DownloadPositionTicks = Math.Max(transcodingJob.DownloadPositionTicks ?? segmentEndingPositionTicks, segmentEndingPositionTicks);
                    _transcodingJobHelper.OnTranscodeEndRequest(transcodingJob);
                }

                return Task.CompletedTask;
            });

            return FileStreamResponseHelpers.GetStaticFileResult(segmentPath, MimeTypes.GetMimeType(segmentPath));
        }

        private int? GetCurrentTranscodingIndex(string playlist, string segmentExtension)
        {
            var job = _transcodingJobHelper.GetTranscodingJob(playlist, TranscodingJobType);

            if (job == null || job.HasExited)
            {
                return null;
            }

            var file = GetLastTranscodingFile(playlist, segmentExtension, _fileSystem);

            if (file == null)
            {
                return null;
            }

            var playlistFilename = Path.GetFileNameWithoutExtension(playlist);

            var indexString = Path.GetFileNameWithoutExtension(file.Name).Substring(playlistFilename.Length);

            return int.Parse(indexString, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        private static FileSystemMetadata? GetLastTranscodingFile(string playlist, string segmentExtension, IFileSystem fileSystem)
        {
            var folder = Path.GetDirectoryName(playlist) ?? throw new ArgumentException("Path can't be a root directory.", nameof(playlist));

            var filePrefix = Path.GetFileNameWithoutExtension(playlist);

            try
            {
                return fileSystem.GetFiles(folder, new[] { segmentExtension }, true, false)
                    .Where(i => Path.GetFileNameWithoutExtension(i.Name).StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(fileSystem.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch (IOException)
            {
                return null;
            }
        }

        private void DeleteLastFile(string playlistPath, string segmentExtension, int retryCount)
        {
            var file = GetLastTranscodingFile(playlistPath, segmentExtension, _fileSystem);

            if (file != null)
            {
                DeleteFile(file.FullName, retryCount);
            }
        }

        private void DeleteFile(string path, int retryCount)
        {
            if (retryCount >= 5)
            {
                return;
            }

            _logger.LogDebug("Deleting partial HLS file {Path}", path);

            try
            {
                _fileSystem.DeleteFile(path);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Error deleting partial stream file(s) {Path}", path);

                var task = Task.Delay(100);
                task.Wait();
                DeleteFile(path, retryCount + 1);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting partial stream file(s) {Path}", path);
            }
        }
    }
}
