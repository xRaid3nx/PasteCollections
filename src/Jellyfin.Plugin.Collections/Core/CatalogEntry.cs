using System;

namespace Jellyfin.Plugin.Collections.Core;

/// <summary>
/// A lightweight, Jellyfin-independent view of one library item.
/// </summary>
/// <param name="Id">The item id.</param>
/// <param name="Name">The display name.</param>
/// <param name="OriginalTitle">The original title, if any.</param>
/// <param name="SortName">The sort name, if any.</param>
/// <param name="Year">The production year, if known.</param>
/// <param name="Kind">Movie or series.</param>
/// <param name="LibraryName">The library the item lives in, if known.</param>
/// <param name="Path">The file system path, if any.</param>
/// <param name="TmdbId">The TMDB id, when Jellyfin has one.</param>
public sealed record CatalogEntry(
    Guid Id,
    string Name,
    string? OriginalTitle,
    string? SortName,
    int? Year,
    ItemKind Kind,
    string? LibraryName,
    string? Path,
    int? TmdbId = null);
