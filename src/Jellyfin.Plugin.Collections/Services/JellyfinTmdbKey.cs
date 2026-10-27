using System;
using System.Collections;
using System.Reflection;

namespace Jellyfin.Plugin.Collections.Services;

/// <summary>
/// Reads a TMDB API key from another Jellyfin plugin, when the user has set one there.
/// </summary>
internal static class JellyfinTmdbKey
{
    /// <summary>
    /// Looks through the host's plugins for a TMDB plugin configuration that has an API key.
    /// </summary>
    /// <param name="host">The server host. Reflection is used so this plugin does not reference the TMDB plugin.</param>
    /// <returns>The key, or null when none is set.</returns>
    public static string? TryRead(object? host)
    {
        var plugins = host?.GetType().GetProperty("Plugins")?.GetValue(host) as IEnumerable;
        if (plugins is null)
        {
            return null;
        }

        foreach (var plugin in plugins)
        {
            if (plugin is null || !LooksLikeTmdb(plugin))
            {
                continue;
            }

            var key = ReadKey(plugin.GetType().GetProperty("Configuration")?.GetValue(plugin));
            if (!string.IsNullOrWhiteSpace(key) && key.Trim().Length >= 8)
            {
                return key.Trim();
            }
        }

        return null;
    }

    private static bool LooksLikeTmdb(object plugin)
    {
        var typeName = plugin.GetType().FullName ?? string.Empty;
        var name = plugin.GetType().GetProperty("Name")?.GetValue(plugin) as string ?? string.Empty;
        return ContainsTmdb(typeName) || ContainsTmdb(name);
    }

    private static bool ContainsTmdb(string value)
    {
        return value.Contains("Tmdb", StringComparison.OrdinalIgnoreCase)
            || value.Contains("TheMovieDb", StringComparison.OrdinalIgnoreCase)
            || value.Contains("The Movie Database", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadKey(object? config)
    {
        if (config is null)
        {
            return null;
        }

        string? fallback = null;
        foreach (var property in config.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.PropertyType != typeof(string) || property.GetIndexParameters().Length > 0 || !property.CanRead)
            {
                continue;
            }

            if (property.GetValue(config) is not string value || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (property.Name.Equals("ApiKey", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("TmdbApiKey", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            if (property.Name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase))
            {
                fallback = value;
            }
        }

        return fallback;
    }
}
