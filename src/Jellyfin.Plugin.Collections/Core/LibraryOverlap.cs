using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.Collections.Core;

/// <summary>
/// Counts how many remote parts are already in a local catalog.
/// </summary>
public static class LibraryOverlap
{
    /// <summary>
    /// Drops parts that the current media-type filter excludes. A part with no kind is treated as a movie.
    /// </summary>
    /// <param name="parts">The remote parts.</param>
    /// <param name="mediaTypes">Movies, shows, or both.</param>
    /// <returns>The parts to show and match.</returns>
    public static IReadOnlyList<RemotePart> Filter(IReadOnlyList<RemotePart> parts, MediaTypes mediaTypes)
    {
        return mediaTypes switch
        {
            MediaTypes.Movies => [.. parts.Where(part => part.Kind != ItemKind.Series)],
            MediaTypes.Shows => [.. parts.Where(part => part.Kind == ItemKind.Series)],
            _ => parts
        };
    }

    /// <summary>
    /// Counts parts that are in the catalog. A TMDB id match wins. Otherwise the strict title must match,
    /// and the year must match when both sides have one. A library item with a different TMDB id is not a match.
    /// </summary>
    /// <param name="catalog">The library.</param>
    /// <param name="parts">The remote parts, already filtered.</param>
    /// <returns>The overlap.</returns>
    public static Overlap Measure(IReadOnlyList<CatalogEntry> catalog, IReadOnlyList<RemotePart> parts)
    {
        var unique = Dedupe(parts);
        if (unique.Count == 0)
        {
            return new Overlap(0, 0);
        }

        var byTmdb = new HashSet<(int Id, ItemKind Kind)>();
        var byTitle = new Dictionary<string, List<CatalogEntry>>(StringComparer.Ordinal);
        foreach (var entry in catalog)
        {
            if (entry.TmdbId is > 0)
            {
                byTmdb.Add((entry.TmdbId.Value, entry.Kind));
            }

            AddTitle(byTitle, entry.Name, entry);
            AddTitle(byTitle, entry.OriginalTitle, entry);
        }

        var owned = 0;
        foreach (var part in unique)
        {
            if (part.TmdbId is > 0 && byTmdb.Contains((part.TmdbId.Value, part.Kind)))
            {
                owned++;
                continue;
            }

            if (TitleHit(byTitle, part))
            {
                owned++;
            }
        }

        return new Overlap(owned, unique.Count);
    }

    private static List<RemotePart> Dedupe(IReadOnlyList<RemotePart> parts)
    {
        var seenIds = new HashSet<(int Id, ItemKind Kind)>();
        var seenTitles = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<RemotePart>(parts.Count);
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part.Title) && part.TmdbId is not > 0)
            {
                continue;
            }

            if (part.TmdbId is > 0)
            {
                if (!seenIds.Add((part.TmdbId.Value, part.Kind)))
                {
                    continue;
                }
            }
            else if (!seenTitles.Add(TitleKey(part)))
            {
                continue;
            }

            list.Add(part);
        }

        return list;
    }

    private static string TitleKey(RemotePart part)
    {
        return TitleNormalizer.Strict(part.Title) + "|" + (part.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static void AddTitle(Dictionary<string, List<CatalogEntry>> index, string? title, CatalogEntry entry)
    {
        var key = TitleNormalizer.Strict(title);
        if (key.Length == 0)
        {
            return;
        }

        if (!index.TryGetValue(key, out var list))
        {
            list = [];
            index[key] = list;
        }

        if (!list.Contains(entry))
        {
            list.Add(entry);
        }
    }

    private static bool TitleHit(Dictionary<string, List<CatalogEntry>> byTitle, RemotePart part)
    {
        if (!byTitle.TryGetValue(TitleNormalizer.Strict(part.Title), out var entries))
        {
            return false;
        }

        foreach (var entry in entries)
        {
            if (entry.Kind != part.Kind)
            {
                continue;
            }

            if (part.TmdbId is > 0 && entry.TmdbId is > 0 && entry.TmdbId != part.TmdbId)
            {
                continue;
            }

            if (part.Year is null || entry.Year is null || part.Year == entry.Year)
            {
                return true;
            }
        }

        return false;
    }
}
