using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Collections.Api.Dto;
using Jellyfin.Plugin.Collections.Core;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Collections.Services;

/// <summary>
/// Runs Preview and Apply. Depends only on small interfaces so it can be tested with fakes.
/// </summary>
public sealed class BatchCollectionsService
{
    /// <summary>
    /// The longest collection name accepted.
    /// </summary>
    public const int MaxNameLength = 200;

    /// <summary>
    /// The most item ids accepted by one Apply call.
    /// </summary>
    public const int MaxApplyIds = 20_000;

    private static readonly SemaphoreSlim _applyLock = new(1, 1);

    private readonly ILibraryCatalog _catalog;
    private static readonly TimeSpan _posterTimeout = TimeSpan.FromSeconds(60);

    private readonly ICollectionService _collections;
    private readonly ICollectionArtwork _artwork;
    private readonly ILogger<BatchCollectionsService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BatchCollectionsService"/> class.
    /// </summary>
    /// <param name="catalog">The library catalog.</param>
    /// <param name="collections">The collection service.</param>
    /// <param name="artwork">The collection artwork service.</param>
    /// <param name="logger">The logger.</param>
    public BatchCollectionsService(ILibraryCatalog catalog, ICollectionService collections, ICollectionArtwork artwork, ILogger<BatchCollectionsService> logger)
    {
        _catalog = catalog;
        _collections = collections;
        _artwork = artwork;
        _logger = logger;
    }

    /// <summary>
    /// Gets existing collections, sorted by name.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection summaries.</returns>
    public IReadOnlyList<CollectionSummaryDto> GetCollections(CancellationToken cancellationToken)
    {
        return [.. _collections.GetCollections(cancellationToken)
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new CollectionSummaryDto { Id = c.Id, Name = c.Name, ItemCount = c.MemberIds.Count })];
    }

    /// <summary>
    /// Matches the list against the library. Changes nothing.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The preview.</returns>
    /// <exception cref="BatchRequestException">The request is invalid.</exception>
    public PreviewResponse Preview(PreviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = ValidateName(request.CollectionName);
        var listError = ListParser.Validate(request.List);
        if (listError is not null)
        {
            throw new BatchRequestException(listError);
        }

        if (request.YearTolerance is < 0 or > 1)
        {
            throw new BatchRequestException("Year tolerance must be 0 (exact) or 1 (±1 year).");
        }

        if (!Enum.IsDefined(request.MediaTypes))
        {
            throw new BatchRequestException("Media type must be Movies, Shows or Both.");
        }

        var parsed = ListParser.Parse(request.List);
        if (parsed.Lines.Count == 0)
        {
            throw new BatchRequestException("The list has no titles (only blank or comment lines).");
        }

        var catalog = _catalog.GetEntries(request.MediaTypes, cancellationToken);
        var target = FindTarget(name, cancellationToken);
        var options = new MatchOptions
        {
            YearTolerance = request.YearTolerance,
            Fuzzy = request.Fuzzy,
            ExistingMemberIds = target.Collection?.MemberIds ?? new HashSet<Guid>()
        };

        var results = Matcher.Match(catalog, parsed.Lines, options, cancellationToken);
        var counts = Enum.GetValues<MatchStatus>().ToDictionary(s => s, s => results.Count(r => r.Status == s));
        _logger.LogInformation(
            "Paste Collections: preview for {Collection}: {Lines} lines, {Matched} matched, {NotFound} not found",
            name,
            results.Count,
            counts[MatchStatus.Matched],
            counts[MatchStatus.NotFound]);

        return new PreviewResponse
        {
            Collection = new TargetCollectionDto
            {
                Name = name,
                Exists = target.Collection is not null,
                Id = target.Collection?.Id,
                ItemCount = target.Collection?.MemberIds.Count ?? 0,
                Warning = target.Warning
            },
            Lines = [.. results.Select(ToDto)],
            DuplicateLines = [.. parsed.Duplicates.Select(d => new DuplicateLineDto
            {
                LineNumber = d.Line.LineNumber,
                Raw = d.Line.Raw,
                DuplicateOfLine = d.DuplicateOfLineNumber
            })],
            Counts = counts,
            LibraryItemCount = catalog.Count,
            IgnoredLineCount = parsed.IgnoredCount
        };
    }

    /// <summary>
    /// Adds exactly the confirmed items to the collection, creating it if needed. Never removes anything.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened.</returns>
    /// <exception cref="BatchRequestException">The request is invalid.</exception>
    public async Task<ApplyResponse> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = ValidateName(request.CollectionName);
        if (request.ItemIds is null || request.ItemIds.Count == 0)
        {
            throw new BatchRequestException("Nothing is selected. Tick at least one row in the preview.");
        }

        if (request.ItemIds.Count > MaxApplyIds)
        {
            throw new BatchRequestException(string.Create(CultureInfo.InvariantCulture, $"Too many items ({request.ItemIds.Count:N0}). The limit is {MaxApplyIds:N0}."));
        }

        if (!Enum.IsDefined(request.Poster))
        {
            throw new BatchRequestException("Unknown poster option.");
        }

        var requested = request.ItemIds.Where(id => !id.Equals(Guid.Empty)).Distinct().ToList();
        var valid = _catalog.GetByIds(requested, cancellationToken).Select(e => e.Id).ToHashSet();
        var failed = requested
            .Where(id => !valid.Contains(id))
            .Select(id => new FailedItemDto { Id = id, Reason = "Not a movie or series in the library." })
            .ToList();
        var validInOrder = requested.Where(valid.Contains).ToList();

        await _applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var target = FindTarget(name, cancellationToken);
            if (target.Collection is { } existing)
            {
                var toAdd = validInOrder.Where(id => !existing.MemberIds.Contains(id)).ToList();
                await _collections.AddAsync(existing.Id, toAdd, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Paste Collections: apply to existing {Collection}: added {Added}, already present {Present}, rejected {Failed}",
                    existing.Name,
                    toAdd.Count,
                    validInOrder.Count - toAdd.Count,
                    failed.Count);
                return new ApplyResponse
                {
                    CollectionId = existing.Id,
                    CollectionName = existing.Name,
                    Created = false,
                    Added = toAdd.Count,
                    AlreadyPresent = validInOrder.Count - toAdd.Count,
                    Failed = failed,
                    Warning = target.Warning
                };
            }

            if (validInOrder.Count == 0)
            {
                throw new BatchRequestException("None of the selected items are movies or series in the library, so no collection was created.");
            }

            // The first collection on a server triggers a library scan that can drop the new members.
            var firstCollection = _collections.GetCollections(cancellationToken).Count == 0;

            // Metadata is locked after the poster is added, otherwise the lock would stop Jellyfin's refresh.
            var lockLater = request.LockMetadata && request.Poster != PosterMode.Off;
            var id = await _collections.CreateAsync(name, validInOrder, request.LockMetadata && !lockLater, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Paste Collections: created {Collection} with {Added} items, rejected {Failed}",
                name,
                validInOrder.Count,
                failed.Count);
            string? warning = null;
            if (firstCollection)
            {
                try
                {
                    var readded = await _collections.EnsureMembersAsync(id, validInOrder, cancellationToken).ConfigureAwait(false);
                    if (readded > 0)
                    {
                        warning = string.Create(CultureInfo.InvariantCulture, $"Jellyfin's first-collection scan dropped {readded} item(s); they were added back.");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    _logger.LogWarning(ex, "Paste Collections: checking the first collection {Collection} failed", name);
                    warning = "This is the server's first collection. If it shows fewer items than expected, click Apply again.";
                }
            }

            var poster = await AddPosterAsync(id, name, validInOrder, request.Poster, lockLater, cancellationToken).ConfigureAwait(false);
            return new ApplyResponse
            {
                CollectionId = id,
                CollectionName = name,
                Created = true,
                Added = validInOrder.Count,
                AlreadyPresent = 0,
                Failed = failed,
                Poster = poster.Outcome,
                Backdrop = poster.Backdrop,
                PosterMessage = poster.Message.Length == 0 ? null : poster.Message,
                Warning = warning
            };
        }
        finally
        {
            _applyLock.Release();
        }
    }

    /// <summary>
    /// Gets existing collections that are missing a poster or a backdrop, sorted by name.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection summaries.</returns>
    public IReadOnlyList<CollectionSummaryDto> GetCollectionsWithoutPoster(CancellationToken cancellationToken)
    {
        return [.. GetCollections(cancellationToken).Where(c => _artwork.NeedsArtwork(c.Id))];
    }

    /// <summary>
    /// Fills in a missing poster and backdrop on an existing collection. Existing artwork is left alone.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened.</returns>
    /// <exception cref="BatchRequestException">The request is invalid.</exception>
    public async Task<PosterResponse> AddPosterToExistingAsync(PosterRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Poster) || request.Poster == PosterMode.Off)
        {
            throw new BatchRequestException("Choose how to find the poster.");
        }

        var collection = _collections.GetCollections(cancellationToken).FirstOrDefault(c => c.Id.Equals(request.CollectionId))
            ?? throw new BatchRequestException("That collection no longer exists.");

        await _applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var poster = _artwork.NeedsArtwork(collection.Id)
                ? await AddPosterAsync(collection.Id, collection.Name, [], request.Poster, false, cancellationToken).ConfigureAwait(false)
                : new PosterResult(PosterOutcome.NotAttempted, "It already has a poster and a backdrop.");
            return new PosterResponse { CollectionId = collection.Id, Poster = poster.Outcome, Backdrop = poster.Backdrop, Message = poster.Message };
        }
        finally
        {
            _applyLock.Release();
        }
    }

    private async Task<PosterResult> AddPosterAsync(Guid collectionId, string name, IReadOnlyList<Guid> memberIds, PosterMode mode, bool lockAfter, CancellationToken cancellationToken)
    {
        if (mode == PosterMode.Off)
        {
            return new PosterResult(PosterOutcome.NotAttempted, string.Empty);
        }

        PosterResult result;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(_posterTimeout);
            try
            {
                result = await _artwork.AddPosterAsync(collectionId, name, memberIds, mode, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                result = new PosterResult(PosterOutcome.None, "Looking for a poster took too long, so it was skipped.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Paste Collections: adding a poster to {Collection} failed", name);
                result = new PosterResult(PosterOutcome.None, "Could not add a poster. Check the server log.");
            }
        }

        if (lockAfter)
        {
            try
            {
                await _artwork.LockAsync(collectionId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Paste Collections: locking {Collection} failed", name);
                result = result with { Message = (result.Message + " The metadata could not be locked.").Trim() };
            }
        }

        return result;
    }

    private static string ValidateName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new BatchRequestException("Enter a collection name.");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new BatchRequestException(string.Create(CultureInfo.InvariantCulture, $"The collection name is too long (limit {MaxNameLength} characters)."));
        }

        return trimmed;
    }

    private static PreviewLineDto ToDto(MatchResult result)
    {
        return new PreviewLineDto
        {
            LineNumber = result.Line.LineNumber,
            Raw = result.Line.Raw,
            Title = result.Line.Title,
            Year = result.Line.Year,
            Status = result.Status,
            SelectedId = result.SelectedId,
            Selected = result.IsSelectedByDefault,
            Similarity = result.Similarity,
            DuplicateOfLine = result.DuplicateOfLineNumber,
            Note = result.Note,
            Candidates = [.. result.Candidates.Select(c => new CandidateDto
            {
                Id = c.Entry.Id,
                Name = c.Entry.Name,
                Year = c.Entry.Year,
                Kind = c.Entry.Kind,
                LibraryName = c.Entry.LibraryName,
                InCollection = c.InCollection,
                Similarity = c.Similarity
            })]
        };
    }

    private (CollectionInfo? Collection, string? Warning) FindTarget(string name, CancellationToken cancellationToken)
    {
        var matches = _collections.GetCollections(cancellationToken)
            .Where(c => string.Equals(c.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.MemberIds.Count)
            .ToList();

        if (matches.Count == 0)
        {
            return (null, null);
        }

        var warning = matches.Count > 1
            ? string.Create(CultureInfo.InvariantCulture, $"{matches.Count} collections are named \"{name}\". Using the largest one ({matches[0].MemberIds.Count} items).")
            : null;
        return (matches[0], warning);
    }
}
