using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.Collections.Core;

/// <summary>
/// Reads a curated pack file. The file is downloaded by the server and matched locally.
/// </summary>
public static class CuratedPacks
{
    /// <summary>
    /// Reads a pack document.
    /// </summary>
    /// <param name="json">The file body.</param>
    /// <returns>One list per pack. Empty when the document isn't usable.</returns>
    public static IReadOnlyList<RemoteList> Read(string json)
    {
        var lists = new List<RemoteList>();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("packs", out var packs) || packs.ValueKind != JsonValueKind.Array)
        {
            return lists;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in packs.EnumerateArray())
        {
            var id = Text(pack, "id");
            var name = Text(pack, "name");
            if (!IsPackId(id) || name.Length == 0 || !seen.Add(id))
            {
                continue;
            }

            var parts = new List<RemotePart>();
            if (pack.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var title = Text(item, "title");
                    if (title.Length == 0)
                    {
                        continue;
                    }

                    var kind = MediaTypeText.Parse(Text(item, "mediaType")) ?? ItemKind.Movie;
                    parts.Add(new RemotePart(title, Year(item), TmdbId(item), kind));
                }
            }

            lists.Add(new RemoteList("pack", id, name, parts));
        }

        return lists;
    }

    /// <summary>
    /// Whether a pack id can be requested from the page.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>True for a short id made of letters, numbers, dashes and underscores.</returns>
    public static bool IsPackId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 80)
        {
            return false;
        }

        foreach (var character in id)
        {
            if (character is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static int? TmdbId(JsonElement item)
    {
        if (!item.TryGetProperty("tmdbId", out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var id) && id > 0)
        {
            return id;
        }

        if (property.ValueKind == JsonValueKind.String
            && int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id)
            && id > 0)
        {
            return id;
        }

        return null;
    }

    private static int? Year(JsonElement item)
    {
        if (!item.TryGetProperty("year", out var property))
        {
            return null;
        }

        var year = property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        };

        return year is >= ListParser.MinYear and <= 2100 ? year : null;
    }

    private static string Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return property.GetString()?.Trim() ?? string.Empty;
    }
}
