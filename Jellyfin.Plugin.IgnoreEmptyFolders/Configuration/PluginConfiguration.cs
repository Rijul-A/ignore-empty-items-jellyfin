using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.IgnoreEmptyFolders.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Delete TV shows that have no video files anywhere under them.
    /// </summary>
    public bool DeleteEmptyShows { get; set; } = true;

    /// <summary>
    /// Delete individual seasons that contain no video files.
    /// </summary>
    public bool DeleteEmptySeasons { get; set; } = true;

    /// <summary>
    /// Delete movies that have no video file on disk.
    /// </summary>
    public bool DeleteEmptyMovies { get; set; } = true;

    /// <summary>
    /// Delete music artists that have no audio files.
    /// </summary>
    public bool DeleteEmptyMusicArtists { get; set; } = true;

    /// <summary>
    /// Delete music albums that have no audio files.
    /// </summary>
    public bool DeleteEmptyMusicAlbums { get; set; } = true;

    /// <summary>
    /// Delete collections (boxsets) that are empty.
    /// </summary>
    public bool DeleteEmptyCollections { get; set; } = true;

    /// <summary>
    /// Delete folders that have no media files.
    /// </summary>
    public bool DeleteEmptyFolders { get; set; } = true;

    /// <summary>
    /// Delete playlists that are empty.
    /// </summary>
    public bool DeleteEmptyPlaylists { get; set; } = true;

    /// <summary>
    /// Log when items are deleted.
    /// </summary>
    public bool LogDeletions { get; set; } = true;

    /// <summary>
    /// Tag empty items instead of deleting them,
    /// and block the tag for all non-admin users.
    /// </summary>
    public bool HideInsteadOfDelete { get; set; } = true;

    /// <summary>
    /// Tag applied to empty items when HideInsteadOfDelete is enabled.
    /// </summary>
    public string HideTag
    {
        get; set;
    } = "plugin-ignore-empty-folders-hidden";

    /// <summary>
    /// The previously used hide tag, used to clean up
    /// if the tag is renamed.
    /// </summary>
    public string PreviousHideTag { get; set; } = string.Empty;

    /// <summary>
    /// Tracks whether hide mode was active on the last run,
    /// used to detect toggle-off and trigger cleanup.
    /// </summary>
    public bool HideInsteadOfDeleteWasActive { get; set; } = false;

    /// <summary>
    /// Do not add the hide tag to admin users,
    /// allowing them to see hidden items.
    /// </summary>
    public bool HideTagSkipAdmins { get; set; } = true;
}
