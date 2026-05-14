using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.IgnoreEmptyFolders.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Delete TV shows that have no video files anywhere under them.</summary>
    public bool DeleteEmptyShows { get; set; } = true;

    /// <summary>Delete individual seasons that contain no video files.</summary>
    public bool DeleteEmptySeasons { get; set; } = true;

    /// <summary>Log when items are deleted.</summary>
    public bool LogDeletions { get; set; } = true;
}
