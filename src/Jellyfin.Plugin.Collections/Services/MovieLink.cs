namespace Jellyfin.Plugin.Collections.Services;

/// <summary>
/// A remembered movie-to-collection link. A null collection id means TMDB said the movie is not in one.
/// </summary>
/// <param name="CollectionId">The TMDB collection id, or null.</param>
/// <param name="CollectionName">The collection name, when there is one.</param>
public readonly record struct MovieLink(int? CollectionId, string? CollectionName);
