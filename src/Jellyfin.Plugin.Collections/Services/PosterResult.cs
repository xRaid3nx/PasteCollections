using Jellyfin.Plugin.Collections.Core;

namespace Jellyfin.Plugin.Collections.Services;

/// <summary>
/// What happened when artwork was added to a collection.
/// </summary>
/// <param name="Outcome">The poster outcome.</param>
/// <param name="Message">A readable explanation.</param>
/// <param name="Backdrop">The backdrop outcome.</param>
public sealed record PosterResult(PosterOutcome Outcome, string Message, BackdropOutcome Backdrop = BackdropOutcome.NotAttempted);
