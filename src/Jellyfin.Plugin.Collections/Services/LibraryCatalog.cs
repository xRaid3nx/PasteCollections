using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Collections.Core;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Collections.Services;

/// <summary>
/// Reads movies and series from the Jellyfin library in one query.
/// </summary>
public sealed class LibraryCatalog : ILibraryCatalog
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryCatalog> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryCatalog"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public LibraryCatalog(ILibraryManager libraryManager, ILogger<LibraryCatalog> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<CatalogEntry> GetEntries(MediaTypes mediaTypes, CancellationToken cancellationToken)
    {
        var kinds = mediaTypes switch
        {
            MediaTypes.Movies => new[] { BaseItemKind.Movie },
            MediaTypes.Shows => new[] { BaseItemKind.Series },
            _ => new[] { BaseItemKind.Movie, BaseItemKind.Series }
        };

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = kinds,
            Recursive = true,
            IsVirtualItem = false,
            DtoOptions = DtoOptions.StoredColumnsOnly
        });

        cancellationToken.ThrowIfCancellationRequested();
        var entries = Map(items);
        _logger.LogInformation("Paste Collections: catalog has {Count} items ({MediaTypes})", entries.Count, mediaTypes);
        return entries;
    }

    /// <inheritdoc />
    public IReadOnlyList<CatalogEntry> GetByIds(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return [];
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            ItemIds = [.. ids],
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            IsVirtualItem = false,
            DtoOptions = DtoOptions.StoredColumnsOnly
        });

        cancellationToken.ThrowIfCancellationRequested();
        return Map(items);
    }

    private List<CatalogEntry> Map(IReadOnlyList<BaseItem> items)
    {
        var libraries = GetLibraryLocations();
        var entries = new List<CatalogEntry>(items.Count);
        foreach (var item in items)
        {
            if (item.IsVirtualItem || item.ExtraType is not null || !item.OwnerId.Equals(Guid.Empty))
            {
                continue;
            }

            if (item is Video { PrimaryVersionId: { } primary } && !primary.Equals(Guid.Empty))
            {
                continue;
            }

            var kind = item.GetBaseItemKind() switch
            {
                BaseItemKind.Movie => ItemKind.Movie,
                BaseItemKind.Series => ItemKind.Series,
                _ => (ItemKind?)null
            };

            if (kind is null || string.IsNullOrWhiteSpace(item.Name))
            {
                continue;
            }

            entries.Add(new CatalogEntry(
                item.Id,
                item.Name,
                NullIfEmpty(item.OriginalTitle),
                NullIfEmpty(item.ForcedSortName),
                item.ProductionYear,
                kind.Value,
                ResolveLibraryName(item.Path, libraries),
                NullIfEmpty(item.Path),
                ReadTmdbId(item),
                ReadImdbId(item)));
        }

        return entries;
    }

    private List<(string Location, string Name)> GetLibraryLocations()
    {
        var locations = new List<(string Location, string Name)>();
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            foreach (var location in folder.Locations ?? [])
            {
                if (!string.IsNullOrWhiteSpace(location))
                {
                    locations.Add((Path.TrimEndingDirectorySeparator(location), folder.Name));
                }
            }
        }

        // Longest first so nested library roots win.
        return [.. locations.OrderByDescending(l => l.Location.Length)];
    }

    private static string? ResolveLibraryName(string? path, List<(string Location, string Name)> libraries)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var (location, name) in libraries)
        {
            if (path.StartsWith(location, StringComparison.OrdinalIgnoreCase)
                && (path.Length == location.Length || path[location.Length] is '\\' or '/'))
            {
                return name;
            }
        }

        return Path.GetFileName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)));
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int? ReadTmdbId(BaseItem item)
    {
        if (!item.TryGetProviderId(MetadataProvider.Tmdb, out var raw)
            || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            || id <= 0)
        {
            return null;
        }

        return id;
    }

    private static string? ReadImdbId(BaseItem item)
    {
        if (!item.TryGetProviderId(MetadataProvider.Imdb, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var id = raw.Trim();
        return id.Length is > 2 and <= 20 && id.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? id : null;
    }
}
