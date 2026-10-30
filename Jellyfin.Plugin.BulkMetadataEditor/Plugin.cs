using System;
using System.Collections.Generic;
using Jellyfin.Plugin.BulkMetadataEditor.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.BulkMetadataEditor;

/// <summary>
/// Bulk metadata editor plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
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
    public override string Name => "Bulk Metadata Editor";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("0bdf47d4-d88e-49c6-bd9e-01e8aeb47bd7");

    /// <inheritdoc />
    public override string Description => "Filter library items and edit metadata for many items at once.";

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
                Name = Name,
                DisplayName = "Bulk Metadata Editor",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.bulkEditor.html",
                EnableInMainMenu = true,
                MenuSection = "main",
                MenuIcon = "edit_note"
            }
        ];
    }
}
