using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Collections.Core;

namespace Jellyfin.Plugin.Collections.Services;

/// <summary>
/// Gives a collection a poster.
/// </summary>
public interface ICollectionArtwork
{
    /// <summary>
    /// Whether the collection exists and has no poster.
    /// </summary>
    /// <param name="collectionId">The collection.</param>
    /// <returns><c>true</c> if it has no primary image.</returns>
    bool NeedsPoster(Guid collectionId);

    /// <summary>
    /// Identifies the collection on TMDB and, if allowed, falls back to a collage of its members' posters.
    /// Locked collections skip TMDB, because identifying would rewrite their metadata.
    /// </summary>
    /// <param name="collectionId">The collection.</param>
    /// <param name="name">The collection name.</param>
    /// <param name="memberIds">The members, in list order (the collage uses the first ones). Empty uses the collection's own order.</param>
    /// <param name="mode">The poster mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened.</returns>
    Task<PosterResult> AddPosterAsync(Guid collectionId, string name, IReadOnlyList<Guid> memberIds, PosterMode mode, CancellationToken cancellationToken);

    /// <summary>
    /// Locks the collection's metadata.
    /// </summary>
    /// <param name="collectionId">The collection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task LockAsync(Guid collectionId, CancellationToken cancellationToken);
}
