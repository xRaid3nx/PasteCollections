namespace Jellyfin.Plugin.Collections.Core;

/// <summary>
/// A Trakt user list, before its items are loaded.
/// </summary>
/// <param name="Username">The Trakt username.</param>
/// <param name="Slug">The list slug.</param>
/// <param name="Name">The list name.</param>
public readonly record struct TraktUserList(string Username, string Slug, string Name);
