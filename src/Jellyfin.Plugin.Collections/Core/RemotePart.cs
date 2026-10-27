namespace Jellyfin.Plugin.Collections.Core;

/// <summary>
/// One title from a remote collection or list.
/// </summary>
/// <param name="Title">The display title.</param>
/// <param name="Year">The release year, when known.</param>
/// <param name="TmdbId">The TMDB id, when the source has one.</param>
/// <param name="Kind">Movie or series. TMDB movie and TV ids are not interchangeable.</param>
public sealed record RemotePart(string Title, int? Year, int? TmdbId, ItemKind Kind);
