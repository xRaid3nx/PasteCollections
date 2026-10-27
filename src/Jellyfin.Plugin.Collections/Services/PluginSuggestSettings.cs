using System;
using MediaBrowser.Controller;

namespace Jellyfin.Plugin.Collections.Services;

/// <summary>
/// Reads suggest settings from the plugin configuration, and falls back to Jellyfin's TMDB key.
/// </summary>
public sealed class PluginSuggestSettings : ISuggestSettings
{
    private readonly IServerApplicationHost _host;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginSuggestSettings"/> class.
    /// </summary>
    /// <param name="host">The server host, used only to find another plugin's TMDB key.</param>
    public PluginSuggestSettings(IServerApplicationHost host)
    {
        _host = host;
    }

    /// <inheritdoc />
    public string TmdbApiKey
    {
        get
        {
            var configured = Plugin.Instance?.Configuration.TmdbApiKey?.Trim();
            if (!string.IsNullOrEmpty(configured))
            {
                return configured;
            }

            return JellyfinTmdbKey.TryRead(_host) ?? string.Empty;
        }
    }

    /// <inheritdoc />
    public string TraktClientId => Plugin.Instance?.Configuration.TraktClientId?.Trim() ?? string.Empty;

    /// <inheritdoc />
    public string CuratedPacksUrl => Plugin.Instance?.Configuration.CuratedPacksUrl?.Trim() ?? string.Empty;

    /// <inheritdoc />
    public int CacheHours => Clamp(Plugin.Instance?.Configuration.SuggestCacheHours ?? 24, 0, 720);

    /// <inheritdoc />
    public int MinOwned => Clamp(Plugin.Instance?.Configuration.SuggestMinOwned ?? 1, 0, 5000);

    private static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);
}
