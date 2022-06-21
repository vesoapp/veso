#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Extensions;
using Jellyfin.XmlTv;
using Jellyfin.XmlTv.Entities;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.LiveTv.Listings
{
    public class XmlTvListingsProvider : IListingsProvider
    {
        private static readonly TimeSpan _maxCacheAge = TimeSpan.FromHours(1);

        private readonly IServerConfigurationManager _config;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<XmlTvListingsProvider> _logger;
        private readonly IFileSystem _fileSystem;
        private readonly IZipClient _zipClient;

        public XmlTvListingsProvider(
            IServerConfigurationManager config,
            IHttpClientFactory httpClientFactory,
            ILogger<XmlTvListingsProvider> logger,
            IFileSystem fileSystem,
            IZipClient zipClient)
        {
            _config = config;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _fileSystem = fileSystem;
            _zipClient = zipClient;
        }

        public string Name => "XmlTV";

        public string Type => "xmltv";

        private string GetLanguage(ListingsProviderInfo info)
        {
            if (!string.IsNullOrWhiteSpace(info.PreferredLanguage))
            {
                return info.PreferredLanguage;
            }

            return _config.Configuration.PreferredMetadataLanguage;
        }

        private async Task<string> GetXml(ListingsProviderInfo info, CancellationToken cancellationToken)
        {
            _logger.LogInformation("xmltv path: {Path}", info.Path);

            if (!info.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return UnzipIfNeeded(info.Path, info.Path);
            }

            string cacheFilename = info.Id + ".xml";
            string cacheFile = Path.Combine(_config.ApplicationPaths.CachePath, "xmltv", cacheFilename);
            if (File.Exists(cacheFile) && File.GetLastWriteTimeUtc(cacheFile) >= DateTime.UtcNow.Subtract(_maxCacheAge))
            {
                return UnzipIfNeeded(info.Path, cacheFile);
            }

            // Must check if file exists as parent directory may not exist.
            if (File.Exists(cacheFile))
            {
                File.Delete(cacheFile);
            }

            _logger.LogInformation("Downloading xmltv listings from {Path}", info.Path);

            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile));

            using var response = await _httpClientFactory.CreateClient(NamedClient.Default).GetAsync(info.Path, cancellationToken).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var fileStream = new FileStream(cacheFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, IODefaults.CopyToBufferSize, FileOptions.Asynchronous))
            {
                await stream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            return UnzipIfNeeded(info.Path, cacheFile);
        }

        private string UnzipIfNeeded(ReadOnlySpan<char> originalUrl, string file)
        {
            ReadOnlySpan<char> ext = Path.GetExtension(originalUrl.LeftPart('?'));

            if (ext.Equals(".gz", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string tempFolder = ExtractGz(file);
                    return FindXmlFile(tempFolder);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error extracting from gz file {File}", file);
                }

                try
                {
                    string tempFolder = ExtractFirstFileFromGz(file);
                    return FindXmlFile(tempFolder);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error extracting from zip file {File}", file);
                }
            }

            return file;
        }

        private string ExtractFirstFileFromGz(string file)
        {
            using (var stream = File.OpenRead(file))
            {
                string tempFolder = GetTempFolderPath(stream);
                Directory.CreateDirectory(tempFolder);

                _zipClient.ExtractFirstFileFromGz(stream, tempFolder, "data.xml");

                return tempFolder;
            }
        }

        private string ExtractGz(string file)
        {
            using (var stream = File.OpenRead(file))
            {
                string tempFolder = GetTempFolderPath(stream);
                Directory.CreateDirectory(tempFolder);

                _zipClient.ExtractAllFromGz(stream, tempFolder, true);

                return tempFolder;
            }
        }

        private string GetTempFolderPath(Stream stream)
        {
#pragma warning disable CA5351
            using var md5 = MD5.Create();
#pragma warning restore CA5351
            var checksum = Convert.ToHexString(md5.ComputeHash(stream));
            stream.Position = 0;
            return Path.Combine(_config.ApplicationPaths.TempDirectory, checksum);
        }

        private string FindXmlFile(string directory)
        {
            return _fileSystem.GetFiles(directory, true)
                .Where(i => string.Equals(i.Extension, ".xml", StringComparison.OrdinalIgnoreCase))
                .Select(i => i.FullName)
                .FirstOrDefault();
        }

        public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(ListingsProviderInfo info, string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(channelId))
            {
                throw new ArgumentNullException(nameof(channelId));
            }

            _logger.LogDebug("Getting xmltv programs for channel {Id}", channelId);

            string path = await GetXml(info, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Opening XmlTvReader for {Path}", path);
            var reader = new XmlTvReader(path, GetLanguage(info));

            return reader.GetProgrammes(channelId, startDateUtc, endDateUtc, cancellationToken)
                        .Select(p => GetProgramInfo(p, info));
        }

        private static ProgramInfo GetProgramInfo(XmlTvProgram program, ListingsProviderInfo info)
        {
            string episodeTitle = program.Episode?.Title;

            var programInfo = new ProgramInfo
            {
                ChannelId = program.ChannelId,
                EndDate = program.EndDate.UtcDateTime,
                EpisodeNumber = program.Episode?.Episode,
                EpisodeTitle = episodeTitle,
                Genres = program.Categories,
                StartDate = program.StartDate.UtcDateTime,
                Name = program.Title,
                Overview = program.Description,
                ProductionYear = program.CopyrightDate?.Year,
                SeasonNumber = program.Episode?.Series,
                IsSeries = program.Episode != null,
                IsRepeat = program.IsPreviouslyShown && !program.IsNew,
                IsPremiere = program.Premiere != null,
                IsKids = program.Categories.Any(c => info.KidsCategories.Contains(c, StringComparison.OrdinalIgnoreCase)),
                IsMovie = program.Categories.Any(c => info.MovieCategories.Contains(c, StringComparison.OrdinalIgnoreCase)),
                IsNews = program.Categories.Any(c => info.NewsCategories.Contains(c, StringComparison.OrdinalIgnoreCase)),
                IsSports = program.Categories.Any(c => info.SportsCategories.Contains(c, StringComparison.OrdinalIgnoreCase)),
                ImageUrl = program.Icon != null && !string.IsNullOrEmpty(program.Icon.Source) ? program.Icon.Source : null,
                HasImage = program.Icon != null && !string.IsNullOrEmpty(program.Icon.Source),
                OfficialRating = program.Rating != null && !string.IsNullOrEmpty(program.Rating.Value) ? program.Rating.Value : null,
                CommunityRating = program.StarRating,
                SeriesId = program.Episode == null ? null : program.Title.GetMD5().ToString("N", CultureInfo.InvariantCulture)
            };

            if (string.IsNullOrWhiteSpace(program.ProgramId))
            {
                string uniqueString = (program.Title ?? string.Empty) + (episodeTitle ?? string.Empty) /*+ (p.IceTvEpisodeNumber ?? string.Empty)*/;

                if (programInfo.SeasonNumber.HasValue)
                {
                    uniqueString = "-" + programInfo.SeasonNumber.Value.ToString(CultureInfo.InvariantCulture);
                }

                if (programInfo.EpisodeNumber.HasValue)
                {
                    uniqueString = "-" + programInfo.EpisodeNumber.Value.ToString(CultureInfo.InvariantCulture);
                }

                programInfo.ShowId = uniqueString.GetMD5().ToString("N", CultureInfo.InvariantCulture);

                // If we don't have valid episode info, assume it's a unique program, otherwise recordings might be skipped
                if (programInfo.IsSeries
                    && !programInfo.IsRepeat
                    && (programInfo.EpisodeNumber ?? 0) == 0)
                {
                    programInfo.ShowId += programInfo.StartDate.Ticks.ToString(CultureInfo.InvariantCulture);
                }
            }
            else
            {
                programInfo.ShowId = program.ProgramId;
            }

            // Construct an id from the channel and start date
            programInfo.Id = string.Format(CultureInfo.InvariantCulture, "{0}_{1:O}", program.ChannelId, program.StartDate);

            if (programInfo.IsMovie)
            {
                programInfo.IsSeries = false;
                programInfo.EpisodeNumber = null;
                programInfo.EpisodeTitle = null;
            }

            return programInfo;
        }

        public Task Validate(ListingsProviderInfo info, bool validateLogin, bool validateListings)
        {
            // Assume all urls are valid. check files for existence
            if (!info.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !File.Exists(info.Path))
            {
                throw new FileNotFoundException("Could not find the XmlTv file specified:", info.Path);
            }

            return Task.CompletedTask;
        }

        public async Task<List<NameIdPair>> GetLineups(ListingsProviderInfo info, string country, string location)
        {
            // In theory this should never be called because there is always only one lineup
            string path = await GetXml(info, CancellationToken.None).ConfigureAwait(false);
            _logger.LogDebug("Opening XmlTvReader for {Path}", path);
            var reader = new XmlTvReader(path, GetLanguage(info));
            IEnumerable<XmlTvChannel> results = reader.GetChannels();

            // Should this method be async?
            return results.Select(c => new NameIdPair() { Id = c.Id, Name = c.DisplayName }).ToList();
        }

        public async Task<List<ChannelInfo>> GetChannels(ListingsProviderInfo info, CancellationToken cancellationToken)
        {
            // In theory this should never be called because there is always only one lineup
            string path = await GetXml(info, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Opening XmlTvReader for {Path}", path);
            var reader = new XmlTvReader(path, GetLanguage(info));
            var results = reader.GetChannels();

            // Should this method be async?
            return results.Select(c => new ChannelInfo
            {
                Id = c.Id,
                Name = c.DisplayName,
                ImageUrl = c.Icon != null && !string.IsNullOrEmpty(c.Icon.Source) ? c.Icon.Source : null,
                Number = string.IsNullOrWhiteSpace(c.Number) ? c.Id : c.Number
            }).ToList();
        }
    }
}
