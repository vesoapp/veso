#pragma warning disable CS1591
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Emby.Dlna.Profiles;
using Emby.Dlna.Server;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Dlna;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Emby.Dlna
{
    public class DlnaManager : IDlnaManager
    {
        private readonly IApplicationPaths _appPaths;
        private readonly IXmlSerializer _xmlSerializer;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger<DlnaManager> _logger;
        private readonly IServerApplicationHost _appHost;
        private static readonly Assembly _assembly = typeof(DlnaManager).Assembly;
        private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;

        private readonly Dictionary<string, Tuple<InternalProfileInfo, DeviceProfile>> _profiles = new Dictionary<string, Tuple<InternalProfileInfo, DeviceProfile>>(StringComparer.Ordinal);

        public DlnaManager(
            IXmlSerializer xmlSerializer,
            IFileSystem fileSystem,
            IApplicationPaths appPaths,
            ILoggerFactory loggerFactory,
            IServerApplicationHost appHost)
        {
            _xmlSerializer = xmlSerializer;
            _fileSystem = fileSystem;
            _appPaths = appPaths;
            _logger = loggerFactory.CreateLogger<DlnaManager>();
            _appHost = appHost;
        }

        private string UserProfilesPath => Path.Combine(_appPaths.ConfigurationDirectoryPath, "dlna", "user");

        private string SystemProfilesPath => Path.Combine(_appPaths.ConfigurationDirectoryPath, "dlna", "system");

        public async Task InitProfilesAsync()
        {
            try
            {
                await ExtractSystemProfilesAsync().ConfigureAwait(false);
                LoadProfiles();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error extracting DLNA profiles.");
            }
        }

        private void LoadProfiles()
        {
            var list = GetProfiles(UserProfilesPath, DeviceProfileType.User)
                .OrderBy(i => i.Name)
                .ToList();

            list.AddRange(GetProfiles(SystemProfilesPath, DeviceProfileType.System)
                .OrderBy(i => i.Name));
        }

        public IEnumerable<DeviceProfile> GetProfiles()
        {
            lock (_profiles)
            {
                return _profiles.Values
                    .OrderBy(i => i.Item1.Info.Type == DeviceProfileType.User ? 0 : 1)
                    .ThenBy(i => i.Item1.Info.Name)
                    .Select(i => i.Item2)
                    .ToList();
            }
        }

        /// <inheritdoc />
        public DeviceProfile GetDefaultProfile()
        {
            return new DefaultProfile();
        }

        /// <inheritdoc />
        public DeviceProfile? GetProfile(DeviceIdentification deviceInfo)
        {
            if (deviceInfo == null)
            {
                throw new ArgumentNullException(nameof(deviceInfo));
            }

            var profile = GetProfiles()
                .FirstOrDefault(i => i.Identification != null && IsMatch(deviceInfo, i.Identification));

            if (profile == null)
            {
                _logger.LogInformation("No matching device profile found. The default will need to be used. \n{@Profile}", deviceInfo);
            }
            else
            {
                _logger.LogDebug("Found matching device profile: {ProfileName}", profile.Name);
            }

            return profile;
        }

        /// <summary>
        /// Attempts to match a device with a profile.
        /// Rules:
        /// - If the profile field has no value, the field matches irregardless of its contents.
        /// - the profile field can be an exact match, or a reg exp.
        /// </summary>
        /// <param name="deviceInfo">The <see cref="DeviceIdentification"/> of the device.</param>
        /// <param name="profileInfo">The <see cref="DeviceIdentification"/> of the profile.</param>
        /// <returns><b>True</b> if they match.</returns>
        public bool IsMatch(DeviceIdentification deviceInfo, DeviceIdentification profileInfo)
        {
            return IsRegexOrSubstringMatch(deviceInfo.FriendlyName, profileInfo.FriendlyName)
                && IsRegexOrSubstringMatch(deviceInfo.Manufacturer, profileInfo.Manufacturer)
                && IsRegexOrSubstringMatch(deviceInfo.ManufacturerUrl, profileInfo.ManufacturerUrl)
                && IsRegexOrSubstringMatch(deviceInfo.ModelDescription, profileInfo.ModelDescription)
                && IsRegexOrSubstringMatch(deviceInfo.ModelName, profileInfo.ModelName)
                && IsRegexOrSubstringMatch(deviceInfo.ModelNumber, profileInfo.ModelNumber)
                && IsRegexOrSubstringMatch(deviceInfo.ModelUrl, profileInfo.ModelUrl)
                && IsRegexOrSubstringMatch(deviceInfo.SerialNumber, profileInfo.SerialNumber);
        }

        private bool IsRegexOrSubstringMatch(string input, string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                // In profile identification: An empty pattern matches anything.
                return true;
            }

            if (string.IsNullOrEmpty(input))
            {
                // The profile contains a value, and the device doesn't.
                return false;
            }

            try
            {
                return input.Equals(pattern, StringComparison.OrdinalIgnoreCase)
                    || Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch (ArgumentException ex)
            {
                _logger.LogError(ex, "Error evaluating regex pattern {Pattern}", pattern);
                return false;
            }
        }

        /// <inheritdoc />
        public DeviceProfile? GetProfile(IHeaderDictionary headers)
        {
            if (headers == null)
            {
                throw new ArgumentNullException(nameof(headers));
            }

            var profile = GetProfiles().FirstOrDefault(i => i.Identification != null && IsMatch(headers, i.Identification));
            if (profile == null)
            {
                _logger.LogDebug("No matching device profile found. {@Headers}", headers);
            }
            else
            {
                _logger.LogDebug("Found matching device profile: {0}", profile.Name);
            }

            return profile;
        }

        private bool IsMatch(IHeaderDictionary headers, DeviceIdentification profileInfo)
        {
            return profileInfo.Headers.Any(i => IsMatch(headers, i));
        }

        private bool IsMatch(IHeaderDictionary headers, HttpHeaderInfo header)
        {
            // Handle invalid user setup
            if (string.IsNullOrEmpty(header.Name))
            {
                return false;
            }

            if (headers.TryGetValue(header.Name, out StringValues value))
            {
                switch (header.Match)
                {
                    case HeaderMatchType.Equals:
                        return string.Equals(value, header.Value, StringComparison.OrdinalIgnoreCase);
                    case HeaderMatchType.Substring:
                        var isMatch = value.ToString().IndexOf(header.Value, StringComparison.OrdinalIgnoreCase) != -1;
                        // _logger.LogDebug("IsMatch-Substring value: {0} testValue: {1} isMatch: {2}", value, header.Value, isMatch);
                        return isMatch;
                    case HeaderMatchType.Regex:
                        return Regex.IsMatch(value, header.Value, RegexOptions.IgnoreCase);
                    default:
                        throw new ArgumentException("Unrecognized HeaderMatchType");
                }
            }

            return false;
        }

        private IEnumerable<DeviceProfile> GetProfiles(string path, DeviceProfileType type)
        {
            try
            {
                return _fileSystem.GetFilePaths(path)
                    .Where(i => string.Equals(Path.GetExtension(i), ".xml", StringComparison.OrdinalIgnoreCase))
                    .Select(i => ParseProfileFile(i, type))
                    .Where(i => i != null)
                    .ToList()!; // We just filtered out all the nulls
            }
            catch (IOException)
            {
                return Array.Empty<DeviceProfile>();
            }
        }

        private DeviceProfile? ParseProfileFile(string path, DeviceProfileType type)
        {
            lock (_profiles)
            {
                if (_profiles.TryGetValue(path, out Tuple<InternalProfileInfo, DeviceProfile>? profileTuple))
                {
                    return profileTuple.Item2;
                }

                try
                {
                    var tempProfile = (DeviceProfile)_xmlSerializer.DeserializeFromFile(typeof(DeviceProfile), path);
                    var profile = ReserializeProfile(tempProfile);

                    profile.Id = path.ToLowerInvariant().GetMD5().ToString("N", CultureInfo.InvariantCulture);

                    _profiles[path] = new Tuple<InternalProfileInfo, DeviceProfile>(GetInternalProfileInfo(_fileSystem.GetFileInfo(path), type), profile);

                    return profile;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error parsing profile file: {Path}", path);

                    return null;
                }
            }
        }

        /// <inheritdoc />
        public DeviceProfile? GetProfile(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentNullException(nameof(id));
            }

            var info = GetProfileInfosInternal().FirstOrDefault(i => string.Equals(i.Info.Id, id, StringComparison.OrdinalIgnoreCase));

            if (info == null)
            {
                return null;
            }

            return ParseProfileFile(info.Path, info.Info.Type);
        }

        private IEnumerable<InternalProfileInfo> GetProfileInfosInternal()
        {
            lock (_profiles)
            {
                return _profiles.Values
                    .Select(i => i.Item1)
                    .OrderBy(i => i.Info.Type == DeviceProfileType.User ? 0 : 1)
                    .ThenBy(i => i.Info.Name);
            }
        }

        /// <inheritdoc />
        public IEnumerable<DeviceProfileInfo> GetProfileInfos()
        {
            return GetProfileInfosInternal().Select(i => i.Info);
        }

        private InternalProfileInfo GetInternalProfileInfo(FileSystemMetadata file, DeviceProfileType type)
        {
            return new InternalProfileInfo(
                new DeviceProfileInfo
                {
                    Id = file.FullName.ToLowerInvariant().GetMD5().ToString("N", CultureInfo.InvariantCulture),
                    Name = _fileSystem.GetFileNameWithoutExtension(file),
                    Type = type
                },
                file.FullName);
        }

        private async Task ExtractSystemProfilesAsync()
        {
            var namespaceName = GetType().Namespace + ".Profiles.Xml.";

            var systemProfilesPath = SystemProfilesPath;

            foreach (var name in _assembly.GetManifestResourceNames())
            {
                if (!name.StartsWith(namespaceName, StringComparison.Ordinal))
                {
                    continue;
                }

                var path = Path.Join(
                    systemProfilesPath,
                    Path.GetFileName(name.AsSpan()).Slice(namespaceName.Length));

                // The stream should exist as we just got its name from GetManifestResourceNames
                using (var stream = _assembly.GetManifestResourceStream(name)!)
                {
                    var length = stream.Length;
                    var fileInfo = _fileSystem.GetFileInfo(path);

                    if (!fileInfo.Exists || fileInfo.Length != length)
                    {
                        Directory.CreateDirectory(systemProfilesPath);

                        var fileOptions = AsyncFile.WriteOptions;
                        fileOptions.Mode = FileMode.Create;
                        fileOptions.PreallocationSize = length;
                        var fileStream = new FileStream(path, fileOptions);
                        await using (fileStream.ConfigureAwait(false))
                        {
                            await stream.CopyToAsync(fileStream).ConfigureAwait(false);
                        }
                    }
                }
            }

            // Not necessary, but just to make it easy to find
            Directory.CreateDirectory(UserProfilesPath);
        }

        /// <inheritdoc />
        public void DeleteProfile(string id)
        {
            var info = GetProfileInfosInternal().First(i => string.Equals(id, i.Info.Id, StringComparison.OrdinalIgnoreCase));

            if (info.Info.Type == DeviceProfileType.System)
            {
                throw new ArgumentException("System profiles cannot be deleted.");
            }

            _fileSystem.DeleteFile(info.Path);

            lock (_profiles)
            {
                _profiles.Remove(info.Path);
            }
        }

        /// <inheritdoc />
        public void CreateProfile(DeviceProfile profile)
        {
            profile = ReserializeProfile(profile);

            if (string.IsNullOrEmpty(profile.Name))
            {
                throw new ArgumentException("Profile is missing Name");
            }

            var newFilename = _fileSystem.GetValidFilename(profile.Name) + ".xml";
            var path = Path.Combine(UserProfilesPath, newFilename);

            SaveProfile(profile, path, DeviceProfileType.User);
        }

        /// <inheritdoc />
        public void UpdateProfile(string profileId, DeviceProfile profile)
        {
            profile = ReserializeProfile(profile);

            if (string.IsNullOrEmpty(profile.Id))
            {
                throw new ArgumentException("Profile is missing Id");
            }

            if (string.IsNullOrEmpty(profile.Name))
            {
                throw new ArgumentException("Profile is missing Name");
            }

            var current = GetProfileInfosInternal().First(i => string.Equals(i.Info.Id, profileId, StringComparison.OrdinalIgnoreCase));

            var newFilename = _fileSystem.GetValidFilename(profile.Name) + ".xml";
            var path = Path.Combine(UserProfilesPath, newFilename);

            if (!string.Equals(path, current.Path, StringComparison.Ordinal) &&
                current.Info.Type != DeviceProfileType.System)
            {
                _fileSystem.DeleteFile(current.Path);
            }

            SaveProfile(profile, path, DeviceProfileType.User);
        }

        private void SaveProfile(DeviceProfile profile, string path, DeviceProfileType type)
        {
            lock (_profiles)
            {
                _profiles[path] = new Tuple<InternalProfileInfo, DeviceProfile>(GetInternalProfileInfo(_fileSystem.GetFileInfo(path), type), profile);
            }

            SerializeToXml(profile, path);
        }

        internal void SerializeToXml(DeviceProfile profile, string path)
        {
            _xmlSerializer.SerializeToFile(profile, path);
        }

        /// <summary>
        /// Recreates the object using serialization, to ensure it's not a subclass.
        /// If it's a subclass it may not serialize properly to xml (different root element tag name).
        /// </summary>
        /// <param name="profile">The device profile.</param>
        /// <returns>The re-serialized device profile.</returns>
        private DeviceProfile ReserializeProfile(DeviceProfile profile)
        {
            if (profile.GetType() == typeof(DeviceProfile))
            {
                return profile;
            }

            var json = JsonSerializer.Serialize(profile, _jsonOptions);

            // Output can't be null if the input isn't null
            return JsonSerializer.Deserialize<DeviceProfile>(json, _jsonOptions)!;
        }

        /// <inheritdoc />
        public string GetServerDescriptionXml(IHeaderDictionary headers, string serverUuId, string serverAddress)
        {
            var profile = GetProfile(headers) ?? GetDefaultProfile();

            var serverId = _appHost.SystemId;

            return new DescriptionXmlBuilder(profile, serverUuId, serverAddress, _appHost.FriendlyName, serverId).GetXml();
        }

        /// <inheritdoc />
        public ImageStream? GetIcon(string filename)
        {
            var format = filename.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? ImageFormat.Png
                : ImageFormat.Jpg;

            var resource = GetType().Namespace + ".Images." + filename.ToLowerInvariant();
            var stream = _assembly.GetManifestResourceStream(resource);
            if (stream == null)
            {
                return null;
            }

            return new ImageStream(stream)
            {
                Format = format
            };
        }

        private class InternalProfileInfo
        {
            internal InternalProfileInfo(DeviceProfileInfo info, string path)
            {
                Info = info;
                Path = path;
            }

            internal DeviceProfileInfo Info { get; }

            internal string Path { get; }
        }
    }

    /*
    class DlnaProfileEntryPoint : IServerEntryPoint
    {
        private readonly IApplicationPaths _appPaths;
        private readonly IFileSystem _fileSystem;
        private readonly IXmlSerializer _xmlSerializer;

        public DlnaProfileEntryPoint(IApplicationPaths appPaths, IFileSystem fileSystem, IXmlSerializer xmlSerializer)
        {
            _appPaths = appPaths;
            _fileSystem = fileSystem;
            _xmlSerializer = xmlSerializer;
        }

        public void Run()
        {
            DumpProfiles();
        }

        private void DumpProfiles()
        {
            DeviceProfile[] list = new[]
            {
                new SamsungSmartTvProfile(),
                new XboxOneProfile(),
                new SonyPs3Profile(),
                new SonyPs4Profile(),
                new SonyBravia2010Profile(),
                new SonyBravia2011Profile(),
                new SonyBravia2012Profile(),
                new SonyBravia2013Profile(),
                new SonyBravia2014Profile(),
                new SonyBlurayPlayer2013(),
                new SonyBlurayPlayer2014(),
                new SonyBlurayPlayer2015(),
                new SonyBlurayPlayer2016(),
                new SonyBlurayPlayerProfile(),
                new PanasonicVieraProfile(),
                new WdtvLiveProfile(),
                new DenonAvrProfile(),
                new LinksysDMA2100Profile(),
                new LgTvProfile(),
                new Foobar2000Profile(),
                new SharpSmartTvProfile(),
                new MediaMonkeyProfile(),
                // new Windows81Profile(),
                // new WindowsMediaCenterProfile(),
                // new WindowsPhoneProfile(),
                new DirectTvProfile(),
                new DishHopperJoeyProfile(),
                new DefaultProfile(),
                new PopcornHourProfile(),
                new MarantzProfile()
            };

            foreach (var item in list)
            {
                var path = Path.Combine(_appPaths.ProgramDataPath, _fileSystem.GetValidFilename(item.Name) + ".xml");

                _xmlSerializer.SerializeToFile(item, path);
            }
        }

        public void Dispose()
        {
        }
    }*/
}
