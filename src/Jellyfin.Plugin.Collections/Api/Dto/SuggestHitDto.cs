namespace Jellyfin.Plugin.Collections.Api.Dto;

/// <summary>
/// A remote collection the user can load into the paste box.
/// </summary>
public sealed class SuggestHitDto
{
    /// <summary>
    /// Gets or sets the source: <c>tmdb</c>, <c>trakt</c> or <c>pack</c>.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the id within that source.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the collection name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many parts are already in the library.
    /// </summary>
    public int Owned { get; set; }

    /// <summary>
    /// Gets or sets how many parts the list has. Zero when the parts have not been loaded yet.
    /// </summary>
    public int Total { get; set; }
}
