using System.Collections.Generic;

namespace Jellyfin.Plugin.Collections.Api.Dto;

/// <summary>
/// A list ready to paste into Preview.
/// </summary>
public sealed class SuggestListResponse
{
    /// <summary>
    /// Gets or sets the source id.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list id.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the collection name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the titles.
    /// </summary>
    public IReadOnlyList<SuggestItemDto> Items { get; set; } = [];

    /// <summary>
    /// Gets or sets a message when the list could not be loaded, or has no titles for the current filter.
    /// </summary>
    public string? Message { get; set; }
}
