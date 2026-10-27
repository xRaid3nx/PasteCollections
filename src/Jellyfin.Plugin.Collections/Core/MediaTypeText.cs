namespace Jellyfin.Plugin.Collections.Core;

/// <summary>
/// Converts between Seerr/TMDB media type strings and <see cref="ItemKind"/>.
/// </summary>
public static class MediaTypeText
{
    /// <summary>
    /// Reads <c>movie</c> or <c>tv</c> (and a few aliases).
    /// </summary>
    /// <param name="value">The text.</param>
    /// <returns>The kind, or null when it isn't recognised.</returns>
    public static ItemKind? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "movie" or "movies" => ItemKind.Movie,
            "tv" or "show" or "shows" or "series" => ItemKind.Series,
            _ => null
        };
    }

    /// <summary>
    /// Writes the Seerr media type for a kind.
    /// </summary>
    /// <param name="kind">Movie or series.</param>
    /// <returns><c>movie</c> or <c>tv</c>.</returns>
    public static string Format(ItemKind kind) => kind == ItemKind.Series ? "tv" : "movie";
}
