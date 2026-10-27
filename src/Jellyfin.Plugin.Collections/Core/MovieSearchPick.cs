using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Collections.Core;

/// <summary>
/// Picks a TMDB movie only when one search result clearly matches the library title.
/// </summary>
public static class MovieSearchPick
{
    /// <summary>
    /// Returns the TMDB id when exactly one hit matches. A year must match when the library item has one.
    /// </summary>
    /// <param name="name">The library title.</param>
    /// <param name="originalTitle">The library original title, if any.</param>
    /// <param name="year">The library year, if any.</param>
    /// <param name="hits">The TMDB hits.</param>
    /// <returns>The id, or null when there is no single clear match.</returns>
    public static int? Pick(string name, string? originalTitle, int? year, IReadOnlyList<MovieHit> hits)
    {
        var matched = hits.Where(hit => TitlesMatch(name, originalTitle, hit)).ToList();
        if (year is int wanted)
        {
            var exact = matched.Where(hit => hit.Year == wanted).Select(hit => hit.Id).Distinct().ToList();
            return exact.Count == 1 ? exact[0] : null;
        }

        var ids = matched.Select(hit => hit.Id).Distinct().ToList();
        return ids.Count == 1 ? ids[0] : null;
    }

    private static bool TitlesMatch(string name, string? originalTitle, MovieHit hit)
    {
        return Same(name, hit.Title)
            || Same(name, hit.OriginalTitle)
            || Same(originalTitle, hit.Title)
            || Same(originalTitle, hit.OriginalTitle);
    }

    private static bool Same(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var strictLeft = TitleNormalizer.Strict(left);
        var strictRight = TitleNormalizer.Strict(right);
        if (strictLeft.Length > 0 && strictLeft == strictRight)
        {
            return true;
        }

        var looseLeft = TitleNormalizer.Loose(left);
        var looseRight = TitleNormalizer.Loose(right);
        return looseLeft.Length > 0 && looseLeft == looseRight;
    }
}
