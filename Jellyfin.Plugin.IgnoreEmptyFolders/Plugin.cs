using Jellyfin.Plugin.IgnoreEmptyFolders.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.IgnoreEmptyFolders;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin? Instance
    {
        get; private set;
    }

    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "Ignore Empty Items";

    public override string Description =>
        "Automatically hides or removes TV shows, seasons, movies, " +
        "music artists, albums, collections, folders and " +
        "playlists that have no media files from the library.";

    public override Guid Id =>
        Guid.Parse("f310dc1b-ea81-4cbc-bdc1-19f32a0542a8");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath =
                    $"{GetType().Namespace}.Configuration." +
                    "configPage.html",
            }
        ];
    }
}
