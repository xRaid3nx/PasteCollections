using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Collections.Api.Dto;
using Jellyfin.Plugin.Collections.Core;
using Jellyfin.Plugin.Collections.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Collections.Tests;

public sealed class SuggestTests : IDisposable
{
    private readonly string _cachePath = Path.Combine(Path.GetTempPath(), "paste-collections-suggest-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly FakeSettings _settings = new();
    private readonly FakeRemote _remote = new();
    private readonly SuggestCache _cache;
    private readonly SuggestService _service;

    public SuggestTests()
    {
        _cache = new SuggestCache(_cachePath, () => _settings.CacheHours, NullLogger<SuggestCache>.Instance);
        _service = new SuggestService(
            new ListCatalog(
            [
                new CatalogEntry(Guid.NewGuid(), "Star Wars", null, null, 1977, ItemKind.Movie, "Movies", null, 11)
            ]),
            _settings,
            _cache,
            NullLogger<SuggestService>.Instance,
            new HttpClient(_remote),
            true,
            TimeSpan.Zero);
    }

    public void Dispose()
    {
        _service.Dispose();
        _remote.Dispose();
        if (File.Exists(_cachePath))
        {
            File.Delete(_cachePath);
        }
    }

    [Fact]
    public void TmdbId_BeatsADifferentTitle()
    {
        var catalog = new List<CatalogEntry>
        {
            new(Guid.NewGuid(), "The Lion King", null, null, 1994, ItemKind.Movie, "Movies", null, 111),
            new(Guid.NewGuid(), "The Lion King", null, null, 2019, ItemKind.Movie, "Movies", null, 222)
        };
        var line = new ParsedLine(1, "not the title", "Something Else", 2019, TmdbId: 111);

        var result = Assert.Single(Matcher.Match(catalog, [line], new MatchOptions()));

        Assert.Equal(MatchStatus.Matched, result.Status);
        Assert.Equal(catalog[0].Id, result.SelectedId);
        Assert.True(result.IsSelectedByDefault);
    }

    [Fact]
    public void TmdbId_FallsBackToTitleWhenTheIdIsNotInTheLibrary()
    {
        var catalog = new List<CatalogEntry>
        {
            new(Guid.NewGuid(), "The Lion King", null, null, 1994, ItemKind.Movie, "Movies", null, 111)
        };
        var line = new ParsedLine(1, "The Lion King (1994)", "The Lion King", 1994, TmdbId: 999);

        var result = Assert.Single(Matcher.Match(catalog, [line], new MatchOptions()));

        Assert.Equal(MatchStatus.Matched, result.Status);
        Assert.Equal(catalog[0].Id, result.SelectedId);
    }

    [Fact]
    public void TmdbId_DoesNotMatchAMovieIdOntoASeries()
    {
        var catalog = new List<CatalogEntry>
        {
            new(Guid.NewGuid(), "Star Wars", null, null, 1977, ItemKind.Movie, "Movies", null, 11)
        };
        var line = new ParsedLine(1, "Star Wars", "Star Wars", 1977, TmdbId: 11, RemoteKind: ItemKind.Series);

        var result = Assert.Single(Matcher.Match(catalog, [line], new MatchOptions()));

        Assert.Equal(MatchStatus.NotFound, result.Status);
    }

    [Fact]
    public void KnownLines_AttachByLineNumber()
    {
        var parsed = ListParser.Parse("Star Wars (1977)\nThe Empire Strikes Back (1980)");
        var withIds = KnownLines.Apply(parsed.Lines, [new KnownLine(2, 1891, ItemKind.Movie)]);

        Assert.Null(withIds[0].TmdbId);
        Assert.Equal(1891, withIds[1].TmdbId);
        Assert.Equal(ItemKind.Movie, withIds[1].RemoteKind);
    }

    [Fact]
    public void Overlap_PrefersTmdbId_AndDoesNotCountADifferentIdWithTheSameTitle()
    {
        var catalog = new List<CatalogEntry>
        {
            new(Guid.NewGuid(), "Star Wars", null, null, 1977, ItemKind.Movie, "Movies", null, 11)
        };
        var parts = new List<RemotePart>
        {
            new("A New Hope", 1977, 11, ItemKind.Movie),
            new("Star Wars", 1977, 999, ItemKind.Movie)
        };

        var count = LibraryOverlap.Measure(catalog, parts);

        Assert.Equal(1, count.Owned);
        Assert.Equal(2, count.Total);
    }

    [Fact]
    public void Rank_OrdersByOwnedThenCompleteness()
    {
        var ranked = FranchiseRanker.Rank(
            [
                new RemoteHit("tmdb", "1", "Sparse", 1, 10),
                new RemoteHit("tmdb", "2", "Almost", 4, 5),
                new RemoteHit("tmdb", "3", "Empty", 0, 9),
                new RemoteHit("tmdb", "4", "Bigger", 4, 8)
            ],
            1);

        Assert.Equal(["Almost", "Bigger", "Sparse"], ranked.Select(hit => hit.Name).ToArray());
    }

    [Theory]
    [InlineData("https://trakt.tv/users/someone/lists/marvel", "someone", "marvel")]
    [InlineData("https://www.trakt.tv/users/someone/lists/marvel-phase-1/", "someone", "marvel-phase-1")]
    public void TraktUrl_ParsesUserLists(string url, string user, string slug)
    {
        Assert.True(TraktResponses.TryParseListUrl(url, out var username, out var list));
        Assert.Equal(user, username);
        Assert.Equal(slug, list);
    }

    [Fact]
    public void TraktItems_ReadMoviesAndShows()
    {
        const string Json = """
            [
              { "type": "movie", "movie": { "title": "Iron Man", "year": 2008, "ids": { "tmdb": 1726 } } },
              { "type": "show", "show": { "title": "Loki", "year": 2021, "ids": { "tmdb": 84958 } } }
            ]
            """;

        var list = TraktResponses.ReadItems(Json, "someone", "marvel", "Marvel");

        Assert.Equal("trakt", list.Source);
        Assert.Equal("someone/marvel", list.Id);
        Assert.Equal(2, list.Parts.Count);
        Assert.Equal(ItemKind.Series, list.Parts[1].Kind);
        Assert.Equal(84958, list.Parts[1].TmdbId);
    }

    [Fact]
    public void CuratedPacks_ReadTheDocument()
    {
        const string Json = """
            { "packs": [ { "id": "toy-story", "name": "Toy Story", "items": [
                { "title": "Toy Story", "year": 1995, "tmdbId": 862, "mediaType": "movie" }
            ] } ] }
            """;

        var list = Assert.Single(CuratedPacks.Read(Json));

        Assert.Equal("pack", list.Source);
        Assert.Equal("toy-story", list.Id);
        Assert.Equal(862, Assert.Single(list.Parts).TmdbId);
    }

    [Fact]
    public void TmdbMovie_ReadsMembership_AndNone()
    {
        var member = TmdbResponses.ReadMovie("""{ "id": 11, "belongs_to_collection": { "id": 10, "name": "Star Wars Collection" } }""");
        var none = TmdbResponses.ReadMovie("""{ "id": 11, "belongs_to_collection": null }""");

        Assert.Equal(10, member!.Value.CollectionId);
        Assert.Null(none!.Value.CollectionId);
    }

    [Fact]
    public void JellyfinTmdbKey_ReadsAnotherPluginsKey()
    {
        var host = new Host
        {
            Plugins = [new TmdbPlugin { Configuration = new TmdbConfig { ApiKey = "abcdefghij" } }]
        };

        Assert.Equal("abcdefghij", JellyfinTmdbKey.TryRead(host));
    }

    [Fact]
    public void Cache_ExpiresWhenHoursAreZero()
    {
        _settings.CacheHours = 0;
        _cache.RememberMovie(11, 10, "Star Wars Collection");

        Assert.False(_cache.TryGetMovie(11, out _));
    }

    [Fact]
    public async Task Search_ScoresOwnedTitles_AndDoesNotAutoApply()
    {
        _remote.Search = """{ "results": [ { "id": 10, "name": "Star Wars Collection" } ] }""";
        _remote.Collection = CollectionJson;

        var response = await _service.SearchAsync(new SuggestSearchRequest { Query = "Star Wars", MediaTypes = MediaTypes.Movies }, CancellationToken.None);

        var hit = Assert.Single(response.Hits);
        Assert.Equal("tmdb", hit.Source);
        Assert.Equal("10", hit.Id);
        Assert.Equal(1, hit.Owned);
        Assert.Equal(2, hit.Total);
        Assert.Contains("api_key=tmdb-key", _remote.LastQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_MissingKey_DoesNotCallTmdb()
    {
        _settings.TmdbApiKey = string.Empty;

        var response = await _service.SearchAsync(new SuggestSearchRequest { Query = "Star Wars" }, CancellationToken.None);

        Assert.Empty(response.Hits);
        Assert.Contains("TMDB API key", response.Message, StringComparison.Ordinal);
        Assert.Equal(0, _remote.Calls);
    }

    [Fact]
    public async Task FromLibrary_UsesTheCacheOnTheSecondCall()
    {
        _remote.Movie = """{ "id": 11, "belongs_to_collection": { "id": 10, "name": "Star Wars Collection" } }""";
        _remote.Collection = CollectionJson;

        var first = await _service.FromLibraryAsync(new SuggestLibraryRequest { MinOwned = 1, MediaTypes = MediaTypes.Movies }, CancellationToken.None);
        var calls = _remote.Calls;
        var second = await _service.FromLibraryAsync(new SuggestLibraryRequest { MinOwned = 1, MediaTypes = MediaTypes.Movies }, CancellationToken.None);

        var hit = Assert.Single(first.Hits);
        Assert.Equal(1, hit.Owned);
        Assert.Equal(2, hit.Total);
        Assert.Equal(0, first.RemainingLookups);
        Assert.Equal(calls, _remote.Calls);
        Assert.Equal(hit.Id, Assert.Single(second.Hits).Id);
    }

    [Fact]
    public async Task FromLibrary_StopsOnRateLimit()
    {
        _remote.MovieStatus = HttpStatusCode.TooManyRequests;

        var response = await _service.FromLibraryAsync(new SuggestLibraryRequest { MediaTypes = MediaTypes.Movies }, CancellationToken.None);

        Assert.Empty(response.Hits);
        Assert.Contains("rate limit", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.RemainingLookups > 0);
    }

    [Fact]
    public async Task GetCollection_ReturnsTitlesForPreview()
    {
        _remote.Collection = CollectionJson;

        var list = await _service.GetAsync("tmdb", "10", MediaTypes.Movies, CancellationToken.None);

        Assert.Equal("Star Wars Collection", list.Name);
        Assert.Equal(2, list.Items.Count);
        Assert.Equal(11, list.Items[0].TmdbId);
        Assert.Equal("movie", list.Items[0].MediaType);
    }

    [Fact]
    public async Task TraktUrl_LoadsItems()
    {
        _remote.TraktItems = """[{ "type": "movie", "movie": { "title": "Iron Man", "year": 2008, "ids": { "tmdb": 1726 } } }]""";

        var list = await _service.TraktItemsAsync(
            new SuggestTraktRequest { Url = "https://trakt.tv/users/someone/lists/marvel", MediaTypes = MediaTypes.Both },
            CancellationToken.None);

        Assert.Equal("Iron Man", Assert.Single(list.Items).Title);
        Assert.Equal("trakt-id", _remote.LastTraktKey);
    }

    [Fact]
    public async Task Packs_AreDownloadedAndScoredLocally()
    {
        _remote.Packs = """
            { "packs": [ { "id": "star-wars", "name": "Star Wars", "items": [
                { "title": "Star Wars", "year": 1977, "tmdbId": 11, "mediaType": "movie" },
                { "title": "The Empire Strikes Back", "year": 1980, "tmdbId": 1891, "mediaType": "movie" }
            ] } ] }
            """;

        var response = await _service.PacksAsync(MediaTypes.Movies, CancellationToken.None);

        var hit = Assert.Single(response.Hits);
        Assert.Equal("pack", hit.Source);
        Assert.Equal(1, hit.Owned);
        Assert.Equal(2, hit.Total);
    }

    private const string CollectionJson = """
        { "id": 10, "name": "Star Wars Collection", "parts": [
            { "id": 11, "title": "Star Wars", "release_date": "1977-05-25" },
            { "id": 1891, "title": "The Empire Strikes Back", "release_date": "1980-05-17" }
        ] }
        """;

    private sealed class ListCatalog(IReadOnlyList<CatalogEntry> entries) : ILibraryCatalog
    {
        public IReadOnlyList<CatalogEntry> GetEntries(MediaTypes mediaTypes, CancellationToken cancellationToken)
        {
            return mediaTypes switch
            {
                MediaTypes.Movies => [.. entries.Where(entry => entry.Kind == ItemKind.Movie)],
                MediaTypes.Shows => [.. entries.Where(entry => entry.Kind == ItemKind.Series)],
                _ => entries
            };
        }

        public IReadOnlyList<CatalogEntry> GetByIds(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
        {
            return [.. entries.Where(entry => ids.Contains(entry.Id))];
        }
    }

    private sealed class FakeSettings : ISuggestSettings
    {
        public string TmdbApiKey { get; set; } = "tmdb-key";

        public string TraktClientId { get; set; } = "trakt-id";

        public string CuratedPacksUrl { get; set; } = "https://packs.example/packs.json";

        public int CacheHours { get; set; } = 24;

        public int MinOwned { get; set; } = 1;
    }

    private sealed class Host
    {
        public IEnumerable<object> Plugins { get; set; } = [];
    }

    private sealed class TmdbPlugin
    {
        public string Name => "TMDB";

        public TmdbConfig Configuration { get; set; } = new();
    }

    private sealed class TmdbConfig
    {
        public string ApiKey { get; set; } = string.Empty;
    }

    private sealed class FakeRemote : HttpMessageHandler
    {
        public string Search { get; set; } = """{ "results": [] }""";

        public string Collection { get; set; } = """{ "id": 10, "name": "Empty", "parts": [] }""";

        public string Movie { get; set; } = """{ "id": 11, "belongs_to_collection": null }""";

        public string TraktItems { get; set; } = "[]";

        public string Packs { get; set; } = """{ "packs": [] }""";

        public HttpStatusCode MovieStatus { get; set; } = HttpStatusCode.OK;

        public int Calls { get; private set; }

        public string? LastQuery { get; private set; }

        public string? LastTraktKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastQuery = request.RequestUri?.Query;
            if (request.Headers.TryGetValues("trakt-api-key", out var keys))
            {
                LastTraktKey = keys.Single();
            }

            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/search/collection", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(HttpStatusCode.OK, Search));
            }

            if (path.Contains("/collection/", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(HttpStatusCode.OK, Collection));
            }

            if (path.Contains("/movie/", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(MovieStatus, Movie));
            }

            if (path.Contains("/items", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(HttpStatusCode.OK, TraktItems));
            }

            if (request.RequestUri.Host == "packs.example")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, Packs));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body)
        {
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
