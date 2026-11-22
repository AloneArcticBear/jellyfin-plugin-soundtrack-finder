// #claude
using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SoundtrackFinder.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SoundtrackFinder;

/// <summary>
/// Soundtrack Finder plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the current plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Soundtrack Finder";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("5f3c1a2e-9b7d-4c1e-a8f2-6d0e3b9c7a41");

    /// <inheritdoc />
    public override string Description => "Erkennt Songs in Filmen und Serien per Shazam, zeigt sie auf einer Timeline zum Bearbeiten und speichert sie auf Wunsch als Musik in Jellyfin.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace;
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = "Soundtrack Finder",
                EnableInMainMenu = true,
                EmbeddedResourcePath = ns + ".Web.configPage.html"
            },
            new PluginPageInfo
            {
                Name = "SoundtrackFinderJs",
                EmbeddedResourcePath = ns + ".Web.configPage.js"
            }
        };
    }
}
