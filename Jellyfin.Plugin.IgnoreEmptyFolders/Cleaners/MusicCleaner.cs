using Jellyfin.Data.Enums;
using Jellyfin.Plugin.IgnoreEmptyFolders.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IgnoreEmptyFolders.Cleaners;

public class MusicCleaner(
    ILibraryManager libraryManager,
    ILogger<MusicCleaner> logger,
    Func<PluginConfiguration, bool>? isConfigurationCurrent = null)
    : BaseItemCleaner(
        libraryManager,
        logger,
        isConfigurationCurrent)
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
        var eligibleAlbums = new Dictionary<Guid, bool>();
        var changedCount = 0;

        if (config.DeleteEmptyMusicArtists)
        {
            changedCount += await CleanArtistsAsync(
                config, progress, eligibleAlbums, cancellationToken
            );
        }

        if (config.DeleteEmptyMusicAlbums)
        {
            changedCount += await CleanAlbumsAsync(
                config, progress, eligibleAlbums, cancellationToken
            );
        }

        return changedCount;
    }

    private async Task<int> CleanArtistsAsync(
        PluginConfiguration config,
        IProgress<double>? progress,
        Dictionary<Guid, bool> eligibleAlbums,
        CancellationToken cancellationToken)
    {
        var changedCount = 0;
        var artists = LibraryManager.GetItemList(
            new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.MusicArtist],
                GroupByPresentationUniqueKey = false,
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

            var hasRealAlbum = HasEligibleAlbum(
                new InternalItemsQuery
                {
                    ParentId = artist.Id,
                    IncludeItemTypes = [BaseItemKind.MusicAlbum],
                    IncludeOwnedItems = true,
                    GroupByPresentationUniqueKey = false,
                    IsVirtualItem = false,
                    DtoOptions = new DtoOptions(false)
                },
                eligibleAlbums,
                cancellationToken);
            if (!hasRealAlbum)
            {
                hasRealAlbum = HasEligibleAlbum(
                    new InternalItemsQuery
                    {
                        ArtistIds = [artist.Id],
                        IncludeItemTypes = [BaseItemKind.MusicAlbum],
                        IncludeOwnedItems = true,
                        GroupByPresentationUniqueKey = false,
                        IsVirtualItem = false,
                        DtoOptions = new DtoOptions(false)
                    },
                    eligibleAlbums,
                    cancellationToken);
            }

            if (!hasRealAlbum)
            {
                // Keep a dedicated album-artist filter even though v12
                // ArtistIds also matches album-artist references.
                hasRealAlbum = HasEligibleAlbum(
                    new InternalItemsQuery
                    {
                        AlbumArtistIds = [artist.Id],
                        IncludeItemTypes = [BaseItemKind.MusicAlbum],
                        IncludeOwnedItems = true,
                        GroupByPresentationUniqueKey = false,
                        IsVirtualItem = false,
                        DtoOptions = new DtoOptions(false)
                    },
                    eligibleAlbums,
                    cancellationToken);
            }

            if (!hasRealAlbum)
            {
                if (await HideOrDeleteItemAsync(
                    artist,
                    "artist",
                    config,
                    cancellationToken))
                {
                    changedCount++;
                }
            }
            else if (config.HideInsteadOfDelete &&
                await UntagItemAsync(
                    artist,
                    config,
                    cancellationToken
                ))
            {
                changedCount++;
            }

            ReportProgress(progress, ++processed, total);
        }

        return changedCount;
    }

    private async Task<int> CleanAlbumsAsync(
        PluginConfiguration config,
        IProgress<double>? progress,
        Dictionary<Guid, bool> eligibleAlbums,
        CancellationToken cancellationToken)
    {
        var changedCount = 0;
        var albums = LibraryManager.GetItemList(
            new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.MusicAlbum],
                GroupByPresentationUniqueKey = false,
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

            if (!HasRealTracks(album, eligibleAlbums))
            {
                if (await HideOrDeleteItemAsync(
                    album,
                    "album",
                    config,
                    cancellationToken))
                {
                    changedCount++;
                }
            }
            else if (config.HideInsteadOfDelete &&
                await UntagItemAsync(
                    album,
                    config,
                    cancellationToken
                ))
            {
                changedCount++;
            }

            ReportProgress(progress, ++processed, total);
        }

        return changedCount;
    }

    private bool HasEligibleAlbum(
        InternalItemsQuery query,
        Dictionary<Guid, bool> eligibleAlbums,
        CancellationToken cancellationToken)
    {
        foreach (var album in LibraryManager.GetItemList(query))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasRealTracks(album, eligibleAlbums))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasRealTracks(
        BaseItem album,
        Dictionary<Guid, bool> eligibleAlbums)
    {
        if (eligibleAlbums.TryGetValue(album.Id, out var eligible))
        {
            return eligible;
        }

        eligible = LibraryManager.GetItemList(
            new InternalItemsQuery
            {
                ParentId = album.Id,
                IncludeItemTypes = [BaseItemKind.Audio],
                IncludeOwnedItems = true,
                GroupByPresentationUniqueKey = false,
                IsVirtualItem = false,
                DtoOptions = new DtoOptions(false)
            }).Any(IsRealMedia);
        eligibleAlbums.Add(album.Id, eligible);
        return eligible;
    }
}
