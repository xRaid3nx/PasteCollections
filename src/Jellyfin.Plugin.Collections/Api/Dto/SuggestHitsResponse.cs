using System.Collections.Generic;

namespace Jellyfin.Plugin.Collections.Api.Dto;

/// <summary>
/// Collections the user can choose from.
/// </summary>
public sealed class SuggestHitsResponse
{
    /// <summary>
    /// Gets or sets the collections.
    /// </summary>
    public IReadOnlyList<SuggestHitDto> Hits { get; set; } = [];

    /// <summary>
    /// Gets or sets how many TMDB lookups are still waiting. Zero when the scan is caught up.
    /// </summary>
    public int RemainingLookups { get; set; }

    /// <summary>
    /// Gets or sets a message for the page, such as a missing key or a rate limit.
    /// </summary>
    public string? Message { get; set; }
}
