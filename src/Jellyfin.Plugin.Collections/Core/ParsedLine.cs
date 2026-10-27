namespace Jellyfin.Plugin.Collections.Core;

/// <summary>
/// One usable line from the pasted list.
/// </summary>
/// <param name="LineNumber">1-based line number in the original text.</param>
/// <param name="Raw">The line exactly as pasted (without the line break).</param>
/// <param name="Title">The cleaned title.</param>
/// <param name="Year">The year, when written in a recognised year format.</param>
/// <param name="BareYearTitle">
/// When the line ends in a bare 4-digit year-like number (e.g. <c>Aladdin 1992</c>), the title without that number.
/// Used only as a fallback when <paramref name="Title"/> finds nothing.
/// </param>
/// <param name="BareYear">The bare trailing number interpreted as a year, paired with <paramref name="BareYearTitle"/>.</param>
/// <param name="TmdbId">A TMDB id supplied with the line, when the list came from a remote collection.</param>
/// <param name="RemoteKind">Movie or series for <paramref name="TmdbId"/>, when known. TMDB movie and TV ids are separate.</param>
public sealed record ParsedLine(
    int LineNumber,
    string Raw,
    string Title,
    int? Year,
    string? BareYearTitle = null,
    int? BareYear = null,
    int? TmdbId = null,
    ItemKind? RemoteKind = null);
