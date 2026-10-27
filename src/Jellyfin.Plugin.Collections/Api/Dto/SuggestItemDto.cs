namespace Jellyfin.Plugin.Collections.Api.Dto;

/// <summary>
/// One title from a suggested list.
/// </summary>
public sealed class SuggestItemDto
{
    /// <summary>
    /// Gets or sets the title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the year.
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    /// Gets or sets the TMDB id, when the source has one.
    /// </summary>
    public int? TmdbId { get; set; }

    /// <summary>
    /// Gets or sets <c>movie</c> or <c>tv</c>.
    /// </summary>
    public string? MediaType { get; set; }
}
