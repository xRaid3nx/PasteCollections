using Jellyfin.Plugin.Collections.Core;

namespace Jellyfin.Plugin.Collections.Api.Dto;

/// <summary>
/// Request body for suggesting franchises from the local library.
/// </summary>
public sealed class SuggestLibraryRequest
{
    /// <summary>
    /// Gets or sets the minimum number of owned titles. Null uses the saved setting.
    /// </summary>
    public int? MinOwned { get; set; }

    /// <summary>
    /// Gets or sets how many uncached movies to ask TMDB about in this call.
    /// </summary>
    public int? MaxLookups { get; set; }

    /// <summary>
    /// Gets or sets which kinds to include when counting parts.
    /// </summary>
    public MediaTypes MediaTypes { get; set; } = MediaTypes.Both;
}
