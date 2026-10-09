using Jellyfin.Data.Enums;
using Jellyfin.Plugin.IgnoreEmptyFolders.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IgnoreEmptyFolders.Cleaners;

public class MusicCleaner(
    ILibraryManager libraryManager,
    ILogger<MusicCleaner> logger)
    : BaseItemCleaner(libraryManager, logger)
{
    public override double Weight => CleanupWeights.Music;

    public override bool IsEnabled(PluginConfiguration config)
    {
        return config.DeleteEmptyMusicArtists ||
               config.DeleteEmptyMusicAlbums;
    }

    public override async Task<int> CleanAsync(
        PluginConfiguration config,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var removedCount = 0;

        if (config.DeleteEmptyMusicArtists)
        {
            removedCount += await CleanArtistsAsync(
                config, progress, cancellationToken
            );
        }

        if (config.DeleteEmptyMusicAlbums)
        {
            removedCount += await CleanAlbumsAsync(
                config, progress, cancellationToken
            );
        }

        return removedCount;
    }

    private async Task<int> CleanArtistsAsync(
        PluginConfiguration config,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var removedCount = 0;
        var artists = LibraryManager.GetItemList(
            new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.MusicArtist],
                DtoOptions = new DtoOptions(false)
            });

        Logger.LogInformation(
            "Ignore Empty Items: Checking {Count} artists",
            artists.Count);

        var total = artists.Count;
        var processed = 0;
        foreach (var artist in artists)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var albumCount = LibraryManager.GetCount(
                new InternalItemsQuery
                {
                    ParentId = artist.Id,
                    IncludeItemTypes = [BaseItemKind.MusicAlbum],
                    IsVirtualItem = false,
                    IsMissing = false,
                    Limit = 0,
                    DtoOptions = new DtoOptions(false)
                });

            if (albumCount == 0)
            {
                if (config.LogDeletions)
                {
                    Logger.LogInformation(
                        "Ignore Empty Items: Hiding/removing " +
                        "artist \"{Name}\"",
                        artist.Name);
                }

                if (config.HideInsteadOfDelete)
                {
                    if (await TagItemAsync(
                        artist,
                        config.HideTag,
                        cancellationToken
                    ))
                    {
                        removedCount++;
                    }
                }
                else
                {
                    try
                    {
                        LibraryManager.DeleteItem(
                            artist,
                            new DeleteOptions
                            {
                                DeleteFileLocation = false
                            });
                        removedCount++;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(
                            ex,
                        "Ignore Empty Items: Failed to remove " +
                            "artist \"{Name}\"",
                            artist.Name);
                    }
                }
            }
            else if (config.HideInsteadOfDelete)
            {
                await UntagItemAsync(
                    artist,
                    config.HideTag,
                    cancellationToken
                );
            }

            ReportProgress(progress, ++processed, total);
        }

        return removedCount;
    }

    private async Task<int> CleanAlbumsAsync(
        PluginConfiguration config,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var removedCount = 0;
        var albums = LibraryManager.GetItemList(
            new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.MusicAlbum],
                DtoOptions = new DtoOptions(false)
            });

        Logger.LogInformation(
            "Ignore Empty Items: Checking {Count} albums",
            albums.Count);

        var total = albums.Count;
        var processed = 0;
        foreach (var album in albums)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var trackCount = LibraryManager.GetCount(
                new InternalItemsQuery
                {
                    ParentId = album.Id,
                    IncludeItemTypes = [BaseItemKind.Audio],
                    IsVirtualItem = false,
                    IsMissing = false,
                    Limit = 0,
                    DtoOptions = new DtoOptions(false)
                });

            if (trackCount == 0)
            {
                if (config.LogDeletions)
                {
                    Logger.LogInformation(
                    "Ignore Empty Items: Hiding/removing album " +
                        "\"{Name}\"",
                        album.Name);
                }

                if (config.HideInsteadOfDelete)
                {
                    if (await TagItemAsync(
                        album,
                        config.HideTag,
                        cancellationToken
                    ))
                    {
                        removedCount++;
                    }
                }
                else
                {
                    try
                    {
                        LibraryManager.DeleteItem(
                            album,
                            new DeleteOptions
                            {
                                DeleteFileLocation = false
                            });
                        removedCount++;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(
                            ex,
                        "Ignore Empty Items: Failed to remove " +
                            "album \"{Name}\"",
                            album.Name);
                    }
                }
            }
            else if (config.HideInsteadOfDelete)
            {
                await UntagItemAsync(
                    album,
                    config.HideTag,
                    cancellationToken
                );
            }

            ReportProgress(progress, ++processed, total);
        }

        return removedCount;
    }
}
