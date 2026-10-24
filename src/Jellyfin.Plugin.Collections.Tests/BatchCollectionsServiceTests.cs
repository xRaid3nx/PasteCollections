using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Collections.Api.Dto;
using Jellyfin.Plugin.Collections.Core;
using Jellyfin.Plugin.Collections.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Collections.Tests;

public class BatchCollectionsServiceTests
{
    private readonly FakeCollections _collections = new();
    private readonly FakeArtwork _artwork = new();
    private readonly BatchCollectionsService _service;

    public BatchCollectionsServiceTests()
    {
        _service = new BatchCollectionsService(new FakeCatalog(DisneyCatalog.All), _collections, _artwork, NullLogger<BatchCollectionsService>.Instance);
    }

    private Task<ApplyResponse> ApplyWithPoster(string name, PosterMode poster, bool locked, params Guid[] ids)
    {
        return _service.ApplyAsync(new ApplyRequest { CollectionName = name, ItemIds = ids, LockMetadata = locked, Poster = poster }, CancellationToken.None);
    }

    [Fact]
    public async Task Apply_NewCollection_AddsPoster_WithMembersInListOrder()
    {
        var ids = new[] { Id("Moana", 2016), Id("Coco", 2017), Id("Frozen", 2013) };

        var response = await ApplyWithPoster("Disney Classics", PosterMode.TmdbThenCollage, false, ids);

        var call = Assert.Single(_artwork.Calls);
        Assert.Equal(response.CollectionId, call.CollectionId);
        Assert.Equal("Disney Classics", call.Name);
        Assert.Equal(ids, call.MemberIds);
        Assert.Equal(PosterMode.TmdbThenCollage, call.Mode);
        Assert.Equal(PosterOutcome.Collage, response.Poster);
        Assert.Equal("Collage made from 4 posters.", response.PosterMessage);
        Assert.Empty(_artwork.Locked);
    }

    [Fact]
    public async Task Apply_LockWithPoster_LocksAfterThePoster()
    {
        var response = await ApplyWithPoster("Disney Classics", PosterMode.TmdbOnly, true, Id("Moana", 2016));

        Assert.False(_collections.Store.Single().Locked);
        Assert.Equal([response.CollectionId], _artwork.Locked);
    }

    [Fact]
    public async Task Apply_PosterOff_LocksAtCreation_AndSkipsArtwork()
    {
        var response = await ApplyWithPoster("Disney Classics", PosterMode.Off, true, Id("Moana", 2016));

        Assert.True(_collections.Store.Single().Locked);
        Assert.Empty(_artwork.Calls);
        Assert.Empty(_artwork.Locked);
        Assert.Equal(PosterOutcome.NotAttempted, response.Poster);
        Assert.Null(response.PosterMessage);
    }

    [Fact]
    public async Task Apply_ExistingCollection_NeverTouchesArtwork()
    {
        _collections.Seed("Disney Classics", Id("Coco", 2017));

        var response = await ApplyWithPoster("Disney Classics", PosterMode.TmdbThenCollage, true, Id("Moana", 2016));

        Assert.False(response.Created);
        Assert.Empty(_artwork.Calls);
        Assert.Empty(_artwork.Locked);
    }

    [Fact]
    public async Task Apply_PosterFailure_StillCreatesAndLocks()
    {
        _artwork.Throw = true;

        var response = await ApplyWithPoster("Disney Classics", PosterMode.TmdbThenCollage, true, Id("Moana", 2016));

        Assert.True(response.Created);
        Assert.Equal(PosterOutcome.None, response.Poster);
        Assert.Single(_artwork.Locked);
    }

    [Fact]
    public async Task Apply_UnknownPosterMode_IsRejected()
    {
        await Assert.ThrowsAsync<BatchRequestException>(() => ApplyWithPoster("x", (PosterMode)42, false, Id("Moana", 2016)));
    }

    [Fact]
    public void CollectionsWithoutPoster_ListsOnlyBlankOnes_SortedByName()
    {
        var withPoster = _collections.Seed("Pixar", Id("Coco", 2017));
        _collections.Seed("Zoo Movies", Id("Moana", 2016));
        _collections.Seed("Animals", Id("Coco", 2017), Id("Moana", 2016));
        _artwork.WithPoster.Add(withPoster);

        var list = _service.GetCollectionsWithoutPoster(CancellationToken.None);

        Assert.Equal(["Animals", "Zoo Movies"], list.Select(c => c.Name));
        Assert.Equal(2, list[0].ItemCount);
    }

    [Fact]
    public async Task AddPosterToExisting_UsesTheCollectionsOwnOrder()
    {
        var id = _collections.Seed("Disney Classics", Id("Coco", 2017));

        var response = await _service.AddPosterToExistingAsync(new PosterRequest { CollectionId = id, Poster = PosterMode.TmdbThenCollage }, CancellationToken.None);

        var call = Assert.Single(_artwork.Calls);
        Assert.Equal(id, call.CollectionId);
        Assert.Equal("Disney Classics", call.Name);
        Assert.Empty(call.MemberIds);
        Assert.Empty(_artwork.Locked);
        Assert.Equal(PosterOutcome.Collage, response.Poster);
        Assert.Equal("Collage made from 4 posters.", response.Message);
    }

    [Fact]
    public async Task AddPosterToExisting_LeavesExistingPosterAlone()
    {
        var id = _collections.Seed("Disney Classics", Id("Coco", 2017));
        _artwork.WithPoster.Add(id);

        var response = await _service.AddPosterToExistingAsync(new PosterRequest { CollectionId = id }, CancellationToken.None);

        Assert.Empty(_artwork.Calls);
        Assert.Equal(PosterOutcome.NotAttempted, response.Poster);
    }

    [Fact]
    public async Task AddPosterToExisting_FailureBecomesAMessage()
    {
        var id = _collections.Seed("Disney Classics", Id("Coco", 2017));
        _artwork.Throw = true;

        var response = await _service.AddPosterToExistingAsync(new PosterRequest { CollectionId = id }, CancellationToken.None);

        Assert.Equal(PosterOutcome.None, response.Poster);
        Assert.Equal("Could not add a poster. Check the server log.", response.Message);
    }

    [Theory]
    [InlineData(PosterMode.Off)]
    [InlineData((PosterMode)42)]
    public async Task AddPosterToExisting_RejectsOffAndUnknownModes(PosterMode mode)
    {
        var id = _collections.Seed("Disney Classics", Id("Coco", 2017));

        await Assert.ThrowsAsync<BatchRequestException>(() => _service.AddPosterToExistingAsync(new PosterRequest { CollectionId = id, Poster = mode }, CancellationToken.None));
        Assert.Empty(_artwork.Calls);
    }

    [Fact]
    public async Task AddPosterToExisting_UnknownCollection_IsRejected()
    {
        await Assert.ThrowsAsync<BatchRequestException>(() => _service.AddPosterToExistingAsync(new PosterRequest { CollectionId = Guid.NewGuid() }, CancellationToken.None));
    }

    [Theory]
    [InlineData("Toy Story", "Toy Story Collection", true)]
    [InlineData("Toy Story Collection", "Toy Story Collection", true)]
    [InlineData("the lord of the rings collection", "The Lord of the Rings Collection", true)]
    [InlineData("Toy Story Collection", "Toy Story", true)]
    [InlineData("Maddock Horror Comedy Universe", "Horror Comedy Collection", false)]
    [InlineData("Star Wars", "Star Wars: The Clone Wars Collection", false)]
    [InlineData("Disney Classics", "", false)]
    public void TmdbCollectionName_MatchesOnlyTheSameName(string collection, string tmdb, bool expected)
    {
        Assert.Equal(expected, CollectionArtwork.IsSameCollection(collection, tmdb));
    }

    private static Guid Id(string name, int year) => DisneyCatalog.Get(name, year).Id;

    private PreviewResponse Preview(string list, string name = "Disney Classics", MediaTypes types = MediaTypes.Movies)
    {
        return _service.Preview(new PreviewRequest { List = list, CollectionName = name, MediaTypes = types }, CancellationToken.None);
    }

    private Task<ApplyResponse> Apply(string name, bool locked = false, params Guid[] ids)
    {
        return _service.ApplyAsync(new ApplyRequest { CollectionName = name, ItemIds = ids, LockMetadata = locked }, CancellationToken.None);
    }

    [Fact]
    public void Preview_NewCollection_ReportsStatusesAndCounts()
    {
        var response = Preview("The Lion King (1994)\nThe Lion King\nTreasure Planet\nMoana\n- moana\nMoana (2016)");

        Assert.False(response.Collection.Exists);
        Assert.Equal("Disney Classics", response.Collection.Name);
        Assert.Equal(5, response.Lines.Count);
        Assert.Equal(5, Assert.Single(response.DuplicateLines).LineNumber);
        Assert.Equal(3, response.Counts[MatchStatus.Matched]);
        Assert.Equal(4, response.Lines[^1].DuplicateOfLine);
        Assert.False(response.Lines[^1].Selected);
        Assert.Equal(1, response.Counts[MatchStatus.Ambiguous]);
        Assert.Equal(1, response.Counts[MatchStatus.NotFound]);
        Assert.Equal(DisneyCatalog.MoviesOnly.Count, response.LibraryItemCount);
    }

    [Fact]
    public void Preview_ExistingCollection_CaseInsensitive_MarksMembers()
    {
        _collections.Seed("disney CLASSICS", Id("Moana", 2016));

        var response = Preview("Moana\nCoco", name: "  Disney Classics ");

        Assert.True(response.Collection.Exists);
        Assert.Equal(1, response.Collection.ItemCount);
        Assert.Equal(MatchStatus.AlreadyInCollection, response.Lines[0].Status);
        Assert.False(response.Lines[0].Selected);
        Assert.Equal(MatchStatus.Matched, response.Lines[1].Status);
    }

    [Fact]
    public void Preview_TwoCollectionsSameName_UsesLargestAndWarns()
    {
        _collections.Seed("Disney Classics", Id("Moana", 2016));
        var big = _collections.Seed("Disney Classics", Id("Coco", 2017), Id("Up", 2009));

        var response = Preview("Moana");

        Assert.Equal(big, response.Collection.Id);
        Assert.NotNull(response.Collection.Warning);
    }

    [Fact]
    public void Preview_ShowsOnly_SearchesSeries()
    {
        var response = Preview("Hercules\nMoana", types: MediaTypes.Shows);

        Assert.Equal(MatchStatus.Matched, response.Lines[0].Status);
        Assert.Equal(ItemKind.Series, response.Lines[0].Candidates[0].Kind);
        Assert.Equal(MatchStatus.NotFound, response.Lines[1].Status);
    }

    [Theory]
    [InlineData("", "Disney")]
    [InlineData("   \n  ", "Disney")]
    [InlineData("# only a comment", "Disney")]
    [InlineData("Moana", "")]
    [InlineData("Moana", "    ")]
    [InlineData("Moana", null)]
    public void Preview_BadInput_IsReadableError(string list, string? name)
    {
        var ex = Assert.Throws<BatchRequestException>(() => _service.Preview(new PreviewRequest { List = list, CollectionName = name }, CancellationToken.None));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void Preview_BadToleranceOrMediaType_IsRejected()
    {
        Assert.Throws<BatchRequestException>(() => _service.Preview(new PreviewRequest { List = "Moana", CollectionName = "x", YearTolerance = 5 }, CancellationToken.None));
        Assert.Throws<BatchRequestException>(() => _service.Preview(new PreviewRequest { List = "Moana", CollectionName = "x", MediaTypes = (MediaTypes)42 }, CancellationToken.None));
    }

    [Fact]
    public void Preview_TooLongName_IsRejected()
    {
        Assert.Throws<BatchRequestException>(() => Preview("Moana", name: new string('x', 201)));
    }

    [Fact]
    public void Preview_EmptyLibrary_AllNotFound()
    {
        var service = new BatchCollectionsService(new FakeCatalog([]), _collections, _artwork, NullLogger<BatchCollectionsService>.Instance);

        var response = service.Preview(new PreviewRequest { List = "Moana\nCoco", CollectionName = "x" }, CancellationToken.None);

        Assert.Equal(0, response.LibraryItemCount);
        Assert.All(response.Lines, l => Assert.Equal(MatchStatus.NotFound, l.Status));
    }

    [Fact]
    public async Task Apply_CreatesCollection_WithConfirmedItems()
    {
        var response = await Apply("Disney Classics", true, Id("Moana", 2016), Id("Coco", 2017));

        Assert.True(response.Created);
        Assert.Equal(2, response.Added);
        var stored = Assert.Single(_collections.Store);
        Assert.Equal("Disney Classics", stored.Name);
        Assert.True(stored.Locked);
        Assert.Equal(2, stored.Members.Count);
    }

    [Fact]
    public async Task Apply_Twice_IsIdempotent()
    {
        await Apply("Disney Classics", false, Id("Moana", 2016), Id("Coco", 2017));

        var second = await Apply("disney classics", false, Id("Moana", 2016), Id("Coco", 2017));

        Assert.False(second.Created);
        Assert.Equal(0, second.Added);
        Assert.Equal(2, second.AlreadyPresent);
        Assert.Single(_collections.Store);
        Assert.Equal(1, _collections.CreateCalls);
        Assert.Equal(0, _collections.AddCalls);
    }

    [Fact]
    public async Task Apply_ExistingCollection_AddsOnlyNew_NeverRemoves()
    {
        var existing = _collections.Seed("Disney Classics", Id("Moana", 2016), Id("Up", 2009));

        var response = await Apply("Disney Classics", false, Id("Moana", 2016), Id("Coco", 2017));

        Assert.False(response.Created);
        Assert.Equal(existing, response.CollectionId);
        Assert.Equal(1, response.Added);
        Assert.Equal(1, response.AlreadyPresent);
        var members = _collections.Store.Single().Members;
        Assert.Equal(3, members.Count);
        Assert.Contains(Id("Up", 2009), members);
    }

    [Fact]
    public async Task Apply_RejectsUnknownIds_AndDeduplicates()
    {
        var bogus = Guid.NewGuid();

        var response = await Apply("Disney Classics", false, Id("Moana", 2016), Id("Moana", 2016), bogus, Guid.Empty);

        Assert.Equal(1, response.Added);
        var failed = Assert.Single(response.Failed);
        Assert.Equal(bogus, failed.Id);
    }

    [Fact]
    public async Task Apply_OnlyInvalidIds_DoesNotCreate()
    {
        await Assert.ThrowsAsync<BatchRequestException>(() => Apply("Disney Classics", false, Guid.NewGuid()));
        Assert.Empty(_collections.Store);
    }

    [Fact]
    public async Task Apply_NothingSelected_IsRejected()
    {
        await Assert.ThrowsAsync<BatchRequestException>(() => Apply("Disney Classics", false));
        await Assert.ThrowsAsync<BatchRequestException>(() => _service.ApplyAsync(new ApplyRequest { CollectionName = "x" }, CancellationToken.None));
        await Assert.ThrowsAsync<BatchRequestException>(() => Apply("  ", false, Id("Moana", 2016)));
    }

    [Fact]
    public async Task Apply_TooManyIds_IsRejected()
    {
        var ids = Enumerable.Range(0, BatchCollectionsService.MaxApplyIds + 1).Select(_ => Guid.NewGuid()).ToArray();

        await Assert.ThrowsAsync<BatchRequestException>(() => Apply("x", false, ids));
    }

    [Fact]
    public async Task Apply_TwoCollectionsSameName_AddsToLargest()
    {
        _collections.Seed("Disney Classics");
        var big = _collections.Seed("Disney Classics", Id("Up", 2009));

        var response = await Apply("Disney Classics", false, Id("Moana", 2016));

        Assert.Equal(big, response.CollectionId);
        Assert.NotNull(response.Warning);
    }

    [Fact]
    public void GetCollections_SortedWithCounts()
    {
        _collections.Seed("Pixar", Id("Up", 2009));
        _collections.Seed("Disney Classics", Id("Moana", 2016), Id("Coco", 2017));

        var list = _service.GetCollections(CancellationToken.None);

        Assert.Equal(new[] { "Disney Classics", "Pixar" }, list.Select(c => c.Name));
        Assert.Equal(2, list[0].ItemCount);
    }

    [Fact]
    public async Task PreviewThenApply_EndToEnd_RerunAddsNothing()
    {
        const string List = "The Lion King (1994)\nAladdin (1992)\nTreasure Planet\nFrozen 2";
        var preview = Preview(List);
        var ids = preview.Lines.Where(l => l.Selected).Select(l => l.SelectedId!.Value).ToArray();

        var first = await Apply("Disney Classics", false, ids);
        var rerun = Preview(List);

        Assert.Equal(3, first.Added);
        Assert.Equal(3, rerun.Counts[MatchStatus.AlreadyInCollection]);
        Assert.DoesNotContain(rerun.Lines, l => l.Selected);
    }
}
