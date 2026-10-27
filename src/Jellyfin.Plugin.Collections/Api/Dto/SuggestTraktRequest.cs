using Jellyfin.Plugin.Collections.Core;

namespace Jellyfin.Plugin.Collections.Api.Dto;

/// <summary>
/// Request body for a Trakt username or a Trakt list URL.
/// </summary>
public sealed class SuggestTraktRequest
{
    /// <summary>
    /// Gets or sets the Trakt username, when browsing that user's lists.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Gets or sets a Trakt list URL, when loading one list.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Gets or sets which kinds to keep.
    /// </summary>
    public MediaTypes MediaTypes { get; set; } = MediaTypes.Both;
}
