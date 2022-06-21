#nullable disable
#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Extensions;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.MediaEncoding.Probing;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.MediaEncoding.Encoder
{
    /// <summary>
    /// Class MediaEncoder.
    /// </summary>
    public class MediaEncoder : IMediaEncoder, IDisposable
    {
        /// <summary>
        /// The default SDR image extraction timeout in milliseconds.
        /// </summary>
        internal const int DefaultSdrImageExtractionTimeout = 10000;

        /// <summary>
        /// The default HDR image extraction timeout in milliseconds.
        /// </summary>
        internal const int DefaultHdrImageExtractionTimeout = 20000;

        private readonly ILogger<MediaEncoder> _logger;
        private readonly IServerConfigurationManager _configurationManager;
        private readonly IFileSystem _fileSystem;
        private readonly ILocalizationManager _localization;
        private readonly IConfiguration _config;
        private readonly string _startupOptionFFmpegPath;

        private readonly SemaphoreSlim _thumbnailResourcePool = new SemaphoreSlim(2, 2);

        private readonly object _runningProcessesLock = new object();
        private readonly List<ProcessWrapper> _runningProcesses = new List<ProcessWrapper>();

        // MediaEncoder is registered as a Singleton
        private readonly JsonSerializerOptions _jsonSerializerOptions;

        private List<string> _encoders = new List<string>();
        private List<string> _decoders = new List<string>();
        private List<string> _hwaccels = new List<string>();
        private List<string> _filters = new List<string>();
        private IDictionary<int, bool> _filtersWithOption = new Dictionary<int, bool>();

        private bool _isVaapiDeviceAmd = false;
        private bool _isVaapiDeviceInteliHD = false;
        private bool _isVaapiDeviceInteli965 = false;

        private Version _ffmpegVersion = null;
        private string _ffmpegPath = string.Empty;
        private string _ffprobePath;
        private int _threads;

        public MediaEncoder(
            ILogger<MediaEncoder> logger,
            IServerConfigurationManager configurationManager,
            IFileSystem fileSystem,
            ILocalizationManager localization,
            IConfiguration config)
        {
            _logger = logger;
            _configurationManager = configurationManager;
            _fileSystem = fileSystem;
            _localization = localization;
            _config = config;
            _startupOptionFFmpegPath = config.GetValue<string>(Controller.Extensions.ConfigurationExtensions.FfmpegPathKey) ?? string.Empty;
            _jsonSerializerOptions = JsonDefaults.Options;
        }

        /// <inheritdoc />
        public string EncoderPath => _ffmpegPath;

        /// <inheritdoc />
        public string ProbePath => _ffprobePath;

        public Version EncoderVersion => _ffmpegVersion;

        public bool IsVaapiDeviceAmd => _isVaapiDeviceAmd;

        public bool IsVaapiDeviceInteliHD => _isVaapiDeviceInteliHD;

        public bool IsVaapiDeviceInteli965 => _isVaapiDeviceInteli965;

        /// <summary>
        /// Run at startup or if the user removes a Custom path from transcode page.
        /// Sets global variables FFmpegPath.
        /// Precedence is: Config > CLI > $PATH.
        /// </summary>
        public void SetFFmpegPath()
        {
            // 1) Custom path stored in config/encoding xml file under tag <EncoderAppPath> takes precedence
            var ffmpegPath = _configurationManager.GetEncodingOptions().EncoderAppPath;
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                // 2) Check if the --ffmpeg CLI switch has been given
                ffmpegPath = _startupOptionFFmpegPath;
                if (string.IsNullOrEmpty(ffmpegPath))
                {
                    // 3) Check "ffmpeg"
                    ffmpegPath = "ffmpeg";
                }
            }

            if (!ValidatePath(ffmpegPath))
            {
                _ffmpegPath = null;
            }

            // Write the FFmpeg path to the config/encoding.xml file as <EncoderAppPathDisplay> so it appears in UI
            var options = _configurationManager.GetEncodingOptions();
            options.EncoderAppPathDisplay = _ffmpegPath ?? string.Empty;
            _configurationManager.SaveConfiguration("encoding", options);

            // Only if mpeg path is set, try and set path to probe
            if (_ffmpegPath != null)
            {
                // Determine a probe path from the mpeg path
                _ffprobePath = Regex.Replace(_ffmpegPath, @"[^\/\\]+?(\.[^\/\\\n.]+)?$", @"ffprobe$1");

                // Interrogate to understand what coders are supported
                var validator = new EncoderValidator(_logger, _ffmpegPath);

                SetAvailableDecoders(validator.GetDecoders());
                SetAvailableEncoders(validator.GetEncoders());
                SetAvailableFilters(validator.GetFilters());
                SetAvailableFiltersWithOption(validator.GetFiltersWithOption());
                SetAvailableHwaccels(validator.GetHwaccels());
                SetMediaEncoderVersion(validator);

                _threads = EncodingHelper.GetNumberOfThreads(null, options, null);

                // Check the Vaapi device vendor
                if (OperatingSystem.IsLinux()
                    && SupportsHwaccel("vaapi")
                    && !string.IsNullOrEmpty(options.VaapiDevice)
                    && string.Equals(options.HardwareAccelerationType, "vaapi", StringComparison.OrdinalIgnoreCase))
                {
                    _isVaapiDeviceAmd = validator.CheckVaapiDeviceByDriverName("Mesa Gallium driver", options.VaapiDevice);
                    _isVaapiDeviceInteliHD = validator.CheckVaapiDeviceByDriverName("Intel iHD driver", options.VaapiDevice);
                    _isVaapiDeviceInteli965 = validator.CheckVaapiDeviceByDriverName("Intel i965 driver", options.VaapiDevice);
                    if (_isVaapiDeviceAmd)
                    {
                        _logger.LogInformation("VAAPI device {RenderNodePath} is AMD GPU", options.VaapiDevice);
                    }
                    else if (_isVaapiDeviceInteliHD)
                    {
                        _logger.LogInformation("VAAPI device {RenderNodePath} is Intel GPU (iHD)", options.VaapiDevice);
                    }
                    else if (_isVaapiDeviceInteli965)
                    {
                        _logger.LogInformation("VAAPI device {RenderNodePath} is Intel GPU (i965)", options.VaapiDevice);
                    }
                }
            }

            _logger.LogInformation("FFmpeg: {FfmpegPath}", _ffmpegPath ?? string.Empty);
        }

        /// <summary>
        /// Triggered from the Settings > Transcoding UI page when users submits Custom FFmpeg path to use.
        /// Only write the new path to xml if it exists.  Do not perform validation checks on ffmpeg here.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="pathType">The path type.</param>
        public void UpdateEncoderPath(string path, string pathType)
        {
            var config = _configurationManager.GetEncodingOptions();

            // Filesystem may not be case insensitive, but EncoderAppPathDisplay should always point to a valid file?
            if (string.IsNullOrEmpty(config.EncoderAppPath)
                && string.Equals(config.EncoderAppPathDisplay, path, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Existing ffmpeg path is empty and the new path is the same as {EncoderAppPathDisplay}. Skipping", nameof(config.EncoderAppPathDisplay));
                return;
            }

            string newPath;

            _logger.LogInformation("Attempting to update encoder path to {Path}. pathType: {PathType}", path ?? string.Empty, pathType ?? string.Empty);

            if (!string.Equals(pathType, "custom", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Unexpected pathType value");
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                // User had cleared the custom path in UI
                newPath = string.Empty;
            }
            else
            {
                if (Directory.Exists(path))
                {
                    // Given path is directory, so resolve down to filename
                    newPath = GetEncoderPathFromDirectory(path, "ffmpeg");
                }
                else
                {
                    newPath = path;
                }

                if (!new EncoderValidator(_logger, newPath).ValidateVersion())
                {
                    throw new ResourceNotFoundException();
                }
            }

            // Write the new ffmpeg path to the xml as <EncoderAppPath>
            // This ensures its not lost on next startup
            config.EncoderAppPath = newPath;
            _configurationManager.SaveConfiguration("encoding", config);

            // Trigger SetFFmpegPath so we validate the new path and setup probe path
            SetFFmpegPath();
        }

        /// <summary>
        /// Validates the supplied FQPN to ensure it is a ffmpeg utility.
        /// If checks pass, global variable FFmpegPath is updated.
        /// </summary>
        /// <param name="path">FQPN to test.</param>
        /// <returns><c>true</c> if the version validation succeeded; otherwise, <c>false</c>.</returns>
        private bool ValidatePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            bool rc = new EncoderValidator(_logger, path).ValidateVersion();
            if (!rc)
            {
                _logger.LogWarning("FFmpeg: Failed version check: {Path}", path);
                return false;
            }

            _ffmpegPath = path;
            return true;
        }

        private string GetEncoderPathFromDirectory(string path, string filename, bool recursive = false)
        {
            try
            {
                var files = _fileSystem.GetFilePaths(path, recursive);

                var excludeExtensions = new[] { ".c" };

                return files.FirstOrDefault(i => string.Equals(Path.GetFileNameWithoutExtension(i), filename, StringComparison.OrdinalIgnoreCase)
                                                    && !excludeExtensions.Contains(Path.GetExtension(i) ?? string.Empty));
            }
            catch (Exception)
            {
                // Trap all exceptions, like DirNotExists, and return null
                return null;
            }
        }

        public void SetAvailableEncoders(IEnumerable<string> list)
        {
            _encoders = list.ToList();
        }

        public void SetAvailableDecoders(IEnumerable<string> list)
        {
            _decoders = list.ToList();
        }

        public void SetAvailableHwaccels(IEnumerable<string> list)
        {
            _hwaccels = list.ToList();
        }

        public void SetAvailableFilters(IEnumerable<string> list)
        {
            _filters = list.ToList();
        }

        public void SetAvailableFiltersWithOption(IDictionary<int, bool> dict)
        {
            _filtersWithOption = dict;
        }

        public void SetMediaEncoderVersion(EncoderValidator validator)
        {
            _ffmpegVersion = validator.GetFFmpegVersion();
        }

        public bool SupportsEncoder(string encoder)
        {
            return _encoders.Contains(encoder, StringComparer.OrdinalIgnoreCase);
        }

        public bool SupportsDecoder(string decoder)
        {
            return _decoders.Contains(decoder, StringComparer.OrdinalIgnoreCase);
        }

        public bool SupportsHwaccel(string hwaccel)
        {
            return _hwaccels.Contains(hwaccel, StringComparer.OrdinalIgnoreCase);
        }

        public bool SupportsFilter(string filter)
        {
            return _filters.Contains(filter, StringComparer.OrdinalIgnoreCase);
        }

        public bool SupportsFilterWithOption(FilterOptionType option)
        {
            if (_filtersWithOption.TryGetValue((int)option, out var val))
            {
                return val;
            }

            return false;
        }

        public bool CanEncodeToAudioCodec(string codec)
        {
            if (string.Equals(codec, "opus", StringComparison.OrdinalIgnoreCase))
            {
                codec = "libopus";
            }
            else if (string.Equals(codec, "mp3", StringComparison.OrdinalIgnoreCase))
            {
                codec = "libmp3lame";
            }

            return SupportsEncoder(codec);
        }

        public bool CanEncodeToSubtitleCodec(string codec)
        {
            // TODO
            return true;
        }

        /// <summary>
        /// Gets the media info.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        public Task<MediaInfo> GetMediaInfo(MediaInfoRequest request, CancellationToken cancellationToken)
        {
            var extractChapters = request.MediaType == DlnaProfileType.Video && request.ExtractChapters;
            var inputFile = request.MediaSource.Path;

            string analyzeDuration = string.Empty;
            string ffmpegAnalyzeDuration = _config.GetFFmpegAnalyzeDuration() ?? string.Empty;

            if (!string.IsNullOrEmpty(ffmpegAnalyzeDuration))
            {
                analyzeDuration = "-analyzeduration " + ffmpegAnalyzeDuration;
            }
            else if (request.MediaSource.AnalyzeDurationMs > 0)
            {
                analyzeDuration = "-analyzeduration " +
                                  (request.MediaSource.AnalyzeDurationMs * 1000).ToString();
            }

            var forceEnableLogging = request.MediaSource.Protocol != MediaProtocol.File;

            return GetMediaInfoInternal(
                GetInputArgument(inputFile, request.MediaSource),
                request.MediaSource.Path,
                request.MediaSource.Protocol,
                extractChapters,
                analyzeDuration,
                request.MediaType == DlnaProfileType.Audio,
                request.MediaSource.VideoType,
                forceEnableLogging,
                cancellationToken);
        }

        /// <summary>
        /// Gets the input argument.
        /// </summary>
        /// <param name="inputFile">The input file.</param>
        /// <param name="mediaSource">The mediaSource.</param>
        /// <returns>System.String.</returns>
        /// <exception cref="ArgumentException">Unrecognized InputType.</exception>
        public string GetInputArgument(string inputFile, MediaSourceInfo mediaSource)
        {
            var prefix = "file";
            if (mediaSource.VideoType == VideoType.BluRay
                || mediaSource.IsoType == IsoType.BluRay)
            {
                prefix = "bluray";
            }

            return EncodingUtils.GetInputArgument(prefix, inputFile, mediaSource.Protocol);
        }

        /// <summary>
        /// Gets the input argument for an external subtitle file.
        /// </summary>
        /// <param name="inputFile">The input file.</param>
        /// <returns>System.String.</returns>
        /// <exception cref="ArgumentException">Unrecognized InputType.</exception>
        public string GetExternalSubtitleInputArgument(string inputFile)
        {
            const string Prefix = "file";

            return EncodingUtils.GetInputArgument(Prefix, inputFile, MediaProtocol.File);
        }

        /// <summary>
        /// Gets the media info internal.
        /// </summary>
        /// <returns>Task{MediaInfoResult}.</returns>
        private async Task<MediaInfo> GetMediaInfoInternal(
            string inputPath,
            string primaryPath,
            MediaProtocol protocol,
            bool extractChapters,
            string probeSizeArgument,
            bool isAudio,
            VideoType? videoType,
            bool forceEnableLogging,
            CancellationToken cancellationToken)
        {
            var args = extractChapters
                ? "{0} -i {1} -threads {2} -v warning -print_format json -show_streams -show_chapters -show_format"
                : "{0} -i {1} -threads {2} -v warning -print_format json -show_streams -show_format";
            args = string.Format(CultureInfo.InvariantCulture, args, probeSizeArgument, inputPath, _threads).Trim();

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,

                    // Must consume both or ffmpeg may hang due to deadlocks. See comments below.
                    RedirectStandardOutput = true,

                    FileName = _ffprobePath,
                    Arguments = args,

                    WindowStyle = ProcessWindowStyle.Hidden,
                    ErrorDialog = false,
                },
                EnableRaisingEvents = true
            };

            if (forceEnableLogging)
            {
                _logger.LogInformation("{ProcessFileName} {ProcessArgs}", process.StartInfo.FileName, process.StartInfo.Arguments);
            }
            else
            {
                _logger.LogDebug("{ProcessFileName} {ProcessArgs}", process.StartInfo.FileName, process.StartInfo.Arguments);
            }

            using (var processWrapper = new ProcessWrapper(process, this))
            {
                await using var memoryStream = new MemoryStream();
                _logger.LogDebug("Starting ffprobe with args {Args}", args);
                StartProcess(processWrapper);
                await process.StandardOutput.BaseStream.CopyToAsync(memoryStream, cancellationToken: cancellationToken);
                memoryStream.Seek(0, SeekOrigin.Begin);
                InternalMediaInfoResult result;
                try
                {
                    result = await JsonSerializer.DeserializeAsync<InternalMediaInfoResult>(
                                        memoryStream,
                                        _jsonSerializerOptions,
                                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    StopProcess(processWrapper, 100);

                    throw;
                }

                if (result == null || (result.Streams == null && result.Format == null))
                {
                    throw new FfmpegException("ffprobe failed - streams and format are both null.");
                }

                if (result.Streams != null)
                {
                    // Normalize aspect ratio if invalid
                    foreach (var stream in result.Streams)
                    {
                        if (string.Equals(stream.DisplayAspectRatio, "0:1", StringComparison.OrdinalIgnoreCase))
                        {
                            stream.DisplayAspectRatio = string.Empty;
                        }

                        if (string.Equals(stream.SampleAspectRatio, "0:1", StringComparison.OrdinalIgnoreCase))
                        {
                            stream.SampleAspectRatio = string.Empty;
                        }
                    }
                }

                return new ProbeResultNormalizer(_logger, _localization).GetMediaInfo(result, videoType, isAudio, primaryPath, protocol);
            }
        }

        public Task<string> ExtractAudioImage(string path, int? imageStreamIndex, CancellationToken cancellationToken)
        {
            var mediaSource = new MediaSourceInfo
            {
                Protocol = MediaProtocol.File
            };

            return ExtractImage(path, null, null, imageStreamIndex, mediaSource, true, null, null, ImageFormat.Jpg, cancellationToken);
        }

        public Task<string> ExtractVideoImage(string inputFile, string container, MediaSourceInfo mediaSource, MediaStream videoStream, Video3DFormat? threedFormat, TimeSpan? offset, CancellationToken cancellationToken)
        {
            return ExtractImage(inputFile, container, videoStream, null, mediaSource, false, threedFormat, offset, ImageFormat.Jpg, cancellationToken);
        }

        public Task<string> ExtractVideoImage(string inputFile, string container, MediaSourceInfo mediaSource, MediaStream imageStream, int? imageStreamIndex, ImageFormat? targetFormat, CancellationToken cancellationToken)
        {
            return ExtractImage(inputFile, container, imageStream, imageStreamIndex, mediaSource, false, null, null, targetFormat, cancellationToken);
        }

        private async Task<string> ExtractImage(
            string inputFile,
            string container,
            MediaStream videoStream,
            int? imageStreamIndex,
            MediaSourceInfo mediaSource,
            bool isAudio,
            Video3DFormat? threedFormat,
            TimeSpan? offset,
            ImageFormat? targetFormat,
            CancellationToken cancellationToken)
        {
            var inputArgument = GetInputArgument(inputFile, mediaSource);

            if (!isAudio)
            {
                try
                {
                    return await ExtractImageInternal(inputArgument, container, videoStream, imageStreamIndex, threedFormat, offset, true, targetFormat, cancellationToken).ConfigureAwait(false);
                }
                catch (ArgumentException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "I-frame image extraction failed, will attempt standard way. Input: {Arguments}", inputArgument);
                }
            }

            return await ExtractImageInternal(inputArgument, container, videoStream, imageStreamIndex, threedFormat, offset, false, targetFormat, cancellationToken).ConfigureAwait(false);
        }

        private async Task<string> ExtractImageInternal(string inputPath, string container, MediaStream videoStream, int? imageStreamIndex, Video3DFormat? threedFormat, TimeSpan? offset, bool useIFrame, ImageFormat? targetFormat, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(inputPath))
            {
                throw new ArgumentNullException(nameof(inputPath));
            }

            var outputExtension = targetFormat switch
            {
                ImageFormat.Bmp => ".bmp",
                ImageFormat.Gif => ".gif",
                ImageFormat.Jpg => ".jpg",
                ImageFormat.Png => ".png",
                ImageFormat.Webp => ".webp",
                _ => ".jpg"
            };

            var tempExtractPath = Path.Combine(_configurationManager.ApplicationPaths.TempDirectory, Guid.NewGuid() + outputExtension);
            Directory.CreateDirectory(Path.GetDirectoryName(tempExtractPath));

            // deint -> scale -> thumbnail -> tonemap.
            // put the SW tonemap right after the thumbnail to do it only once to reduce cpu usage.
            var filters = new List<string>();

            // deinterlace using bwdif algorithm for video stream.
            if (videoStream != null && videoStream.IsInterlaced)
            {
                filters.Add("bwdif=0:-1:0");
            }

            // apply some filters to thumbnail extracted below (below) crop any black lines that we made and get the correct ar.
            // This filter chain may have adverse effects on recorded tv thumbnails if ar changes during presentation ex. commercials @ diff ar
            var scaler = threedFormat switch
            {
                // hsbs crop width in half,scale to correct size, set the display aspect,crop out any black bars we may have made. Work out the correct height based on the display aspect it will maintain the aspect where -1 in this case (3d) may not.
                Video3DFormat.HalfSideBySide => "crop=iw/2:ih:0:0,scale=(iw*2):ih,setdar=dar=a,crop=min(iw\\,ih*dar):min(ih\\,iw/dar):(iw-min(iw\\,iw*sar))/2:(ih - min (ih\\,ih/sar))/2,setsar=sar=1",
                // fsbs crop width in half,set the display aspect,crop out any black bars we may have made
                Video3DFormat.FullSideBySide => "crop=iw/2:ih:0:0,setdar=dar=a,crop=min(iw\\,ih*dar):min(ih\\,iw/dar):(iw-min(iw\\,iw*sar))/2:(ih - min (ih\\,ih/sar))/2,setsar=sar=1",
                // htab crop heigh in half,scale to correct size, set the display aspect,crop out any black bars we may have made
                Video3DFormat.HalfTopAndBottom => "crop=iw:ih/2:0:0,scale=(iw*2):ih),setdar=dar=a,crop=min(iw\\,ih*dar):min(ih\\,iw/dar):(iw-min(iw\\,iw*sar))/2:(ih - min (ih\\,ih/sar))/2,setsar=sar=1",
                // ftab crop heigt in half, set the display aspect,crop out any black bars we may have made
                Video3DFormat.FullTopAndBottom => "crop=iw:ih/2:0:0,setdar=dar=a,crop=min(iw\\,ih*dar):min(ih\\,iw/dar):(iw-min(iw\\,iw*sar))/2:(ih - min (ih\\,ih/sar))/2,setsar=sar=1",
                _ => "scale=trunc(iw*sar):ih"
            };

            filters.Add(scaler);

            // Use ffmpeg to sample 100 (we can drop this if required using thumbnail=50 for 50 frames) frames and pick the best thumbnail. Have a fall back just in case.
            // mpegts need larger batch size otherwise the corrupted thumbnail will be created. Larger batch size will lower the processing speed.
            var enableThumbnail = useIFrame && !string.Equals("wtv", container, StringComparison.OrdinalIgnoreCase);
            if (enableThumbnail)
            {
                var useLargerBatchSize = string.Equals("mpegts", container, StringComparison.OrdinalIgnoreCase);
                filters.Add("thumbnail=n=" + (useLargerBatchSize ? "50" : "24"));
            }

            // Use SW tonemap on HDR10/HLG video stream only when the zscale filter is available.
            var enableHdrExtraction = false;

            if ((string.Equals(videoStream?.ColorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase)
                || string.Equals(videoStream?.ColorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase))
                && SupportsFilter("zscale"))
            {
                enableHdrExtraction = true;

                filters.Add("zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0:peak=100,zscale=t=bt709:m=bt709,format=yuv420p");
            }

            var vf = string.Join(',', filters);
            var mapArg = imageStreamIndex.HasValue ? (" -map 0:" + imageStreamIndex.Value.ToString(CultureInfo.InvariantCulture)) : string.Empty;
            var args = string.Format(CultureInfo.InvariantCulture, "-i {0}{3} -threads {4} -v quiet -vframes 1 -vf {2} -f image2 \"{1}\"", inputPath, tempExtractPath, vf, mapArg, _threads);

            if (offset.HasValue)
            {
                args = string.Format(CultureInfo.InvariantCulture, "-ss {0} ", GetTimeParameter(offset.Value)) + args;
            }

            if (!string.IsNullOrWhiteSpace(container))
            {
                var inputFormat = EncodingHelper.GetInputFormat(container);
                if (!string.IsNullOrWhiteSpace(inputFormat))
                {
                    args = "-f " + inputFormat + " " + args;
                }
            }

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    FileName = _ffmpegPath,
                    Arguments = args,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    ErrorDialog = false,
                },
                EnableRaisingEvents = true
            };

            _logger.LogDebug("{ProcessFileName} {ProcessArguments}", process.StartInfo.FileName, process.StartInfo.Arguments);

            using (var processWrapper = new ProcessWrapper(process, this))
            {
                bool ranToCompletion;

                await _thumbnailResourcePool.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    StartProcess(processWrapper);

                    var timeoutMs = _configurationManager.Configuration.ImageExtractionTimeoutMs;
                    if (timeoutMs <= 0)
                    {
                        timeoutMs = enableHdrExtraction ? DefaultHdrImageExtractionTimeout : DefaultSdrImageExtractionTimeout;
                    }

                    ranToCompletion = await process.WaitForExitAsync(TimeSpan.FromMilliseconds(timeoutMs)).ConfigureAwait(false);

                    if (!ranToCompletion)
                    {
                        StopProcess(processWrapper, 1000);
                    }
                }
                finally
                {
                    _thumbnailResourcePool.Release();
                }

                var exitCode = ranToCompletion ? processWrapper.ExitCode ?? 0 : -1;
                var file = _fileSystem.GetFileInfo(tempExtractPath);

                if (exitCode == -1 || !file.Exists || file.Length == 0)
                {
                    _logger.LogError("ffmpeg image extraction failed for {Path}", inputPath);

                    throw new FfmpegException(string.Format(CultureInfo.InvariantCulture, "ffmpeg image extraction failed for {0}", inputPath));
                }

                return tempExtractPath;
            }
        }

        public string GetTimeParameter(long ticks)
        {
            var time = TimeSpan.FromTicks(ticks);

            return GetTimeParameter(time);
        }

        public string GetTimeParameter(TimeSpan time)
        {
            return time.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
        }

        private void StartProcess(ProcessWrapper process)
        {
            process.Process.Start();

            lock (_runningProcessesLock)
            {
                _runningProcesses.Add(process);
            }
        }

        private void StopProcess(ProcessWrapper process, int waitTimeMs)
        {
            try
            {
                if (process.Process.WaitForExit(waitTimeMs))
                {
                    return;
                }

                _logger.LogInformation("Killing ffmpeg process");

                process.Process.Kill();
            }
            catch (InvalidOperationException)
            {
                // The process has already exited or
                // there is no process associated with this Process object.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error killing process");
            }
        }

        private void StopProcesses()
        {
            List<ProcessWrapper> proceses;
            lock (_runningProcessesLock)
            {
                proceses = _runningProcesses.ToList();
                _runningProcesses.Clear();
            }

            foreach (var process in proceses)
            {
                if (!process.HasExited)
                {
                    StopProcess(process, 500);
                }
            }
        }

        public string EscapeSubtitleFilterPath(string path)
        {
            // https://ffmpeg.org/ffmpeg-filters.html#Notes-on-filtergraph-escaping
            // We need to double escape

            return path.Replace('\\', '/').Replace(":", "\\:", StringComparison.Ordinal).Replace("'", "'\\\\\\''", StringComparison.Ordinal);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="dispose"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool dispose)
        {
            if (dispose)
            {
                StopProcesses();
                _thumbnailResourcePool.Dispose();
            }
        }

        /// <inheritdoc />
        public Task ConvertImage(string inputPath, string outputPath)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        public IEnumerable<string> GetPrimaryPlaylistVobFiles(string path, uint? titleNumber)
        {
            // min size 300 mb
            const long MinPlayableSize = 314572800;

            // Try to eliminate menus and intros by skipping all files at the front of the list that are less than the minimum size
            // Once we reach a file that is at least the minimum, return all subsequent ones
            var allVobs = _fileSystem.GetFiles(path, true)
                .Where(file => string.Equals(file.Extension, ".vob", StringComparison.OrdinalIgnoreCase))
                .OrderBy(i => i.FullName)
                .ToList();

            // If we didn't find any satisfying the min length, just take them all
            if (allVobs.Count == 0)
            {
                _logger.LogWarning("No vobs found in dvd structure.");
                return Enumerable.Empty<string>();
            }

            if (titleNumber.HasValue)
            {
                var prefix = string.Format(
                    CultureInfo.InvariantCulture,
                    titleNumber.Value >= 10 ? "VTS_{0}_" : "VTS_0{0}_",
                    titleNumber.Value);
                var vobs = allVobs.Where(i => i.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();

                if (vobs.Count > 0)
                {
                    var minSizeVobs = vobs
                        .SkipWhile(f => f.Length < MinPlayableSize)
                        .ToList();

                    return minSizeVobs.Count == 0 ? vobs.Select(i => i.FullName) : minSizeVobs.Select(i => i.FullName);
                }

                _logger.LogWarning("Could not determine vob file list for {Path} using DvdLib. Will scan using file sizes.", path);
            }

            var files = allVobs
                .SkipWhile(f => f.Length < MinPlayableSize)
                .ToList();

            // If we didn't find any satisfying the min length, just take them all
            if (files.Count == 0)
            {
                _logger.LogWarning("Vob size filter resulted in zero matches. Taking all vobs.");
                files = allVobs;
            }

            // Assuming they're named "vts_05_01", take all files whose second part matches that of the first file
            if (files.Count > 0)
            {
                var parts = _fileSystem.GetFileNameWithoutExtension(files[0]).Split('_');

                if (parts.Length == 3)
                {
                    var title = parts[1];

                    files = files.TakeWhile(f =>
                    {
                        var fileParts = _fileSystem.GetFileNameWithoutExtension(f).Split('_');

                        return fileParts.Length == 3 && string.Equals(title, fileParts[1], StringComparison.OrdinalIgnoreCase);
                    }).ToList();

                    // If this resulted in not getting any vobs, just take them all
                    if (files.Count == 0)
                    {
                        _logger.LogWarning("Vob filename filter resulted in zero matches. Taking all vobs.");
                        files = allVobs;
                    }
                }
            }

            return files.Select(i => i.FullName);
        }

        public bool CanExtractSubtitles(string codec)
        {
            // TODO is there ever a case when a subtitle can't be extracted??
            return true;
        }

        private class ProcessWrapper : IDisposable
        {
            private readonly MediaEncoder _mediaEncoder;

            private bool _disposed = false;

            public ProcessWrapper(Process process, MediaEncoder mediaEncoder)
            {
                Process = process;
                _mediaEncoder = mediaEncoder;
                Process.Exited += OnProcessExited;
            }

            public Process Process { get; }

            public bool HasExited { get; private set; }

            public int? ExitCode { get; private set; }

            private void OnProcessExited(object sender, EventArgs e)
            {
                var process = (Process)sender;

                HasExited = true;

                try
                {
                    ExitCode = process.ExitCode;
                }
                catch
                {
                }

                DisposeProcess(process);
            }

            private void DisposeProcess(Process process)
            {
                lock (_mediaEncoder._runningProcessesLock)
                {
                    _mediaEncoder._runningProcesses.Remove(this);
                }

                try
                {
                    process.Dispose();
                }
                catch
                {
                }
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    if (Process != null)
                    {
                        Process.Exited -= OnProcessExited;
                        DisposeProcess(Process);
                    }
                }

                _disposed = true;
            }
        }
    }
}
