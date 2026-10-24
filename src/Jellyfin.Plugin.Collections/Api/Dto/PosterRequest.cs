using System;
using Jellyfin.Plugin.Collections.Core;

namespace Jellyfin.Plugin.Collections.Api.Dto;

/// <summary>
/// Request for <c>POST /PasteCollections/Posters/Add</c>.
/// </summary>
public sealed class PosterRequest
{
    /// <summary>
    /// Gets or sets the existing collection.
    /// </summary>
    public Guid CollectionId { get; set; }

    /// <summary>
    /// Gets or sets how to find the poster. <see cref="PosterMode.Off"/> is rejected.
    /// </summary>
    public PosterMode Poster { get; set; } = PosterMode.TmdbThenCollage;
}
