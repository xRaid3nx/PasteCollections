namespace Jellyfin.Plugin.Collections.Services;

/// <summary>
/// API keys and cache settings for suggested collections. Never logs the keys.
/// </summary>
public interface ISuggestSettings
{
    /// <summary>
    /// Gets the TMDB v3 API key. A key saved in this plugin wins; otherwise Jellyfin's TMDB plugin key is used.
    /// </summary>
    string TmdbApiKey { get; }

    /// <summary>
    /// Gets the Trakt client id (sent as <c>trakt-api-key</c>). No Trakt login is used.
    /// </summary>
    string TraktClientId { get; }

    /// <summary>
    /// Gets the URL of a curated pack JSON file, or empty when none is set.
    /// </summary>
    string CuratedPacksUrl { get; }

    /// <summary>
    /// Gets how many hours a TMDB or pack answer is remembered on this server. Zero refetches every time.
    /// </summary>
    int CacheHours { get; }

    /// <summary>
    /// Gets the minimum number of owned titles before "suggest from my library" shows a franchise.
    /// </summary>
    int MinOwned { get; }
}
