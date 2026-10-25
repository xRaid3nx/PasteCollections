using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Collections.Services;

/// <summary>
/// Wraps <see cref="ICollectionManager"/>. Only uses the official collection APIs; never touches linked-children storage directly.
/// </summary>
public sealed class CollectionService : ICollectionService
{
    // Jellyfin persists new collections and their members shortly after the API call returns, so a read
    // straight after a write can miss them. Recent writes are merged into reads until the library catches up.
    private static readonly TimeSpan _recentWriteLifetime = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan _scanStartDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan _scanPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _scanWaitLimit = TimeSpan.FromSeconds(60);

    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<CollectionService> _logger;
    private readonly Dictionary<Guid, RecentWrite> _recentWrites = [];
    private readonly Lock _recentLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="CollectionService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="collectionManager">The collection manager.</param>
    /// <param name="taskManager">The scheduled task manager.</param>
    /// <param name="logger">The logger.</param>
    public CollectionService(ILibraryManager libraryManager, ICollectionManager collectionManager, ITaskManager taskManager, ILogger<CollectionService> logger)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _taskManager = taskManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<CollectionInfo> GetCollections(CancellationToken cancellationToken)
    {
        // A BoxSet query loads each collection's linked children (12.x stores them as rows).
        var boxSets = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.BoxSet],
            Recursive = true,
            DtoOptions = DtoOptions.StoredColumnsOnly
        });

        var collections = new List<CollectionInfo>(boxSets.Count);
        lock (_recentLock)
        {
            var now = DateTime.UtcNow;
            foreach (var expired in _recentWrites.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToList())
            {
                _recentWrites.Remove(expired);
            }

            foreach (var boxSet in boxSets.OfType<BoxSet>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var members = GetMemberIds(boxSet);
                if (_recentWrites.TryGetValue(boxSet.Id, out var recent))
                {
                    if (recent.ItemIds.IsSubsetOf(members))
                    {
                        _recentWrites.Remove(boxSet.Id);
                    }
                    else
                    {
                        members.UnionWith(recent.ItemIds);
                    }
                }

                collections.Add(new CollectionInfo(boxSet.Id, boxSet.Name ?? string.Empty, members));
            }

            foreach (var (id, recent) in _recentWrites.ToList())
            {
                if (collections.Exists(c => c.Id.Equals(id)))
                {
                    continue;
                }

                if (_libraryManager.GetItemById(id) is BoxSet)
                {
                    collections.Add(new CollectionInfo(id, recent.Name, new HashSet<Guid>(recent.ItemIds)));
                }
                else
                {
                    _recentWrites.Remove(id);
                }
            }
        }

        return collections;
    }

    /// <inheritdoc />
    public async Task<Guid> CreateAsync(string name, IReadOnlyCollection<Guid> itemIds, bool lockMetadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        cancellationToken.ThrowIfCancellationRequested();

        var collection = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
        {
            Name = name,
            IsLocked = lockMetadata,
            ItemIdList = [.. itemIds.Select(id => id.ToString("N", CultureInfo.InvariantCulture))]
        }).ConfigureAwait(false);

        RememberWrite(collection.Id, name, itemIds);
        _logger.LogInformation("Paste Collections: created collection {Name} ({Id}) with {Count} items", name, collection.Id, itemIds.Count);
        return collection.Id;
    }

    /// <inheritdoc />
    public async Task AddAsync(Guid collectionId, IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (itemIds.Count == 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _collectionManager.AddToCollectionAsync(collectionId, itemIds).ConfigureAwait(false);
        RememberWrite(collectionId, null, itemIds);
        _logger.LogInformation("Paste Collections: added {Count} items to collection {Id}", itemIds.Count, collectionId);
    }

    /// <inheritdoc />
    public async Task<int> EnsureMembersAsync(Guid collectionId, IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        await WaitForLibraryScanAsync(cancellationToken).ConfigureAwait(false);

        var stored = _libraryManager.GetItemList(new InternalItemsQuery
        {
            ItemIds = [collectionId],
            IncludeItemTypes = [BaseItemKind.BoxSet],
            DtoOptions = DtoOptions.StoredColumnsOnly
        }).OfType<BoxSet>().FirstOrDefault();
        if (stored is null)
        {
            return 0;
        }

        var members = GetMemberIds(stored);
        var missing = itemIds.Where(id => !members.Contains(id)).ToList();
        if (missing.Count == 0)
        {
            return 0;
        }

        _logger.LogWarning("Paste Collections: the library scan dropped {Count} items from new collection {Id}; adding them again", missing.Count, collectionId);
        await _collectionManager.AddToCollectionAsync(collectionId, missing).ConfigureAwait(false);
        return missing.Count;
    }

    private async Task WaitForLibraryScanAsync(CancellationToken cancellationToken)
    {
        var scan = _taskManager.ScheduledTasks.FirstOrDefault(t => string.Equals(t.ScheduledTask.Key, "RefreshLibrary", StringComparison.Ordinal));
        if (scan is null)
        {
            return;
        }

        // The scan starts shortly after the Collections folder is created, so give it a moment to begin.
        await Task.Delay(_scanStartDelay, cancellationToken).ConfigureAwait(false);
        var deadline = DateTime.UtcNow + _scanWaitLimit;
        while (scan.State != TaskState.Idle && DateTime.UtcNow < deadline)
        {
            await Task.Delay(_scanPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private void RememberWrite(Guid collectionId, string? name, IReadOnlyCollection<Guid> itemIds)
    {
        lock (_recentLock)
        {
            if (_recentWrites.TryGetValue(collectionId, out var recent))
            {
                recent.ItemIds.UnionWith(itemIds);
                _recentWrites[collectionId] = recent with { Expires = DateTime.UtcNow + _recentWriteLifetime };
            }
            else
            {
                var collectionName = name ?? _libraryManager.GetItemById(collectionId)?.Name ?? string.Empty;
                _recentWrites[collectionId] = new RecentWrite(collectionName, [.. itemIds], DateTime.UtcNow + _recentWriteLifetime);
            }
        }
    }

    private static HashSet<Guid> GetMemberIds(BoxSet boxSet)
    {
        var ids = new HashSet<Guid>();
        var needsResolve = false;
        foreach (var link in boxSet.LinkedChildren)
        {
            if (link.ItemId is { } id && !id.Equals(Guid.Empty))
            {
                ids.Add(id);
            }
            else
            {
                needsResolve = true;
            }
        }

        if (needsResolve || !boxSet.LinkedChildrenLoaded)
        {
            // Legacy path-based links (or an unloaded instance): resolve through the supported API.
            foreach (var child in boxSet.GetLinkedChildren(DtoOptions.StoredColumnsOnly))
            {
                ids.Add(child.Id);
            }
        }

        return ids;
    }

    private sealed record RecentWrite(string Name, HashSet<Guid> ItemIds, DateTime Expires);
}
