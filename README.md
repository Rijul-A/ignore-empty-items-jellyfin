# Jellyfin Plugin: Ignore Empty Folders

Automatically hides or removes library items that have no media files
associated with them.

> [!IMPORTANT]
> **This plugin only changes Jellyfin's library database and metadata.**
> It **never** deletes or modifies any files or folders on your disk.

## What it does

After every library scan, the plugin identifies items with zero actual
media files and either hides or removes them from Jellyfin, depending on
your configuration. The folders on disk are left untouched — when you
later add media files, the next scan picks them up and the item becomes
visible again (in hide mode) or is recreated (in delete mode).

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
| Hide instead of delete | On | Tag empty items instead of removing them, and automatically block the tag for regular users. Prevents repeated "item added" webhook notifications. |
| Skip admins for hide tag | On | Do not block the hide tag for administrators, allowing them to see hidden items. |
| Hide tag | `plugin-ignore-empty-folders-hidden` | The tag applied to empty items when hide mode is enabled. |

## How it works

The plugin provides two mechanisms:

1. **Post-scan task** — Runs automatically after every library scan.
   Checks your library based on your configuration and hides or removes
   empty items.

2. **Scheduled task** — "Clean Empty Items" appears in
   **Dashboard > Scheduled Tasks**. Runs every 24 hours by default.
   Can also be triggered manually.

Both mechanisms share the same logic and respect the plugin's
configuration. Saving configuration changes also synchronizes the hide
tag for existing users and newly created users.

### How emptiness is determined

Items are considered empty when they contain **zero** relevant media or
child items that are:
- Non-virtual (not metadata-only placeholders)
- Non-missing (not marked as unavailable)

In hide mode, empty items receive the hide tag and are hidden from users
whose policies block that tag. In delete mode, the Jellyfin database
entry is removed, while its on-disk location is preserved.

### What happens on disk

Your folder structure, metadata files, subtitles, and any other files on
disk are never touched. Delete mode only removes the corresponding entry
from Jellyfin's database; hide mode only changes Jellyfin metadata and
user visibility.

## Limitations

- Empty items will briefly appear during a library scan before the
  post-scan task hides or removes them.
- In delete mode, empty items may reappear on the next scan because the
  files and folders remain on disk.
- In hide mode, tagged items remain hidden until they contain media or
  the hide tag is removed. If the plugin is uninstalled without first
  disabling hide mode, the tags and user policies must be cleaned up
  manually.

## Uninstalling (hide mode)

If you used **Hide instead of delete**, the plugin will have tagged
items in your library and added a blocked tag to applicable users. These
tags are not cleaned up automatically if the plugin is uninstalled first.

Before uninstalling:

1. Go to **Dashboard > Plugins > Ignore Empty Folders**
2. Disable **Hide instead of delete** and save. The plugin immediately
   removes its hide tag from items and user policies when the setting is
   saved.

You do not need to run **Clean Empty Items** for this tag cleanup. You
may still run that task manually if you also want to perform a normal
empty-item cleanup before uninstalling.
