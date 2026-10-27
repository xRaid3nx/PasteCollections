using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Collections.Core;

/// <summary>
/// Orders franchise suggestions. Owned count first, then how complete the franchise is, then name.
/// </summary>
public static class FranchiseRanker
{
    /// <summary>
    /// Keeps hits you own at least <paramref name="minOwned"/> of, and sorts them.
    /// </summary>
    /// <param name="hits">The hits.</param>
    /// <param name="minOwned">The minimum owned count. Values below 1 still require one owned title.</param>
    /// <returns>The ranked hits.</returns>
    public static IReadOnlyList<RemoteHit> Rank(IReadOnlyList<RemoteHit> hits, int minOwned)
    {
        var floor = Math.Max(1, minOwned);
        return
        [
            .. hits
                .Where(hit => hit.Owned >= floor && hit.Total > 0)
                .OrderByDescending(hit => hit.Owned)
                .ThenByDescending(hit => (double)hit.Owned / hit.Total)
                .ThenBy(hit => hit.Name, StringComparer.OrdinalIgnoreCase)
        ];
    }
}
