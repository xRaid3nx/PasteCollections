namespace Jellyfin.Plugin.Collections.Core;

/// <summary>
/// A collection the user can open, with how much of it is already in the library.
/// </summary>
/// <param name="Source">The source id.</param>
/// <param name="Id">The id within that source.</param>
/// <param name="Name">The collection name.</param>
/// <param name="Owned">How many parts are already in the library.</param>
/// <param name="Total">How many parts the list has after the media-type filter.</param>
public sealed record RemoteHit(string Source, string Id, string Name, int Owned, int Total);
