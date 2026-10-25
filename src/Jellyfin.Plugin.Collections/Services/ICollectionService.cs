using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Collections.Services;

/// <summary>
/// Reads and writes collections through Jellyfin's collection manager.
/// </summary>
public interface ICollectionService
{
    /// <summary>
    /// Gets every collection on the server.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All collections.</returns>
    IReadOnlyList<CollectionInfo> GetCollections(CancellationToken cancellationToken);

    /// <summary>
    /// Creates a collection containing the given items.
    /// </summary>
    /// <param name="name">The collection name.</param>
    /// <param name="itemIds">The items to add.</param>
    /// <param name="lockMetadata">Whether to lock the new collection's metadata.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new collection's id.</returns>
    Task<Guid> CreateAsync(string name, IReadOnlyCollection<Guid> itemIds, bool lockMetadata, CancellationToken cancellationToken);

    /// <summary>
    /// Adds items to an existing collection. Never removes anything.
    /// </summary>
    /// <param name="collectionId">The collection id.</param>
    /// <param name="itemIds">The items to add.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task AddAsync(Guid collectionId, IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);

    /// <summary>
    /// Waits for the library scan that the first collection on a server triggers, then re-adds any items the scan lost.
    /// </summary>
    /// <param name="collectionId">The new collection.</param>
    /// <param name="itemIds">The items it should contain.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of items that had to be re-added.</returns>
    Task<int> EnsureMembersAsync(Guid collectionId, IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);
}
