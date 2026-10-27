using System.Collections.Generic;

namespace Jellyfin.Plugin.Collections.Core;

/// <summary>
/// Attaches caller-supplied TMDB ids to parsed lines with the same line number.
/// </summary>
public static class KnownLines
{
    /// <summary>
    /// Returns the lines, copying a TMDB id onto each one that has a matching known id.
    /// </summary>
    /// <param name="lines">The parsed lines.</param>
    /// <param name="known">Ids from a remote list. Ignored when null or empty.</param>
    /// <returns>The lines, with ids applied where the line numbers match.</returns>
    public static IReadOnlyList<ParsedLine> Apply(IReadOnlyList<ParsedLine> lines, IReadOnlyList<KnownLine>? known)
    {
        if (known is null || known.Count == 0)
        {
            return lines;
        }

        var map = new Dictionary<int, KnownLine>();
        foreach (var item in known)
        {
            if (item.TmdbId > 0)
            {
                map[item.LineNumber] = item;
            }
        }

        if (map.Count == 0)
        {
            return lines;
        }

        var result = new List<ParsedLine>(lines.Count);
        foreach (var line in lines)
        {
            result.Add(map.TryGetValue(line.LineNumber, out var item)
                ? line with { TmdbId = item.TmdbId, RemoteKind = item.Kind }
                : line);
        }

        return result;
    }
}
