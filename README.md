# Jellyfin Plugin: Ignore Empty Folders

Automatically removes library items that have no media files associated
with them.

> [!IMPORTANT]
> **This plugin only removes entries from the Jellyfin database.**
> It **never** deletes or modifies any files or folders on your disk.

## What it does

After every library scan, the plugin identifies items with zero actual
media files and removes them from the Jellyfin database. The folders on
disk are left untouched — when you later add media files, the next scan
picks them up and the item stays.

This is useful if you maintain a folder structure for upcoming content
(with subtitles, NFO files, etc.) but don't want them cluttering your
library until the actual media files are available.

### Supported Item Types

The plugin can be configured to clean up:
- **TV Shows**: Series with no episodes.
- **Seasons**: Individual seasons with no episodes.
- **Movies**: Movie entries with no video file on disk.
- **Music**: Artists or Albums with no audio tracks.
- **Collections**: Empty boxsets.
- **Folders**: Generic folders with no media.
- **Playlists**: Playlists with no items.

## Installation

### From plugin repository (recommended)

Add this repository URL in **Dashboard > Plugins > Repositories**:

```
https://jonathanduvalv.github.io/jellyfin-ignore-empty-folder/repository.json
```

Once added, the plugin will appear in the plugin catalog and can be
installed and updated directly from the Dashboard.

### Manual installation

1. Download the latest `ignore-empty-folders-X.X.X.zip` from
   [Releases](../../releases)
2. Create a plugin directory in your Jellyfin config:
   ```
   /config/plugins/Ignore Empty Folders_{version}/
   ```
3. Extract the ZIP contents (DLL + `meta.json`) into that directory
4. Restart Jellyfin

### Building from source

Requires .NET 9 SDK:

```bash
make build
```

The DLL is output to:
`Jellyfin.Plugin.IgnoreEmptyFolders/bin/Release/net9.0/`.
Copy it to your plugins directory.

## Configuration

Go to **Dashboard > Plugins > Ignore Empty Folders** to configure:

| Setting | Default | Description |
|---------|---------|-------------|
| Delete empty TV shows | On | Remove series with no episodes. |
| Delete empty seasons | On | Remove seasons with no episodes. |
| Delete empty movies | On | Remove movies with no video files. |
| Delete empty artists | On | Remove artists with no audio files. |
| Delete empty albums | On | Remove albums with no audio files. |
| Delete empty collections | On | Remove empty boxsets/collections. |
| Delete empty folders | On | Remove folders with no media files. |
| Delete empty playlists | On | Remove playlists with no items. |
| Log removed items | On | Write a log entry for each removal. |
| Hide instead of delete | Off | Tag empty items instead of removing them, and automatically block the tag for all users. Prevents repeated "item added" webhook notifications. |
| Hide tag | `plugin-empty` | The tag applied to empty items when hide mode is enabled. |

## How it works

The plugin provides two mechanisms:

1. **Post-scan task** — Runs automatically after every library scan.
   Checks your library based on your configuration and removes empty
   items.

2. **Scheduled task** — "Clean Empty Items" appears in
   **Dashboard > Scheduled Tasks**. Runs every 24 hours by default.
   Can also be triggered manually.

Both mechanisms share the same logic and respect the plugin's
configuration.

### What gets removed

Items are removed when they contain **zero** media files that are:
- Non-virtual (not metadata-only placeholders)
- Non-missing (not marked as unavailable)

### What stays on disk

The plugin only removes entries from Jellyfin's database. Your folder
structure, metadata files, subtitles, and any other files on disk are
never touched.

## Limitations

- Empty items will briefly appear during a library scan before the
  post-scan task removes them.
- If the plugin is disabled or uninstalled, empty items will reappear
  on the next scan.

## Uninstalling (hide mode)

If you used **Hide instead of delete**, the plugin will have tagged
items in your library and added a blocked tag to all users. These are
not cleaned up automatically when the plugin is uninstalled.

Before uninstalling:

1. Go to **Dashboard > Plugins > Ignore Empty Folders**
2. Disable **Hide instead of delete** and save
3. Manually trigger **Clean Empty Items** from
   **Dashboard > Scheduled Tasks**

This removes the tag from all items and from all users' blocked tag
lists before the plugin is gone.
