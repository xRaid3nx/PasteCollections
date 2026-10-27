namespace Jellyfin.Plugin.Collections.Api.Dto;

/// <summary>
/// A TMDB id the page already knows for one pasted line.
/// </summary>
public sealed class KnownLineDto
{
    /// <summary>
    /// Gets or sets the 1-based line number in the pasted text.
    /// </summary>
    public int LineNumber { get; set; }

    /// <summary>
    /// Gets or sets the TMDB id.
    /// </summary>
    public int? TmdbId { get; set; }

    /// <summary>
    /// Gets or sets <c>movie</c> or <c>tv</c>.
    /// </summary>
    public string? MediaType { get; set; }
}
