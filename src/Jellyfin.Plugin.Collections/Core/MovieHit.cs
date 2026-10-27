namespace Jellyfin.Plugin.Collections.Core;

/// <summary>
/// One movie from a TMDB search or find response.
/// </summary>
/// <param name="Id">The TMDB movie id.</param>
/// <param name="Title">The title.</param>
/// <param name="OriginalTitle">The original title, if any.</param>
/// <param name="Year">The release year, if known.</param>
public sealed record MovieHit(int Id, string Title, string? OriginalTitle, int? Year);
