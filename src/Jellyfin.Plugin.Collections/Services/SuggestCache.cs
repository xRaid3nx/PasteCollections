using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Collections.Core;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Collections.Services;

/// <summary>
/// Remembers TMDB and pack answers on this server so a library scan does not repeat them.
/// The file stays on the Jellyfin host. It is not uploaded anywhere.
/// </summary>
public sealed class SuggestCache
{
    private const int MaxMovies = 20_000;
    private const int MaxLists = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;
    private readonly Func<int> _cacheHours;
    private readonly ILogger<SuggestCache> _logger;
    private readonly object _gate = new();
    private CacheDocument _data = new();
    private bool _loaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="SuggestCache"/> class.
    /// </summary>
    /// <param name="paths">Application paths. The cache file lives next to plugin configuration.</param>
    /// <param name="settings">Cache lifetime.</param>
    /// <param name="logger">The logger.</param>
    public SuggestCache(IApplicationPaths paths, ISuggestSettings settings, ILogger<SuggestCache> logger)
        : this(Path.Combine(paths.PluginConfigurationsPath, "paste-collections-suggest-cache.json"), () => settings.CacheHours, logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SuggestCache"/> class at an explicit path.
    /// </summary>
    /// <param name="path">The cache file.</param>
    /// <param name="cacheHours">How long an entry stays fresh. Zero means nothing is fresh.</param>
    /// <param name="logger">The logger.</param>
    internal SuggestCache(string path, Func<int> cacheHours, ILogger<SuggestCache> logger)
    {
        _path = path;
        _cacheHours = cacheHours;
        _logger = logger;
    }

    /// <summary>
    /// Reads a fresh movie link.
    /// </summary>
    /// <param name="tmdbId">The movie id.</param>
    /// <param name="link">The link, when fresh.</param>
    /// <returns>True when a fresh entry exists.</returns>
    public bool TryGetMovie(int tmdbId, out MovieLink link)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var row = _data.Movies.Find(movie => movie.TmdbId == tmdbId);
            if (row is null || !Fresh(row.FetchedUtc))
            {
                link = default;
                return false;
            }

            link = new MovieLink(row.CollectionId, row.CollectionName);
            return true;
        }
    }

    /// <summary>
    /// Stores a movie link, including "not in a collection".
    /// </summary>
    /// <param name="tmdbId">The movie id.</param>
    /// <param name="collectionId">The collection id, or null.</param>
    /// <param name="collectionName">The collection name, or null.</param>
    public void RememberMovie(int tmdbId, int? collectionId, string? collectionName)
    {
        lock (_gate)
        {
            EnsureLoaded();
            _data.Movies.RemoveAll(movie => movie.TmdbId == tmdbId);
            _data.Movies.Add(new CacheMovie
            {
                TmdbId = tmdbId,
                CollectionId = collectionId,
                CollectionName = collectionName,
                FetchedUtc = DateTime.UtcNow
            });
            Save();
        }
    }

    /// <summary>
    /// Reads a fresh remote list.
    /// </summary>
    /// <param name="source">The source id.</param>
    /// <param name="id">The list id.</param>
    /// <param name="list">The list, when fresh.</param>
    /// <returns>True when a fresh list exists.</returns>
    public bool TryGetList(string source, string id, out RemoteList list)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var row = FindList(source, id);
            if (row is null || !Fresh(row.FetchedUtc))
            {
                list = new RemoteList(source, id, string.Empty, []);
                return false;
            }

            list = ToList(row);
            return true;
        }
    }

    /// <summary>
    /// Stores one list, replacing an older copy of the same source and id.
    /// </summary>
    /// <param name="list">The list.</param>
    public void RememberList(RemoteList list)
    {
        lock (_gate)
        {
            EnsureLoaded();
            _data.Lists.RemoveAll(row => Same(row, list.Source, list.Id));
            _data.Lists.Add(ToRow(list));
            Save();
        }
    }

    /// <summary>
    /// Replaces every stored list from one source.
    /// </summary>
    /// <param name="source">The source id.</param>
    /// <param name="lists">The new lists.</param>
    public void ReplaceSource(string source, IReadOnlyList<RemoteList> lists)
    {
        lock (_gate)
        {
            EnsureLoaded();
            _data.Lists.RemoveAll(row => string.Equals(row.Source, source, StringComparison.OrdinalIgnoreCase));
            foreach (var list in lists)
            {
                _data.Lists.Add(ToRow(list));
            }

            Save();
        }
    }

    /// <summary>
    /// Reads every fresh list from a source.
    /// </summary>
    /// <param name="source">The source id.</param>
    /// <returns>The lists.</returns>
    public IReadOnlyList<RemoteList> GetLists(string source)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return
            [
                .. _data.Lists
                    .Where(row => string.Equals(row.Source, source, StringComparison.OrdinalIgnoreCase) && Fresh(row.FetchedUtc))
                    .Select(ToList)
            ];
        }
    }

    /// <summary>
    /// Whether a stamp (for example "this pack URL was downloaded") is still fresh.
    /// </summary>
    /// <param name="key">The stamp key.</param>
    /// <returns>True when it is fresh.</returns>
    public bool TryGetStamp(string key)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var row = _data.Stamps.Find(stamp => string.Equals(stamp.Key, key, StringComparison.Ordinal));
            return row is not null && Fresh(row.FetchedUtc);
        }
    }

    /// <summary>
    /// Stores a stamp.
    /// </summary>
    /// <param name="key">The stamp key.</param>
    public void RememberStamp(string key)
    {
        lock (_gate)
        {
            EnsureLoaded();
            _data.Stamps.RemoveAll(stamp => string.Equals(stamp.Key, key, StringComparison.Ordinal));
            _data.Stamps.Add(new CacheStamp { Key = key, FetchedUtc = DateTime.UtcNow });
            Save();
        }
    }

    private CacheList? FindList(string source, string id)
    {
        return _data.Lists.Find(row => Same(row, source, id));
    }

    private static bool Same(CacheList row, string source, string id)
    {
        return string.Equals(row.Source, source, StringComparison.OrdinalIgnoreCase)
            && string.Equals(row.Id, id, StringComparison.Ordinal);
    }

    private bool Fresh(DateTime fetchedUtc)
    {
        var hours = _cacheHours();
        if (hours <= 0)
        {
            return false;
        }

        var fetched = fetchedUtc.Kind == DateTimeKind.Utc ? fetchedUtc : DateTime.SpecifyKind(fetchedUtc, DateTimeKind.Utc);
        return DateTime.UtcNow - fetched < TimeSpan.FromHours(hours);
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var json = File.ReadAllText(_path);
            _data = JsonSerializer.Deserialize<CacheDocument>(json, JsonOptions) ?? new CacheDocument();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Paste Collections: suggest cache could not be read, so it will be rebuilt");
            _data = new CacheDocument();
        }
    }

    private void Save()
    {
        if (_data.Movies.Count > MaxMovies)
        {
            _data.Movies = [.. _data.Movies.OrderByDescending(movie => movie.FetchedUtc).Take(MaxMovies)];
        }

        if (_data.Lists.Count > MaxLists)
        {
            _data.Lists = [.. _data.Lists.OrderByDescending(list => list.FetchedUtc).Take(MaxLists)];
        }

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_data, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Paste Collections: suggest cache could not be saved");
        }
    }

    private static CacheList ToRow(RemoteList list)
    {
        return new CacheList
        {
            Source = list.Source,
            Id = list.Id,
            Name = list.Name,
            FetchedUtc = DateTime.UtcNow,
            Parts =
            [
                .. list.Parts.Select(part => new CachePart
                {
                    Title = part.Title,
                    Year = part.Year,
                    TmdbId = part.TmdbId,
                    Kind = part.Kind
                })
            ]
        };
    }

    private static RemoteList ToList(CacheList row)
    {
        return new RemoteList(
            row.Source,
            row.Id,
            row.Name,
            [
                .. row.Parts.Select(part => new RemotePart(
                    part.Title,
                    part.Year,
                    part.TmdbId,
                    part.Kind))
            ]);
    }

    private sealed class CacheDocument
    {
        public List<CacheMovie> Movies { get; set; } = [];

        public List<CacheList> Lists { get; set; } = [];

        public List<CacheStamp> Stamps { get; set; } = [];
    }

    private sealed class CacheMovie
    {
        public int TmdbId { get; set; }

        public int? CollectionId { get; set; }

        public string? CollectionName { get; set; }

        public DateTime FetchedUtc { get; set; }
    }

    private sealed class CacheList
    {
        public string Source { get; set; } = string.Empty;

        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public DateTime FetchedUtc { get; set; }

        public List<CachePart> Parts { get; set; } = [];
    }

    private sealed class CachePart
    {
        public string Title { get; set; } = string.Empty;

        public int? Year { get; set; }

        public int? TmdbId { get; set; }

        public ItemKind Kind { get; set; }
    }

    private sealed class CacheStamp
    {
        public string Key { get; set; } = string.Empty;

        public DateTime FetchedUtc { get; set; }
    }
}
