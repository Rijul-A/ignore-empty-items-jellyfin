using Jellyfin.Data.Enums;
using Jellyfin.Plugin.IgnoreEmptyFolders.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IgnoreEmptyFolders.Cleaners;

public class SeriesCleaner(
    ILibraryManager libraryManager,
    ILogger<SeriesCleaner> logger)
    : BaseItemCleaner(libraryManager, logger)
{
    public override double Weight => CleanupWeights.Series;

    public override bool IsEnabled(PluginConfiguration config)
    {
        return config.DeleteEmptyShows || config.DeleteEmptySeasons;
    }

    public override async Task<int> CleanAsync(
        PluginConfiguration config,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var seriesList = LibraryManager.GetItemList(
            new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Series],
                DtoOptions = new DtoOptions(false)
                {
                    EnableImages = false
                }
            });

        Logger.LogInformation(
            "Ignore Empty Items: Checking {Count} series",
            seriesList.Count);

        if (seriesList.Count == 0)
        {
            return 0;
        }

        Dictionary<Guid, int> episodeCountsBySeries = [];
        Dictionary<Guid, int> episodeCountsBySeason = [];
        if (config.DeleteEmptyShows || config.DeleteEmptySeasons)
        {
            // A series ownership index requires the physical parent
            // graph.
            // Episode.SeriesId is metadata and may be empty or stale.
            Dictionary<Guid, BaseItem> itemsById = [];
            HashSet<Guid> seriesIds = [];
            if (config.DeleteEmptyShows)
            {
                var libraryItems = LibraryManager.GetItemList(
                    new InternalItemsQuery
                    {
                        DtoOptions = new DtoOptions(false)
                    });

                foreach (var item in libraryItems)
                {
                    itemsById.TryAdd(item.Id, item);
                    if (item is Series series)
                    {
                        seriesIds.Add(series.Id);
                    }
                }

                foreach (var item in seriesList)
                {
                    if (item is Series series)
                    {
                        seriesIds.Add(series.Id);
                        itemsById[series.Id] = series;
                    }
                }
            }

            var episodes = LibraryManager.GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Episode],
                    IsVirtualItem = false,
                    IsMissing = false,
                    DtoOptions = new DtoOptions(false)
                });

            var episodeIds = new HashSet<Guid>();
            var owningSeriesByParentId = new Dictionary<Guid, Guid?>();
            foreach (var episode in episodes.OfType<Episode>())
            {
                if (!episodeIds.Add(episode.Id))
                {
                    continue;
                }

                if (config.DeleteEmptyShows)
                {
                    var seriesId = FindOwningSeriesId(
                        episode.ParentId,
                        itemsById,
                        seriesIds,
                        owningSeriesByParentId);
                    if (seriesId.HasValue)
                    {
                        episodeCountsBySeries[seriesId.Value] =
                            episodeCountsBySeries.GetValueOrDefault(
                                seriesId.Value
                            ) + 1;
                    }
                }

                if (episode.ParentId != Guid.Empty)
                {
                    episodeCountsBySeason[episode.ParentId] =
                        episodeCountsBySeason.GetValueOrDefault(
                            episode.ParentId
                        ) + 1;
                }
            }
        }

        Dictionary<Guid, List<Season>> seasonsBySeries = [];
        if (config.DeleteEmptySeasons)
        {
            var seasons = LibraryManager.GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Season],
                    DtoOptions = new DtoOptions(false)
                });

            var seasonIds = new HashSet<Guid>();
            foreach (var season in seasons.OfType<Season>())
            {
                if (!seasonIds.Add(season.Id))
                {
                    continue;
                }

                var seriesId = season.SeriesId != Guid.Empty
                    ? season.SeriesId
                    : season.ParentId;
                if (seriesId == Guid.Empty)
                {
                    continue;
                }

                if (!seasonsBySeries.TryGetValue(
                    seriesId, out var seriesSeasons
                ))
                {
                    seriesSeasons = [];
                    seasonsBySeries[seriesId] = seriesSeasons;
                }

                seriesSeasons.Add(season);
            }
        }

        var changedCount = 0;
        var total = seriesList.Count;

        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (seriesList[i] is not Series series)
                continue;

            if (config.DeleteEmptyShows)
            {
                var episodeCount =
                    episodeCountsBySeries.GetValueOrDefault(series.Id);

                if (episodeCount == 0)
                {
                    var changed = await HideOrDeleteItemAsync(
                        series,
                        "series",
                        config,
                        cancellationToken);
                    if (changed)
                    {
                        changedCount++;
                    }

                    if (config.HideInsteadOfDelete || changed)
                    {
                        continue;
                    }
                }
                else if (config.HideInsteadOfDelete &&
                    await UntagItemAsync(
                        series,
                        config.HideTag,
                        cancellationToken
                    ))
                {
                    changedCount++;
                }
            }
            else if (config.HideInsteadOfDelete &&
                await UntagItemAsync(
                    series,
                    config.HideTag,
                    cancellationToken
                ))
            {
                changedCount++;
            }

            if (config.DeleteEmptySeasons &&
                seasonsBySeries.TryGetValue(
                    series.Id, out var seriesSeasons
                ))
            {
                changedCount += await CleanSeasonsAsync(
                    config,
                    cancellationToken,
                    seriesSeasons,
                    episodeCountsBySeason
                );
            }

            ReportProgress(progress, i + 1, total);
        }

        return changedCount;
    }

    // Return null for missing or cyclic parent
    // chains; unknown ownership is intentionally
    // not attributed using Episode.SeriesId.
    private static Guid? FindOwningSeriesId(
        Guid parentId,
        IReadOnlyDictionary<Guid, BaseItem> itemsById,
        IReadOnlySet<Guid> seriesIds,
        IDictionary<Guid, Guid?> ownershipCache)
    {
        if (parentId == Guid.Empty)
        {
            return null;
        }

        var path = new List<Guid>();
        var pathIds = new HashSet<Guid>();
        var currentId = parentId;
        Guid? owningSeriesId = null;

        while (currentId != Guid.Empty &&
            pathIds.Add(currentId))
        {
            if (ownershipCache.TryGetValue(
                currentId, out owningSeriesId
            ))
            {
                break;
            }

            path.Add(currentId);
            if (seriesIds.Contains(currentId))
            {
                owningSeriesId = currentId;
                break;
            }

            if (!itemsById.TryGetValue(
                currentId, out var parent
            ))
            {
                break;
            }

            currentId = parent.ParentId;
        }

        foreach (var itemId in path)
        {
            ownershipCache[itemId] = owningSeriesId;
        }

        return owningSeriesId;
    }

    private async Task<int> CleanSeasonsAsync(
        PluginConfiguration config,
        CancellationToken cancellationToken,
        IReadOnlyList<Season> seasons,
        IReadOnlyDictionary<Guid, int> episodeCountsBySeason)
    {
        var changedCount = 0;

        foreach (var season in seasons)
        {
            if (season.LocationType != LocationType.FileSystem)
            {
                // only clean seasons which are on the file system
                // skip others
                continue;
            }

            var episodeCount =
                episodeCountsBySeason.GetValueOrDefault(season.Id);

            if (episodeCount == 0)
            {
                if (await HideOrDeleteItemAsync(
                    season,
                    "season",
                    config,
                    cancellationToken))
                {
                    changedCount++;
                }
            }
            else if (config.HideInsteadOfDelete &&
                await UntagItemAsync(
                    season,
                    config.HideTag,
                    cancellationToken
                ))
            {
                changedCount++;
            }
        }

        return changedCount;
    }
}
