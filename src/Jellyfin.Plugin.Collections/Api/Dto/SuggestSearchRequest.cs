using Jellyfin.Plugin.Collections.Core;

namespace Jellyfin.Plugin.Collections.Api.Dto;

/// <summary>
/// Request body for searching TMDB collections.
/// </summary>
public sealed class SuggestSearchRequest
{
    /// <summary>
    /// Gets or sets the search text.
    /// </summary>
    public string? Query { get; set; }

    /// <summary>
    /// Gets or sets which kinds count as owned and which parts are returned later.
    /// </summary>
    public MediaTypes MediaTypes { get; set; } = MediaTypes.Both;
}
