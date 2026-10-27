using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Collections.Api.Dto;
using Jellyfin.Plugin.Collections.Core;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Collections.Services;

/// <summary>
/// Finds remote collections and scores them against the local library. Nothing is added until Preview and Apply.
/// Library data stays on this server. A stored TMDB id is sent when Jellyfin has one. Otherwise the title, year, or a stored IMDb id is sent.
/// </summary>
public sealed class SuggestService : IDisposable
{
    /// <summary>
    /// How many uncached movies one library scan asks about, unless the request says otherwise.
    /// </summary>
    public const int DefaultLookups = 40;

    /// <summary>
    /// The most uncached movies one call will ask about.
    /// </summary>
    public const int MaxLookupsPerCall = 80;

    /// <summary>
    /// How many collection detail pages one library scan will download.
    /// </summary>
    public const int CollectionsPerCall = 15;

    /// <summary>
    /// How many TMDB search hits are scored.
    /// </summary>
    public const int SearchLimit = 20;

    private const int MaxBodyChars = 1_000_000;

    private readonly ILibraryCatalog _catalog;
    private readonly ISuggestSettings _settings;
    private readonly SuggestCache _cache;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ILogger<SuggestService> _logger;
    private readonly TimeSpan _gap;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly SemaphoreSlim _pace = new(1, 1);
    private DateTime _nextRequest = DateTime.UtcNow;
    private FailureKind _lastFailure;
    private string? _lastError;

    /// <summary>
    /// Initializes a new instance of the <see cref="SuggestService"/> class.
    /// </summary>
    /// <param name="catalog">The library.</param>
    /// <param name="settings">API keys and cache lifetime.</param>
    /// <param name="cache">The on-server cache.</param>
    /// <param name="logger">The logger.</param>
    public SuggestService(ILibraryCatalog catalog, ISuggestSettings settings, SuggestCache cache, ILogger<SuggestService> logger)
        : this(catalog, settings, cache, logger, new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }), true, TimeSpan.FromMilliseconds(250))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SuggestService"/> class with a caller-supplied HTTP client.
    /// </summary>
    /// <param name="catalog">The library.</param>
    /// <param name="settings">API keys and cache lifetime.</param>
    /// <param name="cache">The on-server cache.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="http">The HTTP client.</param>
    /// <param name="ownsHttp">Whether this service disposes <paramref name="http"/>.</param>
    /// <param name="gap">The delay between remote calls. Tests pass <see cref="TimeSpan.Zero"/>.</param>
    internal SuggestService(
        ILibraryCatalog catalog,
        ISuggestSettings settings,
        SuggestCache cache,
        ILogger<SuggestService> logger,
        HttpClient http,
        bool ownsHttp,
        TimeSpan gap)
    {
        _catalog = catalog;
        _settings = settings;
        _cache = cache;
        _logger = logger;
        _http = http;
        _ownsHttp = ownsHttp;
        _gap = gap;
        _http.Timeout = TimeSpan.FromSeconds(20);
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("PasteCollections");
        }
    }

    private enum FailureKind
    {
        None,
        RateLimited,
        Failed
    }

    /// <summary>
    /// Searches TMDB collections by name and scores each one against the library.
    /// </summary>
    /// <param name="request">The query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The hits. A missing key is a message, not an error.</returns>
    public Task<SuggestHitsResponse> SearchAsync(SuggestSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = request.Query?.Trim() ?? string.Empty;
        if (query.Length < 2)
        {
            throw new BatchRequestException("Enter at least 2 characters.");
        }

        if (query.Length > 100)
        {
            throw new BatchRequestException("Search text is limited to 100 characters.");
        }

        var media = RequireMedia(request.MediaTypes);
        if (string.IsNullOrWhiteSpace(_settings.TmdbApiKey))
        {
            return Task.FromResult(Hits([], 0, MissingTmdbMessage()));
        }

        return WithGate(() => SearchCoreAsync(query, media, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Suggests TMDB franchises that already have at least one movie in the library.
    /// </summary>
    /// <param name="request">How many lookups to do, and the owned minimum.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ranked franchises, plus how many lookups are left.</returns>
    public Task<SuggestHitsResponse> FromLibraryAsync(SuggestLibraryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var media = RequireMedia(request.MediaTypes);
        if (string.IsNullOrWhiteSpace(_settings.TmdbApiKey))
        {
            return Task.FromResult(Hits([], 0, MissingTmdbMessage()));
        }

        var min = Math.Clamp(request.MinOwned ?? _settings.MinOwned, 0, 5000);
        var budget = Math.Clamp(request.MaxLookups ?? DefaultLookups, 1, MaxLookupsPerCall);
        return WithGate(() => FromLibraryCoreAsync(media, min, budget, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Loads one collection's titles.
    /// </summary>
    /// <param name="source">tmdb, trakt or pack.</param>
    /// <param name="id">The id within that source.</param>
    /// <param name="mediaTypes">Which kinds to keep.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The titles.</returns>
    public Task<SuggestListResponse> GetAsync(string? source, string? id, MediaTypes mediaTypes, CancellationToken cancellationToken)
    {
        var media = RequireMedia(mediaTypes);
        var key = (source ?? string.Empty).Trim().ToLowerInvariant();
        var listId = (id ?? string.Empty).Trim();
        if (listId.Length is 0 or > 200)
        {
            throw new BatchRequestException("That list id is not valid.");
        }

        return WithGate(() => GetCoreAsync(key, listId, media, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Lists a Trakt user's public lists. Does not log into Trakt.
    /// </summary>
    /// <param name="request">The username.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The lists, without their items yet.</returns>
    public Task<SuggestHitsResponse> TraktListsAsync(SuggestTraktRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var username = request.Username?.Trim() ?? string.Empty;
        if (!TraktResponses.IsSlug(username))
        {
            throw new BatchRequestException("Enter a Trakt username (letters, numbers, dashes).");
        }

        if (string.IsNullOrWhiteSpace(_settings.TraktClientId))
        {
            return Task.FromResult(Hits([], 0, MissingTraktMessage()));
        }

        return WithGate(() => TraktListsCoreAsync(username, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Loads the items of a Trakt list URL.
    /// </summary>
    /// <param name="request">The URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The titles.</returns>
    public Task<SuggestListResponse> TraktItemsAsync(SuggestTraktRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var media = RequireMedia(request.MediaTypes);
        if (!TraktResponses.TryParseListUrl(request.Url, out var username, out var slug))
        {
            throw new BatchRequestException("Enter a Trakt list URL, such as https://trakt.tv/users/someone/lists/marvel.");
        }

        if (string.IsNullOrWhiteSpace(_settings.TraktClientId))
        {
            return Task.FromResult(new SuggestListResponse { Message = MissingTraktMessage() });
        }

        return WithGate(
            async () =>
            {
                var list = await LoadTraktAsync(username, slug, null, cancellationToken).ConfigureAwait(false);
                return list is null
                    ? new SuggestListResponse { Source = "trakt", Message = _lastError ?? "That Trakt list could not be loaded." }
                    : ToList(list, media);
            },
            cancellationToken);
    }

    /// <summary>
    /// Downloads the configured pack file (or uses the cached copy) and lists the packs.
    /// </summary>
    /// <param name="mediaTypes">Which kinds count as owned.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The packs.</returns>
    public Task<SuggestHitsResponse> PacksAsync(MediaTypes mediaTypes, CancellationToken cancellationToken)
    {
        var media = RequireMedia(mediaTypes);
        if (string.IsNullOrWhiteSpace(_settings.CuratedPacksUrl))
        {
            return Task.FromResult(Hits([], 0, "Add a packs URL under Suggest collections. It should be an http or https link to a JSON file. The file is downloaded to this server and matched against your library here."));
        }

        if (!TryHttpUrl(_settings.CuratedPacksUrl, out var uri))
        {
            return Task.FromResult(Hits([], 0, "The packs URL must start with http:// or https://."));
        }

        return WithGate(() => PacksCoreAsync(uri, media, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }

        _busy.Dispose();
        _pace.Dispose();
    }

    private async Task<SuggestHitsResponse> SearchCoreAsync(string query, MediaTypes media, CancellationToken cancellationToken)
    {
        var relative = "search/collection?query=" + Uri.EscapeDataString(query) + "&include_adult=false&language=en-US";
        var body = await GetAsync(TmdbUri(relative), null, cancellationToken).ConfigureAwait(false);
        if (body.RateLimited)
        {
            return Hits([], 0, RateLimitMessage("TMDB"));
        }

        if (!body.Ok || body.Text is null)
        {
            return Hits([], 0, body.Error ?? "TMDB search failed.");
        }

        IReadOnlyList<(int Id, string Name)> found;
        try
        {
            found = TmdbResponses.ReadSearch(body.Text);
        }
        catch (JsonException)
        {
            return Hits([], 0, "TMDB returned something this plugin could not read.");
        }

        var catalog = _catalog.GetEntries(media, cancellationToken);
        var hits = new List<RemoteHit>();
        string? message = null;
        foreach (var (id, name) in found.Take(SearchLimit))
        {
            var list = await LoadTmdbCollectionAsync(id, cancellationToken).ConfigureAwait(false);
            if (list is null)
            {
                if (_lastFailure == FailureKind.RateLimited)
                {
                    message = RateLimitMessage("TMDB");
                    break;
                }

                message ??= _lastError;
                continue;
            }

            var (owned, total) = Score(catalog, list, media);
            hits.Add(new RemoteHit("tmdb", list.Id, TrimName(name), owned, total));
        }

        var ordered = hits
            .OrderByDescending(hit => hit.Owned)
            .ThenBy(hit => hit.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (message is null && ordered.Count == 0)
        {
            message = "No TMDB collections matched that search.";
        }

        return Hits(ordered, 0, message);
    }

    private async Task<SuggestHitsResponse> FromLibraryCoreAsync(MediaTypes media, int minOwned, int budget, CancellationToken cancellationToken)
    {
        if (media == MediaTypes.Shows)
        {
            return Hits([], 0, "TMDB collections are movies. Set Search in to Movies, or Movies and shows.");
        }

        var catalog = _catalog.GetEntries(MediaTypes.Movies, cancellationToken);
        var distinct = DistinctMovies(catalog);
        var pending = distinct.Where(NeedsLookup).ToList();
        var looked = 0;
        string? message = null;
        foreach (var entry in pending)
        {
            if (looked >= budget)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var tmdbId = await ResolveLibraryMovieAsync(entry, cancellationToken).ConfigureAwait(false);
            if (_lastFailure != FailureKind.None)
            {
                message = _lastFailure == FailureKind.RateLimited
                    ? RateLimitMessage("TMDB")
                    : _lastError ?? "TMDB could not be reached. What was found is saved on this server.";
                break;
            }

            if (tmdbId is not > 0)
            {
                looked++;
                continue;
            }

            if (!_cache.TryGetMovie(tmdbId.Value, out _))
            {
                var membership = await LoadMovieAsync(tmdbId.Value, cancellationToken).ConfigureAwait(false);
                if (membership is null)
                {
                    message = _lastFailure == FailureKind.RateLimited
                        ? RateLimitMessage("TMDB")
                        : _lastError ?? "TMDB could not be reached. What was found is saved on this server.";
                    break;
                }

                _cache.RememberMovie(tmdbId.Value, membership.Value.CollectionId, membership.Value.CollectionName);
            }

            looked++;
        }

        var groups = new Dictionary<int, string?>();
        var ownedByCollection = new Dictionary<int, int>();
        foreach (var id in ResolvedIds(distinct))
        {
            if (!_cache.TryGetMovie(id, out var link) || link.CollectionId is not int collectionId)
            {
                continue;
            }

            groups.TryAdd(collectionId, link.CollectionName);
            ownedByCollection[collectionId] = ownedByCollection.GetValueOrDefault(collectionId) + 1;
        }

        var floor = Math.Max(1, minOwned);
        var wanted = ownedByCollection.Where(pair => pair.Value >= floor).Select(pair => pair.Key).ToList();
        var uncached = wanted.Where(id => !_cache.TryGetList("tmdb", id.ToString(CultureInfo.InvariantCulture), out _)).ToList();
        var fetched = 0;
        foreach (var id in uncached)
        {
            if (fetched >= CollectionsPerCall || message is not null)
            {
                break;
            }

            var list = await LoadTmdbCollectionAsync(id, cancellationToken).ConfigureAwait(false);
            if (list is null)
            {
                message = _lastFailure == FailureKind.RateLimited
                    ? RateLimitMessage("TMDB")
                    : _lastError ?? "A TMDB collection could not be loaded. What was found is saved on this server.";
                break;
            }

            fetched++;
        }

        var overlapCatalog = media == MediaTypes.Movies ? catalog : _catalog.GetEntries(media, cancellationToken);
        var hits = new List<RemoteHit>();
        foreach (var id in wanted)
        {
            if (!_cache.TryGetList("tmdb", id.ToString(CultureInfo.InvariantCulture), out var list))
            {
                continue;
            }

            var (owned, total) = Score(overlapCatalog, list, media);
            var name = string.IsNullOrWhiteSpace(list.Name) ? groups.GetValueOrDefault(id) ?? "Collection " + id.ToString(CultureInfo.InvariantCulture) : list.Name;
            hits.Add(new RemoteHit("tmdb", list.Id, TrimName(name), owned, total));
        }

        var ranked = FranchiseRanker.Rank(hits, minOwned);
        var remaining = Math.Max(0, pending.Count - looked) + Math.Max(0, uncached.Count - fetched);
        if (message is null && remaining > 0)
        {
            message = string.Create(CultureInfo.InvariantCulture, $"Checked {looked:N0} more movie(s). {remaining:N0} lookup(s) left. Click Keep looking to continue.");
        }
        else if (message is null && ranked.Count == 0)
        {
            message = catalog.Count == 0
                ? "There are no movies in the library to suggest from."
                : "No franchises to suggest yet. Click Keep looking if lookups are still running. A movie has to belong to a TMDB collection.";
        }

        return Hits(ranked, remaining, message);
    }

    private async Task<SuggestListResponse> GetCoreAsync(string source, string id, MediaTypes media, CancellationToken cancellationToken)
    {
        RemoteList? list = source switch
        {
            "tmdb" => await GetTmdbAsync(id, cancellationToken).ConfigureAwait(false),
            "trakt" => await GetTraktAsync(id, cancellationToken).ConfigureAwait(false),
            "pack" => await GetPackAsync(id, media, cancellationToken).ConfigureAwait(false),
            _ => throw new BatchRequestException("Unknown list source.")
        };

        if (list is null)
        {
            var message = _lastFailure == FailureKind.RateLimited
                ? RateLimitMessage(source == "trakt" ? "Trakt" : "TMDB")
                : _lastError ?? "That list could not be loaded.";
            return new SuggestListResponse { Source = source, Id = id, Message = message };
        }

        return ToList(list, media);
    }

    private async Task<RemoteList?> GetTmdbAsync(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.TmdbApiKey))
        {
            _lastFailure = FailureKind.Failed;
            _lastError = MissingTmdbMessage();
            return null;
        }

        if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var collectionId) || collectionId <= 0)
        {
            throw new BatchRequestException("That TMDB collection id is not valid.");
        }

        return await LoadTmdbCollectionAsync(collectionId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RemoteList?> GetTraktAsync(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.TraktClientId))
        {
            _lastFailure = FailureKind.Failed;
            _lastError = MissingTraktMessage();
            return null;
        }

        var slash = id.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash >= id.Length - 1)
        {
            throw new BatchRequestException("That Trakt list id is not valid.");
        }

        var username = id[..slash];
        var slug = id[(slash + 1)..];
        if (!TraktResponses.IsSlug(username) || !TraktResponses.IsSlug(slug))
        {
            throw new BatchRequestException("That Trakt list id is not valid.");
        }

        return await LoadTraktAsync(username, slug, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RemoteList?> GetPackAsync(string id, MediaTypes media, CancellationToken cancellationToken)
    {
        if (!CuratedPacks.IsPackId(id))
        {
            throw new BatchRequestException("That pack id is not valid.");
        }

        if (!_cache.TryGetList("pack", id, out _))
        {
            var loaded = await PacksCoreAsync(RequirePackUri(), media, cancellationToken).ConfigureAwait(false);
            if (loaded.Message is not null && loaded.Hits.Count == 0)
            {
                _lastError = loaded.Message;
                return null;
            }
        }

        return _cache.TryGetList("pack", id, out var list) ? list : null;
    }

    private async Task<SuggestHitsResponse> TraktListsCoreAsync(string username, CancellationToken cancellationToken)
    {
        var body = await GetAsync(new Uri("https://api.trakt.tv/users/" + Uri.EscapeDataString(username) + "/lists"), TraktHeaders, cancellationToken).ConfigureAwait(false);
        if (body.RateLimited)
        {
            return Hits([], 0, RateLimitMessage("Trakt"));
        }

        if (!body.Ok || body.Text is null)
        {
            return Hits([], 0, body.Error ?? "Trakt could not list that user.");
        }

        try
        {
            var lists = TraktResponses.ReadLists(body.Text, username);
            var hits = lists.Select(list => new RemoteHit("trakt", list.Username + "/" + list.Slug, TrimName(list.Name), 0, 0)).ToList();
            return Hits(hits, 0, hits.Count == 0 ? "That user has no public lists." : null);
        }
        catch (JsonException)
        {
            return Hits([], 0, "Trakt returned something this plugin could not read.");
        }
    }

    private async Task<SuggestHitsResponse> PacksCoreAsync(Uri uri, MediaTypes media, CancellationToken cancellationToken)
    {
        var stamp = "packs:" + uri.AbsoluteUri;
        if (!_cache.TryGetStamp(stamp))
        {
            var body = await GetAsync(uri, null, cancellationToken).ConfigureAwait(false);
            if (body.RateLimited)
            {
                return Hits([], 0, "The pack file asked us to slow down. Try again in a minute.");
            }

            if (!body.Ok || body.Text is null)
            {
                return Hits([], 0, body.Error ?? "The pack file could not be downloaded.");
            }

            try
            {
                var lists = CuratedPacks.Read(body.Text);
                if (lists.Count == 0)
                {
                    return Hits([], 0, "The pack file has no lists. It needs a packs array. See the README for the format.");
                }

                _cache.ReplaceSource("pack", lists);
                _cache.RememberStamp(stamp);
            }
            catch (JsonException)
            {
                return Hits([], 0, "The pack file is not JSON this plugin can read.");
            }
        }

        var catalog = _catalog.GetEntries(media, cancellationToken);
        var hits = new List<RemoteHit>();
        foreach (var list in _cache.GetLists("pack"))
        {
            var (owned, total) = Score(catalog, list, media);
            hits.Add(new RemoteHit("pack", list.Id, TrimName(list.Name), owned, total));
        }

        var ordered = hits.OrderByDescending(hit => hit.Owned).ThenBy(hit => hit.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return Hits(ordered, 0, ordered.Count == 0 ? "The pack file has no lists." : null);
    }

    private async Task<RemoteList?> LoadTmdbCollectionAsync(int id, CancellationToken cancellationToken)
    {
        var key = id.ToString(CultureInfo.InvariantCulture);
        if (_cache.TryGetList("tmdb", key, out var cached))
        {
            _lastFailure = FailureKind.None;
            return cached;
        }

        var body = await GetAsync(TmdbUri("collection/" + key + "?language=en-US"), null, cancellationToken).ConfigureAwait(false);
        if (!body.Ok || body.Text is null)
        {
            Note(body, "TMDB");
            return null;
        }

        try
        {
            var list = TmdbResponses.ReadCollection(body.Text, id);
            if (list is null)
            {
                _lastFailure = FailureKind.Failed;
                _lastError = "TMDB did not return that collection.";
                return null;
            }

            _cache.RememberList(list);
            _lastFailure = FailureKind.None;
            return list;
        }
        catch (JsonException)
        {
            _lastFailure = FailureKind.Failed;
            _lastError = "TMDB returned something this plugin could not read.";
            return null;
        }
    }

    private bool NeedsLookup(CatalogEntry entry)
    {
        if (entry.TmdbId is > 0)
        {
            return !_cache.TryGetMovie(entry.TmdbId.Value, out _);
        }

        if (!_cache.TryGetTitle(TitleKey(entry), out var tmdbId))
        {
            return true;
        }

        return tmdbId is > 0 && !_cache.TryGetMovie(tmdbId.Value, out _);
    }

    private IEnumerable<int> ResolvedIds(IReadOnlyList<CatalogEntry> movies)
    {
        var ids = new HashSet<int>();
        foreach (var entry in movies)
        {
            if (entry.TmdbId is > 0)
            {
                ids.Add(entry.TmdbId.Value);
            }
            else if (_cache.TryGetTitle(TitleKey(entry), out var tmdbId) && tmdbId is > 0)
            {
                ids.Add(tmdbId.Value);
            }
        }

        return ids;
    }

    private async Task<int?> ResolveLibraryMovieAsync(CatalogEntry entry, CancellationToken cancellationToken)
    {
        if (entry.TmdbId is > 0)
        {
            _lastFailure = FailureKind.None;
            return entry.TmdbId;
        }

        var key = TitleKey(entry);
        if (_cache.TryGetTitle(key, out var cached))
        {
            _lastFailure = FailureKind.None;
            return cached;
        }

        _lastFailure = FailureKind.None;
        int? resolved = null;
        if (IsImdbId(entry.ImdbId))
        {
            resolved = await FindByImdbAsync(entry, cancellationToken).ConfigureAwait(false);
            if (_lastFailure != FailureKind.None)
            {
                return null;
            }
        }

        if (resolved is not > 0)
        {
            resolved = await SearchMovieAsync(entry, cancellationToken).ConfigureAwait(false);
            if (_lastFailure != FailureKind.None)
            {
                return null;
            }
        }

        _cache.RememberTitle(key, resolved);
        return resolved;
    }

    private async Task<int?> FindByImdbAsync(CatalogEntry entry, CancellationToken cancellationToken)
    {
        var body = await GetAsync(
            TmdbUri("find/" + Uri.EscapeDataString(entry.ImdbId!) + "?external_source=imdb_id"),
            null,
            cancellationToken).ConfigureAwait(false);
        if (body.Missing)
        {
            return null;
        }

        if (!body.Ok || body.Text is null)
        {
            Note(body, "TMDB");
            return null;
        }

        try
        {
            return MovieSearchPick.Pick(entry.Name, entry.OriginalTitle, entry.Year, TmdbResponses.ReadFindMovies(body.Text));
        }
        catch (JsonException)
        {
            _lastFailure = FailureKind.Failed;
            _lastError = "TMDB returned something this plugin could not read.";
            return null;
        }
    }

    private async Task<int?> SearchMovieAsync(CatalogEntry entry, CancellationToken cancellationToken)
    {
        var query = "search/movie?query=" + Uri.EscapeDataString(entry.Name) + "&include_adult=false&language=en-US";
        if (entry.Year is int year)
        {
            query += "&year=" + year.ToString(CultureInfo.InvariantCulture);
        }

        var body = await GetAsync(TmdbUri(query), null, cancellationToken).ConfigureAwait(false);
        if (!body.Ok || body.Text is null)
        {
            Note(body, "TMDB");
            return null;
        }

        try
        {
            return MovieSearchPick.Pick(entry.Name, entry.OriginalTitle, entry.Year, TmdbResponses.ReadMovieSearch(body.Text));
        }
        catch (JsonException)
        {
            _lastFailure = FailureKind.Failed;
            _lastError = "TMDB returned something this plugin could not read.";
            return null;
        }
    }

    private static List<CatalogEntry> DistinctMovies(IReadOnlyList<CatalogEntry> catalog)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<CatalogEntry>();
        foreach (var entry in catalog)
        {
            var key = entry.TmdbId is > 0 ? "id:" + entry.TmdbId.Value.ToString(CultureInfo.InvariantCulture) : "title:" + TitleKey(entry);
            if (seen.Add(key))
            {
                list.Add(entry);
            }
        }

        return list;
    }

    private static string TitleKey(CatalogEntry entry)
    {
        return TitleNormalizer.Strict(entry.Name) + "|" + (entry.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private static bool IsImdbId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length is < 5 or > 20 || !id.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var i = 2; i < id.Length; i++)
        {
            if (!char.IsDigit(id[i]))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<MovieMembership?> LoadMovieAsync(int id, CancellationToken cancellationToken)
    {
        var body = await GetAsync(TmdbUri("movie/" + id.ToString(CultureInfo.InvariantCulture)), null, cancellationToken).ConfigureAwait(false);
        if (body.Missing)
        {
            return new MovieMembership(null, null);
        }

        if (!body.Ok || body.Text is null)
        {
            Note(body, "TMDB");
            return null;
        }

        try
        {
            var membership = TmdbResponses.ReadMovie(body.Text);
            if (membership is null)
            {
                _lastFailure = FailureKind.Failed;
                _lastError = "TMDB returned something this plugin could not read.";
                return null;
            }

            return membership;
        }
        catch (JsonException)
        {
            _lastFailure = FailureKind.Failed;
            _lastError = "TMDB returned something this plugin could not read.";
            return null;
        }
    }

    private async Task<RemoteList?> LoadTraktAsync(string username, string slug, string? name, CancellationToken cancellationToken)
    {
        var id = username + "/" + slug;
        if (_cache.TryGetList("trakt", id, out var cached))
        {
            return cached;
        }

        var body = await GetAsync(
            new Uri("https://api.trakt.tv/users/" + Uri.EscapeDataString(username) + "/lists/" + Uri.EscapeDataString(slug) + "/items"),
            TraktHeaders,
            cancellationToken).ConfigureAwait(false);
        if (!body.Ok || body.Text is null)
        {
            Note(body, "Trakt");
            return null;
        }

        try
        {
            var list = TraktResponses.ReadItems(body.Text, username, slug, name);
            _cache.RememberList(list);
            return list;
        }
        catch (JsonException)
        {
            _lastFailure = FailureKind.Failed;
            _lastError = "Trakt returned something this plugin could not read.";
            return null;
        }
    }

    private void Note(in BodyResult body, string service)
    {
        if (body.RateLimited)
        {
            _lastFailure = FailureKind.RateLimited;
            _lastError = RateLimitMessage(service);
            return;
        }

        _lastFailure = FailureKind.Failed;
        _lastError = body.Error ?? service + " could not be reached.";
    }

    private async Task<T> WithGate<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        if (!await _busy.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new BatchRequestException("A lookup is already running. Wait for it to finish.");
        }

        try
        {
            _lastFailure = FailureKind.None;
            _lastError = null;
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _busy.Release();
        }
    }

    private Uri TmdbUri(string relative)
    {
        var join = relative.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return new Uri("https://api.themoviedb.org/3/" + relative + join + "api_key=" + Uri.EscapeDataString(_settings.TmdbApiKey.Trim()));
    }

    private void TraktHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("trakt-api-key", _settings.TraktClientId.Trim());
        request.Headers.TryAddWithoutValidation("trakt-api-version", "2");
    }

    private async Task<BodyResult> GetAsync(Uri uri, Action<HttpRequestMessage>? prepare, CancellationToken cancellationToken)
    {
        await PaceAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/json");
            prepare?.Invoke(request);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogInformation("Paste Collections: {Host} asked us to slow down", uri.Host);
                return BodyResult.Limited();
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                return BodyResult.Fail("The API key was rejected.");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return BodyResult.NotFound();
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Paste Collections: {Host} returned {Status}", uri.Host, (int)response.StatusCode);
                return BodyResult.Fail("The service returned an error (" + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ").");
            }

            if (text.Length > MaxBodyChars)
            {
                return BodyResult.Fail("The response was too large.");
            }

            return BodyResult.Success(text);
        }
        catch (Exception ex) when (IsTransportError(ex, cancellationToken))
        {
            _logger.LogWarning(ex, "Paste Collections: could not reach {Host}", uri.Host);
            return BodyResult.Fail("Could not reach " + uri.Host + " from the Jellyfin server.");
        }
    }

    private async Task PaceAsync(CancellationToken cancellationToken)
    {
        if (_gap <= TimeSpan.Zero)
        {
            return;
        }

        await _pace.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = _nextRequest - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            _nextRequest = DateTime.UtcNow + _gap;
        }
        finally
        {
            _pace.Release();
        }
    }

    private Uri RequirePackUri()
    {
        if (!TryHttpUrl(_settings.CuratedPacksUrl, out var uri))
        {
            throw new BatchRequestException("Add a packs URL under Suggest collections first.");
        }

        return uri;
    }

    private static bool TryHttpUrl(string? url, out Uri uri)
    {
        if (Uri.TryCreate(url?.Trim(), UriKind.Absolute, out uri!)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return true;
        }

        uri = null!;
        return false;
    }

    private static (int Owned, int Total) Score(IReadOnlyList<CatalogEntry> catalog, RemoteList list, MediaTypes media)
    {
        var total = LibraryOverlap.Measure([], list.Parts).Total;
        var owned = LibraryOverlap.Measure(catalog, LibraryOverlap.Filter(list.Parts, media)).Owned;
        return (owned, total);
    }

    private static bool IsTransportError(Exception ex, CancellationToken cancellationToken)
    {
        return ex is HttpRequestException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested);
    }

    private static MediaTypes RequireMedia(MediaTypes mediaTypes)
    {
        if (!Enum.IsDefined(mediaTypes))
        {
            throw new BatchRequestException("Media type must be Movies, Shows or Both.");
        }

        return mediaTypes;
    }

    private static string TrimName(string name)
    {
        var trimmed = OneLine(name);
        return trimmed.Length <= BatchCollectionsService.MaxNameLength ? trimmed : trimmed[..BatchCollectionsService.MaxNameLength].TrimEnd();
    }

    private static string OneLine(string value)
    {
        return value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
    }

    private static string MissingTmdbMessage()
    {
        return "Add a TMDB API key under Suggest collections, or set one on Jellyfin's TMDB plugin. The key stays in the plugin settings on this server.";
    }

    private static string MissingTraktMessage()
    {
        return "Add a Trakt client id under Suggest collections. Create one at trakt.tv/oauth/applications. No Trakt login is used.";
    }

    private static string RateLimitMessage(string service)
    {
        return service + " rate limit reached. What was found is saved on this server. Wait a minute, then try again.";
    }

    private static SuggestHitsResponse Hits(IReadOnlyList<RemoteHit> hits, int remaining, string? message)
    {
        return new SuggestHitsResponse
        {
            Hits =
            [
                .. hits.Select(hit => new SuggestHitDto
                {
                    Source = hit.Source,
                    Id = hit.Id,
                    Name = hit.Name,
                    Owned = hit.Owned,
                    Total = hit.Total
                })
            ],
            RemainingLookups = remaining,
            Message = message
        };
    }

    private static SuggestListResponse ToList(RemoteList list, MediaTypes media)
    {
        var parts = LibraryOverlap.Filter(list.Parts, media);
        return new SuggestListResponse
        {
            Source = list.Source,
            Id = list.Id,
            Name = TrimName(list.Name),
            Items =
            [
                .. parts.Select(part => new SuggestItemDto
                {
                    Title = OneLine(part.Title),
                    Year = part.Year,
                    TmdbId = part.TmdbId,
                    MediaType = MediaTypeText.Format(part.Kind)
                })
            ],
            Message = parts.Count == 0 ? "This list has no titles for the current Search in setting." : null
        };
    }

    private readonly record struct BodyResult(int Status, string? Text, string? Error)
    {
        public bool Ok => Status is >= 200 and < 300 && Text is not null;

        public bool RateLimited => Status == 429;

        public bool Missing => Status == 404;

        public static BodyResult Success(string text) => new(200, text, null);

        public static BodyResult Limited() => new(429, null, null);

        public static BodyResult NotFound() => new(404, null, null);

        public static BodyResult Fail(string error) => new(0, null, error);
    }
}
