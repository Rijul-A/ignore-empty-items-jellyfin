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
    ILogger<SeriesCleaner> logger,
    Func<PluginConfiguration, bool>? isConfigurationCurrent = null)
    : BaseItemCleaner(
        libraryManager, logger, isConfigurationCurrent)
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
                GroupByPresentationUniqueKey = false,
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
        HashSet<Guid> uncertainSeriesIds = [];
        HashSet<Guid> uncertainSeasonIds = [];
        var hasUnattributedEpisode = false;
        var hasUnattributedSeasonEpisode = false;
        Dictionary<Guid, BaseItem> itemsById = [];
        HashSet<Guid> seriesIds = [];
        Dictionary<Guid, Guid?> owningSeriesByParentId = [];
        Dictionary<Guid, Guid?> owningSeasonByParentId = [];
        Dictionary<Guid, List<Season>> seasonsBySeries = [];
        if (config.DeleteEmptyShows || config.DeleteEmptySeasons)
        {
            // Metadata IDs can be stale. Index the physical parent
            // graph for both series and season ownership.
            var libraryItems = LibraryManager.GetItemList(
                new InternalItemsQuery
                {
                    IncludeOwnedItems = true,
                    GroupByPresentationUniqueKey = false,
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

            HashSet<Guid> seasonIds = [];
            if (config.DeleteEmptySeasons)
            {
                var seasons = LibraryManager.GetItemList(
                    new InternalItemsQuery
                    {
                        IncludeItemTypes = [BaseItemKind.Season],
                        GroupByPresentationUniqueKey = false,
                        DtoOptions = new DtoOptions(false)
                    });

                foreach (var season in seasons.OfType<Season>())
                {
                    if (!seasonIds.Add(season.Id))
                    {
                        continue;
                    }

                    var seriesId = FindOwningAncestorId(
                        season.ParentId,
                        itemsById,
                        seriesIds,
                        owningSeriesByParentId);
                    if (!seriesId.HasValue)
                    {
                        // Do not assign unresolved seasons by metadata.
                        continue;
                    }

                    if (!seasonsBySeries.TryGetValue(
                        seriesId.Value, out var seriesSeasons
                    ))
                    {
                        seriesSeasons = [];
                        seasonsBySeries[seriesId.Value] = seriesSeasons;
                    }

                    seriesSeasons.Add(season);
                }
            }

            var episodes = LibraryManager.GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Episode],
                    IncludeOwnedItems = true,
                    GroupByPresentationUniqueKey = false,
                    IsVirtualItem = false,
                    IsMissing = false,
                    DtoOptions = new DtoOptions(false)
                });

            var episodeIds = new HashSet<Guid>();
            foreach (var episode in episodes.OfType<Episode>())
            {
                if (!IsRealMedia(episode) ||
                    !episodeIds.Add(episode.Id))
                {
                    continue;
                }

                if (config.DeleteEmptyShows)
                {
                    var seriesId = FindOwningAncestorId(
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
                    else if (episode.SeriesId != Guid.Empty &&
                        seriesIds.Contains(episode.SeriesId))
                    {
                        uncertainSeriesIds.Add(episode.SeriesId);
                    }
                    else
                    {
                        // An unknown owner may belong to any series.
                        hasUnattributedEpisode = true;
                    }
                }

                if (config.DeleteEmptySeasons)
                {
                    var seasonId = FindOwningAncestorId(
                        episode.ParentId,
                        itemsById,
                        seasonIds,
                        owningSeasonByParentId);
                    if (seasonId.HasValue)
                    {
                        episodeCountsBySeason[seasonId.Value] =
                            episodeCountsBySeason.GetValueOrDefault(
                                seasonId.Value
                            ) + 1;
                    }
                    else if (!FindOwningAncestorId(
                        episode.ParentId,
                        itemsById,
                        seriesIds,
                        owningSeriesByParentId).HasValue)
                    {
                        // A known series with no season is not
                        // uncertain; otherwise protect candidates.
                        if (episode.SeasonId != Guid.Empty &&
                            seasonIds.Contains(episode.SeasonId))
                        {
                            uncertainSeasonIds.Add(episode.SeasonId);
                        }
                        else
                        {
                            hasUnattributedSeasonEpisode = true;
                        }
                    }
                }
            }
        }

        var changedCount = 0;
        var total = seriesList.Count;

        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (seriesList[i] is not Series series)
            {
                ReportProgress(progress, i + 1, total);
                continue;
            }

            if (config.DeleteEmptyShows)
            {
                var episodeCount =
                    episodeCountsBySeries.GetValueOrDefault(series.Id);
                var ownershipUncertain =
                    hasUnattributedEpisode ||
                    uncertainSeriesIds.Contains(series.Id);

                if (episodeCount == 0 && !ownershipUncertain)
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
                        ReportProgress(progress, i + 1, total);
                        continue;
                    }
                }
                else if (episodeCount > 0 &&
                    config.HideInsteadOfDelete &&
                    await UntagItemAsync(
                        series,
                        config,
                        cancellationToken
                    ))
                {
                    changedCount++;
                }
            }
            else if (config.HideInsteadOfDelete &&
                await UntagItemAsync(
                    series,
                    config,
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
                    episodeCountsBySeason,
                    uncertainSeasonIds,
                    hasUnattributedSeasonEpisode
                );
            }

            ReportProgress(progress, i + 1, total);
        }

        return changedCount;
    }

    // Return null for missing or cyclic parent chains. Metadata
    // only protects candidates when physical ownership is unknown.
    private static Guid? FindOwningAncestorId(
        Guid parentId,
        IReadOnlyDictionary<Guid, BaseItem> itemsById,
        IReadOnlySet<Guid> ancestorIds,
        IDictionary<Guid, Guid?> ownershipCache)
    {
        if (parentId == Guid.Empty)
        {
            return null;
        }

        var path = new List<Guid>();
        var pathIds = new HashSet<Guid>();
        var currentId = parentId;
        Guid? owningAncestorId = null;

        while (currentId != Guid.Empty &&
            pathIds.Add(currentId))
        {
            if (ownershipCache.TryGetValue(
                currentId, out owningAncestorId
            ))
            {
                break;
            }

            path.Add(currentId);
            if (ancestorIds.Contains(currentId))
            {
                owningAncestorId = currentId;
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
            ownershipCache[itemId] = owningAncestorId;
        }

        return owningAncestorId;
    }

    private async Task<int> CleanSeasonsAsync(
        PluginConfiguration config,
        CancellationToken cancellationToken,
        IReadOnlyList<Season> seasons,
        IReadOnlyDictionary<Guid, int> episodeCountsBySeason,
        IReadOnlySet<Guid> uncertainSeasonIds,
        bool hasUnattributedSeasonEpisode)
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

            if (episodeCount == 0 &&
                !hasUnattributedSeasonEpisode &&
                !uncertainSeasonIds.Contains(season.Id))
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
            else if (episodeCount > 0 &&
                config.HideInsteadOfDelete &&
                await UntagItemAsync(
                    season,
                    config,
                    cancellationToken
                ))
            {
                changedCount++;
            }
        }

        return changedCount;
    }
}
