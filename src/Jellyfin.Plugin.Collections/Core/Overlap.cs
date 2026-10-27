namespace Jellyfin.Plugin.Collections.Core;

/// <summary>
/// How many remote parts matched a library, and how many were considered.
/// </summary>
/// <param name="Owned">Parts that match a library item.</param>
/// <param name="Total">Parts after filtering.</param>
public readonly record struct Overlap(int Owned, int Total);
