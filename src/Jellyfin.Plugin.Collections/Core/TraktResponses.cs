using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Collections.Core;

/// <summary>
/// Reads Trakt list JSON and list URLs. Contains no HTTP.
/// </summary>
public static partial class TraktResponses
{
    /// <summary>
    /// Reads a Trakt user-list URL.
    /// </summary>
    /// <param name="url">A URL such as <c>https://trakt.tv/users/someone/lists/marvel</c>.</param>
    /// <param name="username">The username.</param>
    /// <param name="slug">The list slug.</param>
    /// <returns>True when the URL is a user list.</returns>
    public static bool TryParseListUrl(string? url, out string username, out string slug)
    {
        username = string.Empty;
        slug = string.Empty;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Host, "trakt.tv", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Host, "www.trakt.tv", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var match = ListUrlPattern().Match(uri.AbsolutePath);
        if (!match.Success || !IsSlug(match.Groups[1].Value) || !IsSlug(match.Groups[2].Value))
        {
            return false;
        }

        username = match.Groups[1].Value;
        slug = match.Groups[2].Value;
        return true;
    }

    /// <summary>
    /// Whether a Trakt username or list slug is safe to put in a URL path.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>True when it is a short slug.</returns>
    public static bool IsSlug(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.Length <= 80 && SlugPattern().IsMatch(value);
    }

    /// <summary>
    /// Reads <c>GET /users/{user}/lists</c>.
    /// </summary>
    /// <param name="json">The response body.</param>
    /// <param name="username">The username the lists belong to.</param>
    /// <returns>The lists.</returns>
    public static IReadOnlyList<TraktUserList> ReadLists(string json, string username)
    {
        var list = new List<TraktUserList>();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in document.RootElement.EnumerateArray())
        {
            var name = Text(item, "name");
            var slug = Ids(item, "slug");
            if (name.Length == 0 || !IsSlug(slug))
            {
                continue;
            }

            list.Add(new TraktUserList(username, slug, name));
        }

        return list;
    }

    /// <summary>
    /// Reads <c>GET /users/{user}/lists/{slug}/items</c>.
    /// </summary>
    /// <param name="json">The response body.</param>
    /// <param name="username">The username.</param>
    /// <param name="slug">The list slug.</param>
    /// <param name="name">The list name to show. The slug is used when this is blank.</param>
    /// <returns>The list.</returns>
    public static RemoteList ReadItems(string json, string username, string slug, string? name)
    {
        var parts = new List<RemotePart>();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var type = Text(item, "type");
                var nodeName = type switch
                {
                    "show" or "season" or "episode" => "show",
                    _ => "movie"
                };
                if (!item.TryGetProperty(nodeName, out var node) || node.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var title = Text(node, "title");
                if (title.Length == 0)
                {
                    continue;
                }

                var kind = nodeName == "show" ? ItemKind.Series : ItemKind.Movie;
                parts.Add(new RemotePart(title, Year(node), TmdbId(node), kind));
            }
        }

        var label = string.IsNullOrWhiteSpace(name) ? slug : name.Trim();
        return new RemoteList("trakt", username + "/" + slug, label, parts);
    }

    private static int? TmdbId(JsonElement node)
    {
        if (!node.TryGetProperty("ids", out var ids) || ids.ValueKind != JsonValueKind.Object || !ids.TryGetProperty("tmdb", out var tmdb))
        {
            return null;
        }

        if (tmdb.ValueKind == JsonValueKind.Number && tmdb.TryGetInt32(out var id) && id > 0)
        {
            return id;
        }

        if (tmdb.ValueKind == JsonValueKind.String
            && int.TryParse(tmdb.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id)
            && id > 0)
        {
            return id;
        }

        return null;
    }

    private static int? Year(JsonElement node)
    {
        if (!node.TryGetProperty("year", out var property))
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

    private static string Ids(JsonElement item, string name)
    {
        return item.TryGetProperty("ids", out var ids) ? Text(ids, name) : string.Empty;
    }

    private static string Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return property.GetString()?.Trim() ?? string.Empty;
    }

    [GeneratedRegex("/users/([^/]+)/lists/([^/]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ListUrlPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
}
