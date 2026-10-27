using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Collections.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Collections;

/// <summary>
/// The Paste Collections plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The plugin id. Never change this.
    /// </summary>
    public const string PluginId = "d7904f73-f7e5-4b0a-ae99-a33d1afd11b1";

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Paste Collections";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginId);

    /// <inheritdoc />
    public override string Description => "Add many movies and shows to a collection at once by pasting a list of titles, or by suggesting franchises from TMDB.";

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "PasteCollections",
                DisplayName = "Paste Collections",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "collections"
            }
        ];
    }
}
