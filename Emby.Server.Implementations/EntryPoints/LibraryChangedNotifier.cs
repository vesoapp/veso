#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Entities;
using Jellyfin.Data.Events;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.EntryPoints
{
    public class LibraryChangedNotifier : IServerEntryPoint
    {
        /// <summary>
        /// The library update duration.
        /// </summary>
        private const int LibraryUpdateDuration = 30000;

        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly ISessionManager _sessionManager;
        private readonly IUserManager _userManager;
        private readonly ILogger<LibraryChangedNotifier> _logger;

        /// <summary>
        /// The library changed sync lock.
        /// </summary>
        private readonly object _libraryChangedSyncLock = new object();

        private readonly List<Folder> _foldersAddedTo = new List<Folder>();
        private readonly List<Folder> _foldersRemovedFrom = new List<Folder>();
        private readonly List<BaseItem> _itemsAdded = new List<BaseItem>();
        private readonly List<BaseItem> _itemsRemoved = new List<BaseItem>();
        private readonly List<BaseItem> _itemsUpdated = new List<BaseItem>();
        private readonly ConcurrentDictionary<Guid, DateTime> _lastProgressMessageTimes = new ConcurrentDictionary<Guid, DateTime>();

        public LibraryChangedNotifier(
            ILibraryManager libraryManager,
            ISessionManager sessionManager,
            IUserManager userManager,
            ILogger<LibraryChangedNotifier> logger,
            IProviderManager providerManager)
        {
            _libraryManager = libraryManager;
            _sessionManager = sessionManager;
            _userManager = userManager;
            _logger = logger;
            _providerManager = providerManager;
        }

        /// <summary>
        /// Gets or sets the library update timer.
        /// </summary>
        /// <value>The library update timer.</value>
        private Timer LibraryUpdateTimer { get; set; }

        public Task RunAsync()
        {
            _libraryManager.ItemAdded += OnLibraryItemAdded;
            _libraryManager.ItemUpdated += OnLibraryItemUpdated;
            _libraryManager.ItemRemoved += OnLibraryItemRemoved;

            _providerManager.RefreshCompleted += OnProviderRefreshCompleted;
            _providerManager.RefreshStarted += OnProviderRefreshStarted;
            _providerManager.RefreshProgress += OnProviderRefreshProgress;

            return Task.CompletedTask;
        }

        private void OnProviderRefreshProgress(object sender, GenericEventArgs<Tuple<BaseItem, double>> e)
        {
            var item = e.Argument.Item1;

            if (!EnableRefreshMessage(item))
            {
                return;
            }

            var progress = e.Argument.Item2;

            if (_lastProgressMessageTimes.TryGetValue(item.Id, out var lastMessageSendTime))
            {
                if (progress > 0 && progress < 100 && (DateTime.UtcNow - lastMessageSendTime).TotalMilliseconds < 1000)
                {
                    return;
                }
            }

            _lastProgressMessageTimes.AddOrUpdate(item.Id, _ => DateTime.UtcNow, (_, _) => DateTime.UtcNow);

            var dict = new Dictionary<string, string>();
            dict["ItemId"] = item.Id.ToString("N", CultureInfo.InvariantCulture);
            dict["Progress"] = progress.ToString(CultureInfo.InvariantCulture);

            try
            {
                _sessionManager.SendMessageToAdminSessions(SessionMessageType.RefreshProgress, dict, CancellationToken.None);
            }
            catch
            {
            }

            var collectionFolders = _libraryManager.GetCollectionFolders(item).ToList();

            foreach (var collectionFolder in collectionFolders)
            {
                var collectionFolderDict = new Dictionary<string, string>
                {
                    ["ItemId"] = collectionFolder.Id.ToString("N", CultureInfo.InvariantCulture),
                    ["Progress"] = (collectionFolder.GetRefreshProgress() ?? 0).ToString(CultureInfo.InvariantCulture)
                };

                try
                {
                    _sessionManager.SendMessageToAdminSessions(SessionMessageType.RefreshProgress, collectionFolderDict, CancellationToken.None);
                }
                catch
                {
                }
            }
        }

        private void OnProviderRefreshStarted(object sender, GenericEventArgs<BaseItem> e)
        {
            OnProviderRefreshProgress(sender, new GenericEventArgs<Tuple<BaseItem, double>>(new Tuple<BaseItem, double>(e.Argument, 0)));
        }

        private void OnProviderRefreshCompleted(object sender, GenericEventArgs<BaseItem> e)
        {
            OnProviderRefreshProgress(sender, new GenericEventArgs<Tuple<BaseItem, double>>(new Tuple<BaseItem, double>(e.Argument, 100)));

            _lastProgressMessageTimes.TryRemove(e.Argument.Id, out _);
        }

        private static bool EnableRefreshMessage(BaseItem item)
        {
            if (item is not Folder folder)
            {
                return false;
            }

            if (folder.IsRoot)
            {
                return false;
            }

            if (folder is AggregateFolder || folder is UserRootFolder)
            {
                return false;
            }

            if (folder is UserView || folder is Channel)
            {
                return false;
            }

            if (!folder.IsTopParent)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Handles the ItemAdded event of the libraryManager control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="ItemChangeEventArgs"/> instance containing the event data.</param>
        private void OnLibraryItemAdded(object sender, ItemChangeEventArgs e)
        {
            if (!FilterItem(e.Item))
            {
                return;
            }

            lock (_libraryChangedSyncLock)
            {
                if (LibraryUpdateTimer == null)
                {
                    LibraryUpdateTimer = new Timer(
                        LibraryUpdateTimerCallback,
                        null,
                        LibraryUpdateDuration,
                        Timeout.Infinite);
                }
                else
                {
                    LibraryUpdateTimer.Change(LibraryUpdateDuration, Timeout.Infinite);
                }

                if (e.Item.GetParent() is Folder parent)
                {
                    _foldersAddedTo.Add(parent);
                }

                _itemsAdded.Add(e.Item);
            }
        }

        /// <summary>
        /// Handles the ItemUpdated event of the libraryManager control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="ItemChangeEventArgs"/> instance containing the event data.</param>
        private void OnLibraryItemUpdated(object sender, ItemChangeEventArgs e)
        {
            if (!FilterItem(e.Item))
            {
                return;
            }

            lock (_libraryChangedSyncLock)
            {
                if (LibraryUpdateTimer == null)
                {
                    LibraryUpdateTimer = new Timer(LibraryUpdateTimerCallback, null, LibraryUpdateDuration, Timeout.Infinite);
                }
                else
                {
                    LibraryUpdateTimer.Change(LibraryUpdateDuration, Timeout.Infinite);
                }

                _itemsUpdated.Add(e.Item);
            }
        }

        /// <summary>
        /// Handles the ItemRemoved event of the libraryManager control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="ItemChangeEventArgs"/> instance containing the event data.</param>
        private void OnLibraryItemRemoved(object sender, ItemChangeEventArgs e)
        {
            if (!FilterItem(e.Item))
            {
                return;
            }

            lock (_libraryChangedSyncLock)
            {
                if (LibraryUpdateTimer == null)
                {
                    LibraryUpdateTimer = new Timer(LibraryUpdateTimerCallback, null, LibraryUpdateDuration, Timeout.Infinite);
                }
                else
                {
                    LibraryUpdateTimer.Change(LibraryUpdateDuration, Timeout.Infinite);
                }

                if (e.Parent is Folder parent)
                {
                    _foldersRemovedFrom.Add(parent);
                }

                _itemsRemoved.Add(e.Item);
            }
        }

        /// <summary>
        /// Libraries the update timer callback.
        /// </summary>
        /// <param name="state">The state.</param>
        private void LibraryUpdateTimerCallback(object state)
        {
            lock (_libraryChangedSyncLock)
            {
                // Remove dupes in case some were saved multiple times
                var foldersAddedTo = _foldersAddedTo
                                        .GroupBy(x => x.Id)
                                        .Select(x => x.First())
                                        .ToList();

                var foldersRemovedFrom = _foldersRemovedFrom
                                            .GroupBy(x => x.Id)
                                            .Select(x => x.First())
                                            .ToList();

                var itemsUpdated = _itemsUpdated
                                    .Where(i => !_itemsAdded.Contains(i))
                                    .GroupBy(x => x.Id)
                                    .Select(x => x.First())
                                    .ToList();

                SendChangeNotifications(_itemsAdded.ToList(), itemsUpdated, _itemsRemoved.ToList(), foldersAddedTo, foldersRemovedFrom, CancellationToken.None).GetAwaiter().GetResult();

                if (LibraryUpdateTimer != null)
                {
                    LibraryUpdateTimer.Dispose();
                    LibraryUpdateTimer = null;
                }

                _itemsAdded.Clear();
                _itemsRemoved.Clear();
                _itemsUpdated.Clear();
                _foldersAddedTo.Clear();
                _foldersRemovedFrom.Clear();
            }
        }

        /// <summary>
        /// Sends the change notifications.
        /// </summary>
        /// <param name="itemsAdded">The items added.</param>
        /// <param name="itemsUpdated">The items updated.</param>
        /// <param name="itemsRemoved">The items removed.</param>
        /// <param name="foldersAddedTo">The folders added to.</param>
        /// <param name="foldersRemovedFrom">The folders removed from.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task SendChangeNotifications(List<BaseItem> itemsAdded, List<BaseItem> itemsUpdated, List<BaseItem> itemsRemoved, List<Folder> foldersAddedTo, List<Folder> foldersRemovedFrom, CancellationToken cancellationToken)
        {
            var userIds = _sessionManager.Sessions
                .Select(i => i.UserId)
                .Where(i => !i.Equals(default))
                .Distinct()
                .ToArray();

            foreach (var userId in userIds)
            {
                LibraryUpdateInfo info;

                try
                {
                    info = GetLibraryUpdateInfo(itemsAdded, itemsUpdated, itemsRemoved, foldersAddedTo, foldersRemovedFrom, userId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in GetLibraryUpdateInfo");
                    return;
                }

                if (info.IsEmpty)
                {
                    continue;
                }

                try
                {
                    await _sessionManager.SendMessageToUserSessions(new List<Guid> { userId }, SessionMessageType.LibraryChanged, info, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error sending LibraryChanged message");
                }
            }
        }

        /// <summary>
        /// Gets the library update info.
        /// </summary>
        /// <param name="itemsAdded">The items added.</param>
        /// <param name="itemsUpdated">The items updated.</param>
        /// <param name="itemsRemoved">The items removed.</param>
        /// <param name="foldersAddedTo">The folders added to.</param>
        /// <param name="foldersRemovedFrom">The folders removed from.</param>
        /// <param name="userId">The user id.</param>
        /// <returns>LibraryUpdateInfo.</returns>
        private LibraryUpdateInfo GetLibraryUpdateInfo(List<BaseItem> itemsAdded, List<BaseItem> itemsUpdated, List<BaseItem> itemsRemoved, List<Folder> foldersAddedTo, List<Folder> foldersRemovedFrom, Guid userId)
        {
            var user = _userManager.GetUserById(userId);

            var newAndRemoved = new List<BaseItem>();
            newAndRemoved.AddRange(foldersAddedTo);
            newAndRemoved.AddRange(foldersRemovedFrom);

            var allUserRootChildren = _libraryManager.GetUserRootFolder().GetChildren(user, true).OfType<Folder>().ToList();

            return new LibraryUpdateInfo
            {
                ItemsAdded = itemsAdded.SelectMany(i => TranslatePhysicalItemToUserLibrary(i, user)).Select(i => i.Id.ToString("N", CultureInfo.InvariantCulture)).Distinct().ToArray(),

                ItemsUpdated = itemsUpdated.SelectMany(i => TranslatePhysicalItemToUserLibrary(i, user)).Select(i => i.Id.ToString("N", CultureInfo.InvariantCulture)).Distinct().ToArray(),

                ItemsRemoved = itemsRemoved.SelectMany(i => TranslatePhysicalItemToUserLibrary(i, user, true)).Select(i => i.Id.ToString("N", CultureInfo.InvariantCulture)).Distinct().ToArray(),

                FoldersAddedTo = foldersAddedTo.SelectMany(i => TranslatePhysicalItemToUserLibrary(i, user)).Select(i => i.Id.ToString("N", CultureInfo.InvariantCulture)).Distinct().ToArray(),

                FoldersRemovedFrom = foldersRemovedFrom.SelectMany(i => TranslatePhysicalItemToUserLibrary(i, user)).Select(i => i.Id.ToString("N", CultureInfo.InvariantCulture)).Distinct().ToArray(),

                CollectionFolders = GetTopParentIds(newAndRemoved, allUserRootChildren).ToArray()
            };
        }

        private static bool FilterItem(BaseItem item)
        {
            if (!item.IsFolder && !item.HasPathProtocol)
            {
                return false;
            }

            if (item is IItemByName && item is not MusicArtist)
            {
                return false;
            }

            return item.SourceType == SourceType.Library;
        }

        private IEnumerable<string> GetTopParentIds(List<BaseItem> items, List<Folder> allUserRootChildren)
        {
            var list = new List<string>();

            foreach (var item in items)
            {
                // If the physical root changed, return the user root
                if (item is AggregateFolder)
                {
                    continue;
                }

                foreach (var folder in allUserRootChildren)
                {
                    list.Add(folder.Id.ToString("N", CultureInfo.InvariantCulture));
                }
            }

            return list.Distinct(StringComparer.Ordinal);
        }

        /// <summary>
        /// Translates the physical item to user library.
        /// </summary>
        /// <typeparam name="T">The type of item.</typeparam>
        /// <param name="item">The item.</param>
        /// <param name="user">The user.</param>
        /// <param name="includeIfNotFound">if set to <c>true</c> [include if not found].</param>
        /// <returns>IEnumerable{``0}.</returns>
        private IEnumerable<T> TranslatePhysicalItemToUserLibrary<T>(T item, User user, bool includeIfNotFound = false)
            where T : BaseItem
        {
            // If the physical root changed, return the user root
            if (item is AggregateFolder)
            {
                return new[] { _libraryManager.GetUserRootFolder() as T };
            }

            // Return it only if it's in the user's library
            if (includeIfNotFound || item.IsVisibleStandalone(user))
            {
                return new[] { item };
            }

            return Array.Empty<T>();
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
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
                if (LibraryUpdateTimer != null)
                {
                    LibraryUpdateTimer.Dispose();
                    LibraryUpdateTimer = null;
                }

                _libraryManager.ItemAdded -= OnLibraryItemAdded;
                _libraryManager.ItemUpdated -= OnLibraryItemUpdated;
                _libraryManager.ItemRemoved -= OnLibraryItemRemoved;

                _providerManager.RefreshCompleted -= OnProviderRefreshCompleted;
                _providerManager.RefreshStarted -= OnProviderRefreshStarted;
                _providerManager.RefreshProgress -= OnProviderRefreshProgress;
            }
        }
    }
}
