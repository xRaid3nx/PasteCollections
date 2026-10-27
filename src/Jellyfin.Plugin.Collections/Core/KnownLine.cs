namespace Jellyfin.Plugin.Collections.Core;

/// <summary>
/// A TMDB id to attach to an already parsed list line.
/// </summary>
/// <param name="LineNumber">The 1-based line number from the pasted text.</param>
/// <param name="TmdbId">The TMDB id.</param>
/// <param name="Kind">Movie or series, when known.</param>
public readonly record struct KnownLine(int LineNumber, int TmdbId, ItemKind? Kind);
