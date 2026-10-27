namespace Jellyfin.Plugin.Collections.Core;

/// <summary>
/// A movie's franchise, when TMDB has one.
/// </summary>
/// <param name="CollectionId">The collection id, or null when the movie is not in a collection.</param>
/// <param name="CollectionName">The collection name, when there is one.</param>
public readonly record struct MovieMembership(int? CollectionId, string? CollectionName);
