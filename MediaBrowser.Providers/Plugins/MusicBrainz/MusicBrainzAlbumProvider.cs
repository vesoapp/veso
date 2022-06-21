#nullable disable

#pragma warning disable CS1591, SA1401

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using MediaBrowser.Providers.Plugins.MusicBrainz;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Providers.Music
{
    public class MusicBrainzAlbumProvider : IRemoteMetadataProvider<MusicAlbum, AlbumInfo>, IHasOrder, IDisposable
    {
        /// <summary>
        /// For each single MB lookup/search, this is the maximum number of
        /// attempts that shall be made whilst receiving a 503 Server
        /// Unavailable (indicating throttled) response.
        /// </summary>
        private const uint MusicBrainzQueryAttempts = 5u;

        /// <summary>
        /// The Jellyfin user-agent is unrestricted but source IP must not exceed
        /// one request per second, therefore we rate limit to avoid throttling.
        /// Be prudent, use a value slightly above the minimun required.
        /// https://musicbrainz.org/doc/XML_Web_Service/Rate_Limiting.
        /// </summary>
        private readonly long _musicBrainzQueryIntervalMs;

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<MusicBrainzAlbumProvider> _logger;

        private readonly string _musicBrainzBaseUrl;

        private SemaphoreSlim _apiRequestLock = new SemaphoreSlim(1, 1);
        private Stopwatch _stopWatchMusicBrainz = new Stopwatch();

        public MusicBrainzAlbumProvider(
            IHttpClientFactory httpClientFactory,
            ILogger<MusicBrainzAlbumProvider> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;

            _musicBrainzBaseUrl = Plugin.Instance.Configuration.Server;
            _musicBrainzQueryIntervalMs = Plugin.Instance.Configuration.RateLimit;

            // Use a stopwatch to ensure we don't exceed the MusicBrainz rate limit
            _stopWatchMusicBrainz.Start();

            Current = this;
        }

        internal static MusicBrainzAlbumProvider Current { get; private set; }

        /// <inheritdoc />
        public string Name => "MusicBrainz";

        /// <inheritdoc />
        public int Order => 0;

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(AlbumInfo searchInfo, CancellationToken cancellationToken)
        {
            var releaseId = searchInfo.GetReleaseId();
            var releaseGroupId = searchInfo.GetReleaseGroupId();

            string url;

            if (!string.IsNullOrEmpty(releaseId))
            {
                url = "/ws/2/release/?query=reid:" + releaseId.ToString(CultureInfo.InvariantCulture);
            }
            else if (!string.IsNullOrEmpty(releaseGroupId))
            {
                url = "/ws/2/release?release-group=" + releaseGroupId.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                var artistMusicBrainzId = searchInfo.GetMusicBrainzArtistId();

                if (!string.IsNullOrWhiteSpace(artistMusicBrainzId))
                {
                    url = string.Format(
                        CultureInfo.InvariantCulture,
                        "/ws/2/release/?query=\"{0}\" AND arid:{1}",
                        WebUtility.UrlEncode(searchInfo.Name),
                        artistMusicBrainzId);
                }
                else
                {
                    // I'm sure there is a better way but for now it resolves search for 12" Mixes
                    var queryName = searchInfo.Name.Replace("\"", string.Empty, StringComparison.Ordinal);

                    url = string.Format(
                        CultureInfo.InvariantCulture,
                        "/ws/2/release/?query=\"{0}\" AND artist:\"{1}\"",
                        WebUtility.UrlEncode(queryName),
                        WebUtility.UrlEncode(searchInfo.GetAlbumArtist()));
                }
            }

            if (!string.IsNullOrWhiteSpace(url))
            {
                using var response = await GetMusicBrainzResponse(url, cancellationToken).ConfigureAwait(false);
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return GetResultsFromResponse(stream);
            }

            return Enumerable.Empty<RemoteSearchResult>();
        }

        private IEnumerable<RemoteSearchResult> GetResultsFromResponse(Stream stream)
        {
            using var oReader = new StreamReader(stream, Encoding.UTF8);
            var settings = new XmlReaderSettings()
            {
                ValidationType = ValidationType.None,
                CheckCharacters = false,
                IgnoreProcessingInstructions = true,
                IgnoreComments = true
            };

            using var reader = XmlReader.Create(oReader, settings);
            var results = ReleaseResult.Parse(reader);

            return results.Select(i =>
            {
                var result = new RemoteSearchResult
                {
                    Name = i.Title,
                    ProductionYear = i.Year
                };

                if (i.Artists.Count > 0)
                {
                    result.AlbumArtist = new RemoteSearchResult
                    {
                        SearchProviderName = Name,
                        Name = i.Artists[0].Item1
                    };

                    result.AlbumArtist.SetProviderId(MetadataProvider.MusicBrainzArtist, i.Artists[0].Item2);
                }

                if (!string.IsNullOrWhiteSpace(i.ReleaseId))
                {
                    result.SetProviderId(MetadataProvider.MusicBrainzAlbum, i.ReleaseId);
                }

                if (!string.IsNullOrWhiteSpace(i.ReleaseGroupId))
                {
                    result.SetProviderId(MetadataProvider.MusicBrainzReleaseGroup, i.ReleaseGroupId);
                }

                return result;
            });
        }

        /// <inheritdoc />
        public async Task<MetadataResult<MusicAlbum>> GetMetadata(AlbumInfo info, CancellationToken cancellationToken)
        {
            var releaseId = info.GetReleaseId();
            var releaseGroupId = info.GetReleaseGroupId();

            var result = new MetadataResult<MusicAlbum>
            {
                Item = new MusicAlbum()
            };

            // If we have a release group Id but not a release Id...
            if (string.IsNullOrWhiteSpace(releaseId) && !string.IsNullOrWhiteSpace(releaseGroupId))
            {
                releaseId = await GetReleaseIdFromReleaseGroupId(releaseGroupId, cancellationToken).ConfigureAwait(false);
                result.HasMetadata = true;
            }

            if (string.IsNullOrWhiteSpace(releaseId))
            {
                var artistMusicBrainzId = info.GetMusicBrainzArtistId();

                var releaseResult = await GetReleaseResult(artistMusicBrainzId, info.GetAlbumArtist(), info.Name, cancellationToken).ConfigureAwait(false);

                if (releaseResult != null)
                {
                    if (!string.IsNullOrWhiteSpace(releaseResult.ReleaseId))
                    {
                        releaseId = releaseResult.ReleaseId;
                        result.HasMetadata = true;
                    }

                    if (!string.IsNullOrWhiteSpace(releaseResult.ReleaseGroupId))
                    {
                        releaseGroupId = releaseResult.ReleaseGroupId;
                        result.HasMetadata = true;
                    }

                    result.Item.ProductionYear = releaseResult.Year;
                    result.Item.Overview = releaseResult.Overview;
                }
            }

            // If we have a release Id but not a release group Id...
            if (!string.IsNullOrWhiteSpace(releaseId) && string.IsNullOrWhiteSpace(releaseGroupId))
            {
                releaseGroupId = await GetReleaseGroupFromReleaseId(releaseId, cancellationToken).ConfigureAwait(false);
                result.HasMetadata = true;
            }

            if (!string.IsNullOrWhiteSpace(releaseId) || !string.IsNullOrWhiteSpace(releaseGroupId))
            {
                result.HasMetadata = true;
            }

            if (result.HasMetadata)
            {
                if (!string.IsNullOrEmpty(releaseId))
                {
                    result.Item.SetProviderId(MetadataProvider.MusicBrainzAlbum, releaseId);
                }

                if (!string.IsNullOrEmpty(releaseGroupId))
                {
                    result.Item.SetProviderId(MetadataProvider.MusicBrainzReleaseGroup, releaseGroupId);
                }
            }

            return result;
        }

        private Task<ReleaseResult> GetReleaseResult(string artistMusicBrainId, string artistName, string albumName, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrEmpty(artistMusicBrainId))
            {
                return GetReleaseResult(albumName, artistMusicBrainId, cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(artistName))
            {
                return Task.FromResult(new ReleaseResult());
            }

            return GetReleaseResultByArtistName(albumName, artistName, cancellationToken);
        }

        private async Task<ReleaseResult> GetReleaseResult(string albumName, string artistId, CancellationToken cancellationToken)
        {
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "/ws/2/release/?query=\"{0}\" AND arid:{1}",
                WebUtility.UrlEncode(albumName),
                artistId);

            using var response = await GetMusicBrainzResponse(url, cancellationToken).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var oReader = new StreamReader(stream, Encoding.UTF8);
            var settings = new XmlReaderSettings
            {
                ValidationType = ValidationType.None,
                CheckCharacters = false,
                IgnoreProcessingInstructions = true,
                IgnoreComments = true
            };

            using var reader = XmlReader.Create(oReader, settings);
            return ReleaseResult.Parse(reader).FirstOrDefault();
        }

        private async Task<ReleaseResult> GetReleaseResultByArtistName(string albumName, string artistName, CancellationToken cancellationToken)
        {
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "/ws/2/release/?query=\"{0}\" AND artist:\"{1}\"",
                WebUtility.UrlEncode(albumName),
                WebUtility.UrlEncode(artistName));

            using var response = await GetMusicBrainzResponse(url, cancellationToken).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var oReader = new StreamReader(stream, Encoding.UTF8);
            var settings = new XmlReaderSettings()
            {
                ValidationType = ValidationType.None,
                CheckCharacters = false,
                IgnoreProcessingInstructions = true,
                IgnoreComments = true
            };

            using var reader = XmlReader.Create(oReader, settings);
            return ReleaseResult.Parse(reader).FirstOrDefault();
        }

        private static (string Name, string ArtistId) ParseArtistCredit(XmlReader reader)
        {
            reader.MoveToContent();
            reader.Read();

            // http://stackoverflow.com/questions/2299632/why-does-xmlreader-skip-every-other-element-if-there-is-no-whitespace-separator

            // Loop through each element
            while (!reader.EOF && reader.ReadState == ReadState.Interactive)
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.Name)
                    {
                        case "name-credit":
                        {
                            if (reader.IsEmptyElement)
                            {
                                reader.Read();
                                break;
                            }

                            using var subReader = reader.ReadSubtree();
                            return ParseArtistNameCredit(subReader);
                        }

                        default:
                        {
                            reader.Skip();
                            break;
                        }
                    }
                }
                else
                {
                    reader.Read();
                }
            }

            return default;
        }

        private static (string Name, string ArtistId) ParseArtistNameCredit(XmlReader reader)
        {
            reader.MoveToContent();
            reader.Read();

            // http://stackoverflow.com/questions/2299632/why-does-xmlreader-skip-every-other-element-if-there-is-no-whitespace-separator

            // Loop through each element
            while (!reader.EOF && reader.ReadState == ReadState.Interactive)
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.Name)
                    {
                        case "artist":
                            {
                                if (reader.IsEmptyElement)
                                {
                                    reader.Read();
                                    break;
                                }

                                var id = reader.GetAttribute("id");
                                using var subReader = reader.ReadSubtree();
                                return ParseArtistArtistCredit(subReader, id);
                            }

                        default:
                            {
                                reader.Skip();
                                break;
                            }
                    }
                }
                else
                {
                    reader.Read();
                }
            }

            return (null, null);
        }

        private static (string Name, string ArtistId) ParseArtistArtistCredit(XmlReader reader, string artistId)
        {
            reader.MoveToContent();
            reader.Read();

            string name = null;

            // http://stackoverflow.com/questions/2299632/why-does-xmlreader-skip-every-other-element-if-there-is-no-whitespace-separator

            // Loop through each element
            while (!reader.EOF && reader.ReadState == ReadState.Interactive)
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.Name)
                    {
                        case "name":
                            {
                                name = reader.ReadElementContentAsString();
                                break;
                            }

                        default:
                            {
                                reader.Skip();
                                break;
                            }
                    }
                }
                else
                {
                    reader.Read();
                }
            }

            return (name, artistId);
        }

        private async Task<string> GetReleaseIdFromReleaseGroupId(string releaseGroupId, CancellationToken cancellationToken)
        {
            var url = "/ws/2/release?release-group=" + releaseGroupId.ToString(CultureInfo.InvariantCulture);

            using var response = await GetMusicBrainzResponse(url, cancellationToken).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var oReader = new StreamReader(stream, Encoding.UTF8);
            var settings = new XmlReaderSettings
            {
                ValidationType = ValidationType.None,
                CheckCharacters = false,
                IgnoreProcessingInstructions = true,
                IgnoreComments = true
            };

            using var reader = XmlReader.Create(oReader, settings);
            var result = ReleaseResult.Parse(reader).FirstOrDefault();

            return result?.ReleaseId;
        }

        /// <summary>
        /// Gets the release group id internal.
        /// </summary>
        /// <param name="releaseEntryId">The release entry id.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task{System.String}.</returns>
        private async Task<string> GetReleaseGroupFromReleaseId(string releaseEntryId, CancellationToken cancellationToken)
        {
            var url = "/ws/2/release-group/?query=reid:" + releaseEntryId.ToString(CultureInfo.InvariantCulture);

            using var response = await GetMusicBrainzResponse(url, cancellationToken).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var oReader = new StreamReader(stream, Encoding.UTF8);
            var settings = new XmlReaderSettings
            {
                ValidationType = ValidationType.None,
                CheckCharacters = false,
                IgnoreProcessingInstructions = true,
                IgnoreComments = true,
                Async = true
            };

            using var reader = XmlReader.Create(oReader, settings);
            await reader.MoveToContentAsync().ConfigureAwait(false);
            await reader.ReadAsync().ConfigureAwait(false);

            // Loop through each element
            while (!reader.EOF && reader.ReadState == ReadState.Interactive)
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.Name)
                    {
                        case "release-group-list":
                        {
                            if (reader.IsEmptyElement)
                            {
                                await reader.ReadAsync().ConfigureAwait(false);
                                continue;
                            }

                            using var subReader = reader.ReadSubtree();
                            return GetFirstReleaseGroupId(subReader);
                        }

                        default:
                        {
                            await reader.SkipAsync().ConfigureAwait(false);
                            break;
                        }
                    }
                }
                else
                {
                    await reader.ReadAsync().ConfigureAwait(false);
                }
            }

            return null;
        }

        private string GetFirstReleaseGroupId(XmlReader reader)
        {
            reader.MoveToContent();
            reader.Read();

            // Loop through each element
            while (!reader.EOF && reader.ReadState == ReadState.Interactive)
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.Name)
                    {
                        case "release-group":
                            {
                                return reader.GetAttribute("id");
                            }

                        default:
                            {
                                reader.Skip();
                                break;
                            }
                    }
                }
                else
                {
                    reader.Read();
                }
            }

            return null;
        }

        /// <summary>
        /// Makes request to MusicBrainz server and awaits a response.
        /// A 503 Service Unavailable response indicates throttling to maintain a rate limit.
        /// A number of retries shall be made in order to try and satisfy the request before
        /// giving up and returning null.
        /// </summary>
        /// <param name="url">Address of MusicBrainz server.</param>
        /// <param name="cancellationToken">CancellationToken to use for method.</param>
        /// <returns>Returns response from MusicBrainz service.</returns>
        internal async Task<HttpResponseMessage> GetMusicBrainzResponse(string url, CancellationToken cancellationToken)
        {
            await _apiRequestLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                HttpResponseMessage response;
                var attempts = 0u;
                var requestUrl = _musicBrainzBaseUrl.TrimEnd('/') + url;

                do
                {
                    attempts++;

                    if (_stopWatchMusicBrainz.ElapsedMilliseconds < _musicBrainzQueryIntervalMs)
                    {
                        // MusicBrainz is extremely adamant about limiting to one request per second.
                        var delayMs = _musicBrainzQueryIntervalMs - _stopWatchMusicBrainz.ElapsedMilliseconds;
                        await Task.Delay((int)delayMs, cancellationToken).ConfigureAwait(false);
                    }

                    // Write time since last request to debug log as evidence we're meeting rate limit
                    // requirement, before resetting stopwatch back to zero.
                    _logger.LogDebug("GetMusicBrainzResponse: Time since previous request: {0} ms", _stopWatchMusicBrainz.ElapsedMilliseconds);
                    _stopWatchMusicBrainz.Restart();

                    using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                    response = await _httpClientFactory
                        .CreateClient(NamedClient.MusicBrainz)
                        .SendAsync(request, cancellationToken)
                        .ConfigureAwait(false);

                    // We retry a finite number of times, and only whilst MB is indicating 503 (throttling).
                }
                while (attempts < MusicBrainzQueryAttempts && response.StatusCode == HttpStatusCode.ServiceUnavailable);

                // Log error if unable to query MB database due to throttling.
                if (attempts == MusicBrainzQueryAttempts && response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    _logger.LogError("GetMusicBrainzResponse: 503 Service Unavailable (throttled) response received {0} times whilst requesting {1}", attempts, requestUrl);
                }

                return response;
            }
            finally
            {
                _apiRequestLock.Release();
            }
        }

        /// <inheritdoc />
        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _apiRequestLock?.Dispose();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private class ReleaseResult
        {
            public string ReleaseId;
            public string ReleaseGroupId;
            public string Title;
            public string Overview;
            public int? Year;

            public List<(string, string)> Artists = new();

            public static IEnumerable<ReleaseResult> Parse(XmlReader reader)
            {
                reader.MoveToContent();
                reader.Read();

                // Loop through each element
                while (!reader.EOF && reader.ReadState == ReadState.Interactive)
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        switch (reader.Name)
                        {
                            case "release-list":
                                {
                                    if (reader.IsEmptyElement)
                                    {
                                        reader.Read();
                                        continue;
                                    }

                                    using var subReader = reader.ReadSubtree();
                                    return ParseReleaseList(subReader).ToList();
                                }

                            default:
                                {
                                    reader.Skip();
                                    break;
                                }
                        }
                    }
                    else
                    {
                        reader.Read();
                    }
                }

                return Enumerable.Empty<ReleaseResult>();
            }

            private static IEnumerable<ReleaseResult> ParseReleaseList(XmlReader reader)
            {
                reader.MoveToContent();
                reader.Read();

                // Loop through each element
                while (!reader.EOF && reader.ReadState == ReadState.Interactive)
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        switch (reader.Name)
                        {
                            case "release":
                                {
                                    if (reader.IsEmptyElement)
                                    {
                                        reader.Read();
                                        continue;
                                    }

                                    var releaseId = reader.GetAttribute("id");

                                    using var subReader = reader.ReadSubtree();
                                    var release = ParseRelease(subReader, releaseId);
                                    if (release != null)
                                    {
                                        yield return release;
                                    }

                                    break;
                                }

                            default:
                                {
                                    reader.Skip();
                                    break;
                                }
                        }
                    }
                    else
                    {
                        reader.Read();
                    }
                }
            }

            private static ReleaseResult ParseRelease(XmlReader reader, string releaseId)
            {
                var result = new ReleaseResult
                {
                    ReleaseId = releaseId
                };

                reader.MoveToContent();
                reader.Read();

                // http://stackoverflow.com/questions/2299632/why-does-xmlreader-skip-every-other-element-if-there-is-no-whitespace-separator

                // Loop through each element
                while (!reader.EOF && reader.ReadState == ReadState.Interactive)
                {
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        switch (reader.Name)
                        {
                            case "title":
                                {
                                    result.Title = reader.ReadElementContentAsString();
                                    break;
                                }

                            case "date":
                                {
                                    var val = reader.ReadElementContentAsString();
                                    if (DateTime.TryParse(val, out var date))
                                    {
                                        result.Year = date.Year;
                                    }

                                    break;
                                }

                            case "annotation":
                                {
                                    result.Overview = reader.ReadElementContentAsString();
                                    break;
                                }

                            case "release-group":
                                {
                                    result.ReleaseGroupId = reader.GetAttribute("id");
                                    reader.Skip();
                                    break;
                                }

                            case "artist-credit":
                                {
                                    if (reader.IsEmptyElement)
                                    {
                                        reader.Read();
                                        break;
                                    }

                                    using var subReader = reader.ReadSubtree();
                                    var artist = ParseArtistCredit(subReader);

                                    if (!string.IsNullOrEmpty(artist.Name))
                                    {
                                        result.Artists.Add(artist);
                                    }

                                    break;
                                }

                            default:
                                {
                                    reader.Skip();
                                    break;
                                }
                        }
                    }
                    else
                    {
                        reader.Read();
                    }
                }

                return result;
            }
        }
    }
}
