namespace Jellyfin.Plugin.Collections.Core;

/// <summary>
/// Which backdrop a collection ended up with.
/// </summary>
public enum BackdropOutcome
{
    /// <summary>
    /// Nothing was attempted.
    /// </summary>
    NotAttempted,

    /// <summary>
    /// The collection was identified on TMDB.
    /// </summary>
    Tmdb,

    /// <summary>
    /// A collage was built from the members' backdrops.
    /// </summary>
    Collage,

    /// <summary>
    /// The only member with a backdrop lent its backdrop.
    /// </summary>
    SingleBackdrop,

    /// <summary>
    /// No backdrop could be found or made.
    /// </summary>
    None
}
