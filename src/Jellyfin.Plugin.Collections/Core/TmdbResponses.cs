using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.Collections.Core;

/// <summary>
/// Reads the TMDB JSON this plugin uses. Contains no HTTP.
/// </summary>
public static class TmdbResponses
{
    /// <summary>
    /// Reads collection search results.
    /// </summary>
    /// <param name="json">The response body.</param>
    /// <returns>Id and name pairs, in TMDB order. Empty when the body isn't a search response.</returns>
    public static IReadOnlyList<(int Id, string Name)> ReadSearch(string json)
    {
        var list = new List<(int Id, string Name)>();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var result in results.EnumerateArray())
        {
            if (!TryId(result, out var id))
            {
                continue;
            }

            var name = Text(result, "name");
            if (name.Length == 0)
            {
                continue;
            }

            list.Add((id, name));
        }

        return list;
    }

    /// <summary>
    /// Reads one collection, including its parts.
    /// </summary>
    /// <param name="json">The response body.</param>
    /// <param name="id">The id that was requested, used when the body omits it.</param>
    /// <returns>The list, or null when the body isn't a collection.</returns>
    public static RemoteList? ReadCollection(string json, int id)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var name = Text(root, "name");
        if (name.Length == 0)
        {
            return null;
        }

        var actualId = TryId(root, out var parsed) ? parsed : id;
        var parts = new List<RemotePart>();
        if (root.TryGetProperty("parts", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in array.EnumerateArray())
            {
                var title = Text(part, "title");
                if (title.Length == 0)
                {
                    title = Text(part, "name");
                }

                if (title.Length == 0 || !TryId(part, out var partId))
                {
                    continue;
                }

                var kind = string.Equals(Text(part, "media_type"), "tv", StringComparison.OrdinalIgnoreCase)
                    ? ItemKind.Series
                    : ItemKind.Movie;
                parts.Add(new RemotePart(title, Year(Text(part, "release_date")), partId, kind));
            }
        }

        return new RemoteList("tmdb", actualId.ToString(CultureInfo.InvariantCulture), name, parts);
    }

    /// <summary>
    /// Reads <c>belongs_to_collection</c> from a movie.
    /// </summary>
    /// <param name="json">The response body.</param>
    /// <returns>The membership, or null when the body isn't a movie.</returns>
    public static MovieMembership? ReadMovie(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!TryId(root, out _))
        {
            return null;
        }

        if (!root.TryGetProperty("belongs_to_collection", out var collection) || collection.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new MovieMembership(null, null);
        }

        if (!TryId(collection, out var collectionId))
        {
            return new MovieMembership(null, null);
        }

        var name = Text(collection, "name");
        return new MovieMembership(collectionId, name.Length == 0 ? null : name);
    }

    private static bool TryId(JsonElement element, out int id)
    {
        id = 0;
        if (!element.TryGetProperty("id", out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out id))
        {
            return id > 0;
        }

        return property.ValueKind == JsonValueKind.String
            && int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id)
            && id > 0;
    }

    private static string Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return property.GetString()?.Trim() ?? string.Empty;
    }

    private static int? Year(string date)
    {
        if (date.Length < 4 || !int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year))
        {
            return null;
        }

        return year is >= ListParser.MinYear and <= 2100 ? year : null;
    }
}
