using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Collections.Core;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Collections.Services;

/// <summary>
/// Adds a poster to a collection using Jellyfin's own provider and image APIs.
/// </summary>
public sealed class CollectionArtwork : ICollectionArtwork
{
    private const int CollageWidth = 600;
    private const int CollageHeight = 900;
    private const int CollageTiles = 4;

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IImageEncoder _imageEncoder;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<CollectionArtwork> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CollectionArtwork"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="providerManager">The provider manager.</param>
    /// <param name="imageEncoder">The image encoder.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="logger">The logger.</param>
    public CollectionArtwork(ILibraryManager libraryManager, IProviderManager providerManager, IImageEncoder imageEncoder, IFileSystem fileSystem, ILogger<CollectionArtwork> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _imageEncoder = imageEncoder;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PosterResult> AddPosterAsync(Guid collectionId, string name, IReadOnlyList<Guid> memberIds, PosterMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(memberIds);
        if (mode == PosterMode.Off)
        {
            return new PosterResult(PosterOutcome.NotAttempted, string.Empty);
        }

        if (_libraryManager.GetItemById(collectionId) is not BoxSet collection)
        {
            return new PosterResult(PosterOutcome.None, "The collection could not be found to add a poster.");
        }

        var needPoster = !collection.HasImage(ImageType.Primary, 0);
        var needBackdrop = !collection.HasImage(ImageType.Backdrop, 0);
        if (!needPoster && !needBackdrop)
        {
            return new PosterResult(PosterOutcome.NotAttempted, "It already has a poster and a backdrop.");
        }

        var poster = PosterOutcome.NotAttempted;
        var backdrop = BackdropOutcome.NotAttempted;
        var messages = new List<string>();
        string? tmdb = null;
        if (needPoster)
        {
            if (collection.IsLocked)
            {
                messages.Add("The metadata is locked, so TMDB was skipped.");
            }
            else
            {
                tmdb = await IdentifyAsync(collection, name, cancellationToken).ConfigureAwait(false);
            }

            if (collection.HasImage(ImageType.Primary, 0))
            {
                poster = PosterOutcome.Tmdb;
                messages.Add(tmdb is null ? "Jellyfin already found a poster." : "Poster from TMDB (" + tmdb + ").");
            }
            else if (mode == PosterMode.TmdbOnly)
            {
                poster = PosterOutcome.None;
                messages.Add(tmdb is null ? "No matching collection on TMDB, so there is no poster." : "TMDB has no poster for " + tmdb + ".");
            }
        }

        if (needBackdrop && collection.HasImage(ImageType.Backdrop, 0))
        {
            backdrop = BackdropOutcome.Tmdb;
            needBackdrop = false;
        }

        var collagePoster = needPoster && poster == PosterOutcome.NotAttempted;
        if (mode == PosterMode.TmdbThenCollage && (collagePoster || needBackdrop))
        {
            var members = memberIds.Count > 0 ? memberIds : [.. collection.GetLinkedChildren().Select(item => item.Id)];
            if (collagePoster)
            {
                var made = await AddPosterCollageAsync(collection, members, cancellationToken).ConfigureAwait(false);
                poster = made.Outcome;
                messages.Add(made.Message);
            }

            if (needBackdrop)
            {
                var made = await AddBackdropCollageAsync(collection, members, cancellationToken).ConfigureAwait(false);
                backdrop = made.Backdrop;
                messages.Add(made.Message);
            }
        }

        return new PosterResult(poster, string.Join(" ", messages), backdrop);
    }

    /// <inheritdoc />
    public bool NeedsArtwork(Guid collectionId)
    {
        return _libraryManager.GetItemById(collectionId) is BoxSet collection
            && (!collection.HasImage(ImageType.Primary, 0) || !collection.HasImage(ImageType.Backdrop, 0));
    }

    /// <inheritdoc />
    public async Task LockAsync(Guid collectionId, CancellationToken cancellationToken)
    {
        if (_libraryManager.GetItemById(collectionId) is BoxSet collection && !collection.IsLocked)
        {
            collection.IsLocked = true;
            await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether a TMDB collection name clearly belongs to the collection name.
    /// </summary>
    /// <param name="collectionName">The name the user typed.</param>
    /// <param name="tmdbName">The TMDB collection name.</param>
    /// <returns><c>true</c> for the same name, with or without a trailing "Collection".</returns>
    internal static bool IsSameCollection(string collectionName, string? tmdbName)
    {
        var wanted = TitleNormalizer.Strict(collectionName);
        var found = TitleNormalizer.Strict(tmdbName);
        if (wanted.Length == 0 || found.Length == 0)
        {
            return false;
        }

        return string.Equals(wanted, found, StringComparison.Ordinal)
            || string.Equals(TitleNormalizer.Strict(collectionName + " Collection"), found, StringComparison.Ordinal)
            || string.Equals(wanted, TitleNormalizer.Strict(tmdbName + " Collection"), StringComparison.Ordinal);
    }

    private async Task<string?> IdentifyAsync(BoxSet collection, string name, CancellationToken cancellationToken)
    {
        IEnumerable<RemoteSearchResult> results;
        try
        {
            results = await _providerManager.GetRemoteSearchResults<BoxSet, BoxSetInfo>(
                new RemoteSearchQuery<BoxSetInfo>
                {
                    ItemId = collection.Id,
                    SearchInfo = new BoxSetInfo { Name = name }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Paste Collections: TMDB lookup for collection {Name} failed", name);
            return null;
        }

        var matches = results.Where(r => IsSameCollection(name, r.Name)).ToList();
        if (matches.Count != 1)
        {
            _logger.LogInformation("Paste Collections: {Count} TMDB collection(s) clearly match {Name}", matches.Count, name);
            return null;
        }

        var match = matches[0];
        await _providerManager.RefreshFullItem(
            collection,
            new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllImages = false,
                SearchResult = match
            },
            cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Paste Collections: identified collection {Name} as TMDB {TmdbName}", name, match.Name);
        return match.Name;
    }

    private List<string> GetMemberImages(IReadOnlyList<Guid> memberIds, ImageType type, int max)
    {
        var paths = new List<string>(max);
        foreach (var id in memberIds)
        {
            var path = _libraryManager.GetItemById(id)?.GetImageInfo(type, 0) is { IsLocalFile: true } image ? image.Path : null;
            if (!string.IsNullOrEmpty(path) && File.Exists(path) && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(path);
                if (paths.Count == max)
                {
                    break;
                }
            }
        }

        return paths;
    }

    private async Task<PosterResult> AddBackdropCollageAsync(BoxSet collection, IReadOnlyList<Guid> memberIds, CancellationToken cancellationToken)
    {
        var backdrops = GetMemberImages(memberIds, ImageType.Backdrop, BackdropCollage.MaxTiles);
        if (backdrops.Count == 0)
        {
            return new PosterResult(PosterOutcome.NotAttempted, "None of the items have a backdrop, so no backdrop was made.", BackdropOutcome.None);
        }

        var output = Path.Combine(Path.GetTempPath(), "paste-collections-" + Guid.NewGuid().ToString("N") + Path.GetExtension(backdrops[0]));
        try
        {
            BackdropOutcome outcome;
            if (backdrops.Count == 1)
            {
                File.Copy(backdrops[0], output);
                outcome = BackdropOutcome.SingleBackdrop;
            }
            else
            {
                output = Path.ChangeExtension(output, ".jpg");
                BackdropCollage.Build(backdrops, output);
                outcome = BackdropOutcome.Collage;
            }

            await SaveImageAsync(collection, output, ImageType.Backdrop, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Paste Collections: gave collection {Name} a {Outcome} backdrop from {Count} item(s)", collection.Name, outcome, backdrops.Count);
            return outcome == BackdropOutcome.Collage
                ? new PosterResult(PosterOutcome.NotAttempted, "Backdrop collage made from " + backdrops.Count + " backdrops.", outcome)
                : new PosterResult(PosterOutcome.NotAttempted, "Used the backdrop of the only item that has one.", outcome);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Paste Collections: could not make a backdrop for collection {Name}", collection.Name);
            return new PosterResult(PosterOutcome.NotAttempted, "Could not make a backdrop. Check the server log.", BackdropOutcome.None);
        }
        finally
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    private async Task<PosterResult> AddPosterCollageAsync(BoxSet collection, IReadOnlyList<Guid> memberIds, CancellationToken cancellationToken)
    {
        var posters = GetMemberImages(memberIds, ImageType.Primary, CollageTiles);
        if (posters.Count == 0)
        {
            return new PosterResult(PosterOutcome.None, "None of the items have a poster yet, so no collage was made.");
        }

        var output = Path.Combine(Path.GetTempPath(), "paste-collections-" + Guid.NewGuid().ToString("N") + Path.GetExtension(posters[0]));
        try
        {
            PosterOutcome outcome;
            if (posters.Count == 1 || !_imageEncoder.SupportsImageCollageCreation)
            {
                File.Copy(posters[0], output);
                outcome = PosterOutcome.SinglePoster;
            }
            else
            {
                output = Path.ChangeExtension(output, ".jpg");
                _imageEncoder.CreateImageCollage(new ImageCollageOptions { InputPaths = [.. posters], OutputPath = output, Width = CollageWidth, Height = CollageHeight }, null);
                outcome = PosterOutcome.Collage;
            }

            if (!File.Exists(output))
            {
                return new PosterResult(PosterOutcome.None, "Jellyfin could not build the collage.");
            }

            await SaveImageAsync(collection, output, ImageType.Primary, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Paste Collections: gave collection {Name} a {Outcome} poster from {Count} item(s)", collection.Name, outcome, posters.Count);
            return outcome == PosterOutcome.Collage
                ? new PosterResult(outcome, "Collage made from " + posters.Count + " posters.")
                : new PosterResult(outcome, "Used the poster of the only item that has one.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Paste Collections: could not make a poster for collection {Name}", collection.Name);
            return new PosterResult(PosterOutcome.None, "Could not make a poster. Check the server log.");
        }
        finally
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    private async Task SaveImageAsync(BoxSet collection, string path, ImageType type, CancellationToken cancellationToken)
    {
        await _providerManager.SaveImage(collection, path, MimeFor(path), type, null, false, cancellationToken).ConfigureAwait(false);

        // SaveImage only updates the in-memory item; without this the image is lost when Jellyfin reloads it.
        await collection.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
    }

    private static string MimeFor(string path) => Path.GetExtension(path).ToUpperInvariant() switch
    {
        ".PNG" => "image/png",
        ".WEBP" => "image/webp",
        _ => "image/jpeg"
    };
}
