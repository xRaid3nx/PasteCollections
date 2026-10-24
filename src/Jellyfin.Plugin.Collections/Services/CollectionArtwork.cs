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

        if (collection.IsLocked && mode == PosterMode.TmdbOnly)
        {
            return new PosterResult(PosterOutcome.None, "The collection's metadata is locked, so TMDB was skipped.");
        }

        var tmdb = collection.IsLocked ? null : await IdentifyAsync(collection, name, cancellationToken).ConfigureAwait(false);
        if (tmdb is not null && collection.HasImage(ImageType.Primary, 0))
        {
            return new PosterResult(PosterOutcome.Tmdb, "Poster from TMDB (" + tmdb + ").");
        }

        if (collection.HasImage(ImageType.Primary, 0))
        {
            return new PosterResult(PosterOutcome.Tmdb, "Jellyfin already found a poster.");
        }

        if (mode == PosterMode.TmdbOnly)
        {
            return new PosterResult(PosterOutcome.None, tmdb is null ? "No matching collection on TMDB, so there is no poster." : "TMDB has no poster for " + tmdb + ".");
        }

        var members = memberIds.Count > 0 ? memberIds : [.. collection.GetLinkedChildren().Select(item => item.Id)];
        return await AddCollageAsync(collection, members, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool NeedsPoster(Guid collectionId)
    {
        return _libraryManager.GetItemById(collectionId) is BoxSet collection && !collection.HasImage(ImageType.Primary, 0);
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
                ReplaceAllImages = true,
                SearchResult = match
            },
            cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Paste Collections: identified collection {Name} as TMDB {TmdbName}", name, match.Name);
        return match.Name;
    }

    private async Task<PosterResult> AddCollageAsync(BoxSet collection, IReadOnlyList<Guid> memberIds, CancellationToken cancellationToken)
    {
        var posters = new List<string>(CollageTiles);
        foreach (var id in memberIds)
        {
            var path = _libraryManager.GetItemById(id)?.GetImageInfo(ImageType.Primary, 0) is { IsLocalFile: true } image ? image.Path : null;
            if (!string.IsNullOrEmpty(path) && File.Exists(path) && !posters.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                posters.Add(path);
                if (posters.Count == CollageTiles)
                {
                    break;
                }
            }
        }

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

            await _providerManager.SaveImage(collection, output, MimeFor(output), ImageType.Primary, null, false, cancellationToken).ConfigureAwait(false);
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

    private static string MimeFor(string path) => Path.GetExtension(path).ToUpperInvariant() switch
    {
        ".PNG" => "image/png",
        ".WEBP" => "image/webp",
        _ => "image/jpeg"
    };
}
