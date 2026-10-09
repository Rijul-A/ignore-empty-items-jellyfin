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

        // Discover eligible episodes once and index both TV ownership 
        // levels.
        Dictionary<Guid, int> episodeCountsBySeries = [];
        Dictionary<Guid, int> episodeCountsBySeason = [];
        if (config.DeleteEmptyShows || config.DeleteEmptySeasons)
        {
            var episodes = LibraryManager.GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Episode],
                    IsVirtualItem = false,
                    IsMissing = false,
                    DtoOptions = new DtoOptions(false)
                });

            var episodeIds = new HashSet<Guid>();
            foreach (var episode in episodes.OfType<Episode>())
            {
                if (!episodeIds.Add(episode.Id))
                {
                    continue;
                }

                if (episode.SeriesId != Guid.Empty)
                {
                    episodeCountsBySeries[episode.SeriesId] =
                        episodeCountsBySeries.GetValueOrDefault(
                            episode.SeriesId
                        ) + 1;
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

        var removedCount = 0;
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
                    if (config.LogDeletions)
                    {
                        Logger.LogInformation(
                            "Ignore Empty Items: Hiding/removing " +
                            "series \"{Name}\" - no files",
                            series.Name);
                    }

                    if (config.HideInsteadOfDelete)
                    {
                        await TagItem(
                            series,
                            config.HideTag,
                            cancellationToken
                        );
                        removedCount++;
                        continue;
                    }

                    try
                    {
                        LibraryManager.DeleteItem(
                            series,
                            new DeleteOptions
                            {
                                DeleteFileLocation = false
                            });
                        removedCount++;
                        continue;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(
                            ex,
                            "Ignore Empty Items: Failed to remove " +
                            "series \"{Name}\"",
                            series.Name);
                    }
                }
                else if (config.HideInsteadOfDelete)
                {
                    await UntagItem(
                        series,
                        config.HideTag,
                        cancellationToken
                    );
                }
            }
            else if (config.HideInsteadOfDelete)
            {
                await UntagItem(
                    series,
                    config.HideTag,
                    cancellationToken
                );
            }

            if (config.DeleteEmptySeasons &&
                seasonsBySeries.TryGetValue(
                    series.Id, out var seriesSeasons
                ))
            {
                removedCount += await CleanSeasonsAsync(
                    series,
                    config,
                    cancellationToken,
                    seriesSeasons,
                    episodeCountsBySeason
                );
            }

            ReportProgress(progress, i + 1, total);
        }

        return removedCount;
    }

    private async Task<int> CleanSeasonsAsync(
        Series series,
        PluginConfiguration config,
        CancellationToken cancellationToken,
        IReadOnlyList<Season> seasons,
        IReadOnlyDictionary<Guid, int> episodeCountsBySeason)
    {
        var removedCount = 0;

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
                if (config.LogDeletions)
                {
                    Logger.LogInformation(
                        "Ignore Empty Items: Hiding/removing " +
                        "season \"{Name}\" of series \"{SeriesName}\"",
                        season.Name,
                        series.Name);
                }

                if (config.HideInsteadOfDelete)
                {
                    await TagItem(
                        season,
                        config.HideTag,
                        cancellationToken
                    );
                    removedCount++;
                    continue;
                }

                try
                {
                    LibraryManager.DeleteItem(
                        season,
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
                        "season \"{Name}\"",
                        season.Name);
                }
            }
            else if (config.HideInsteadOfDelete)
            {
                await UntagItem(
                    season,
                    config.HideTag,
                    cancellationToken
                );
            }
        }

        return removedCount;
    }
}
