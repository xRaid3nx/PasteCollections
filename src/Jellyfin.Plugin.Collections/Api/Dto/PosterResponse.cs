using System;
using Jellyfin.Plugin.Collections.Core;

namespace Jellyfin.Plugin.Collections.Api.Dto;

/// <summary>
/// Response for <c>POST /PasteCollections/Posters/Add</c>.
/// </summary>
public sealed class PosterResponse
{
    /// <summary>
    /// Gets or sets the collection id.
    /// </summary>
    public Guid CollectionId { get; set; }

    /// <summary>
    /// Gets or sets which poster the collection got.
    /// </summary>
    public PosterOutcome Poster { get; set; }

    /// <summary>
    /// Gets or sets which backdrop the collection got.
    /// </summary>
    public BackdropOutcome Backdrop { get; set; }

    /// <summary>
    /// Gets or sets a readable explanation of <see cref="Poster"/> and <see cref="Backdrop"/>.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}
