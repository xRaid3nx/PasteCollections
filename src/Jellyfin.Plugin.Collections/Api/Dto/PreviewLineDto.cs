using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Collections.Core;

namespace Jellyfin.Plugin.Collections.Api.Dto;

/// <summary>
/// The preview result for one list line.
/// </summary>
public sealed class PreviewLineDto
{
    /// <summary>
    /// Gets or sets the 1-based line number.
    /// </summary>
    public int LineNumber { get; set; }

    /// <summary>
    /// Gets or sets the raw line text.
    /// </summary>
    public string Raw { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the parsed title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the parsed year.
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    /// Gets or sets a TMDB id that came with the line, when the list was suggested.
    /// </summary>
    public int? TmdbId { get; set; }

    /// <summary>
    /// Gets or sets <c>movie</c> or <c>tv</c> for <see cref="TmdbId"/>.
    /// </summary>
    public string? MediaType { get; set; }

    /// <summary>
    /// Gets or sets the match status.
    /// </summary>
    public MatchStatus Status { get; set; }

    /// <summary>
    /// Gets or sets the suggested item id.
    /// </summary>
    public Guid? SelectedId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the row is checked by default.
    /// </summary>
    public bool Selected { get; set; }

    /// <summary>
    /// Gets or sets the best fuzzy similarity.
    /// </summary>
    public double? Similarity { get; set; }

    /// <summary>
    /// Gets or sets the earlier line that matched the same item.
    /// </summary>
    public int? DuplicateOfLine { get; set; }

    /// <summary>
    /// Gets or sets an explanation.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    /// Gets the candidates.
    /// </summary>
    public IReadOnlyList<CandidateDto> Candidates { get; init; } = [];
}
