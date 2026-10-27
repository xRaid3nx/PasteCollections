using System.Collections.Generic;

namespace Jellyfin.Plugin.Collections.Core;

/// <summary>
/// A remote collection and its parts.
/// </summary>
/// <param name="Source">The source id: <c>tmdb</c>, <c>trakt</c> or <c>pack</c>.</param>
/// <param name="Id">The id within that source.</param>
/// <param name="Name">The collection name to offer the user.</param>
/// <param name="Parts">The titles, in source order.</param>
public sealed record RemoteList(string Source, string Id, string Name, IReadOnlyList<RemotePart> Parts);
