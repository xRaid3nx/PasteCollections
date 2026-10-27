using System.Collections.Generic;
using Jellyfin.Plugin.Collections.Core;

namespace Jellyfin.Plugin.Collections.Api.Dto;

/// <summary>
/// Request body for <c>POST /PasteCollections/Preview</c>.
/// </summary>
public sealed class PreviewRequest
{
    /// <summary>
    /// Gets or sets the pasted list.
    /// </summary>
    public string? List { get; set; }

    /// <summary>
    /// Gets or sets the target collection name.
    /// </summary>
    public string? CollectionName { get; set; }

    /// <summary>
    /// Gets or sets which item kinds to search.
    /// </summary>
    public MediaTypes MediaTypes { get; set; } = MediaTypes.Both;

    /// <summary>
    /// Gets or sets the year tolerance (0 or 1).
    /// </summary>
    public int YearTolerance { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether fuzzy matching is enabled.
    /// </summary>
    public bool Fuzzy { get; set; } = true;

    /// <summary>
    /// Gets or sets TMDB ids for lines that came from a suggested list. Ignored when the pasted text was edited.
    /// </summary>
    public IReadOnlyList<KnownLineDto>? KnownLines { get; set; }
}
