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

    public override int Clean(
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

        var removedCount = 0;
        var total = seriesList.Count;

        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (seriesList[i] is not Series series)
                continue;

            if (config.DeleteEmptyShows)
            {
                var episodeCount = LibraryManager.GetCount(
                    new InternalItemsQuery
                    {
                        ParentId = series.Id,
                        Recursive = true,
                        IncludeItemTypes = [BaseItemKind.Episode],
                        IsVirtualItem = false,
                        IsMissing = false,
                        Limit = 0,
                        DtoOptions = new DtoOptions(false)
                    });

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
                        TagItem(
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
                    UntagItem(
                        series,
                        config.HideTag,
                        cancellationToken
                    );
                }
            }
            else if (config.HideInsteadOfDelete)
            {
                UntagItem(series, config.HideTag, cancellationToken);
            }

            if (config.DeleteEmptySeasons)
            {
                removedCount += CleanSeasons(
                    series,
                    config,
                    cancellationToken
                );
            }

            ReportProgress(progress, i + 1, total);
        }

        return removedCount;
    }

    private int CleanSeasons(
        Series series,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var removedCount = 0;
        var seasons = series.GetSeasons(null, new DtoOptions(false));

        foreach (var season in seasons)
        {
            if (season.LocationType != LocationType.FileSystem)
            {
                // only clean seasons which are on the file system
                // skip others
                continue;
            }

            var episodeCount = LibraryManager.GetCount(
                new InternalItemsQuery
                {
                    ParentId = season.Id,
                    IncludeItemTypes = [BaseItemKind.Episode],
                    IsVirtualItem = false,
                    IsMissing = false,
                    Limit = 0,
                    DtoOptions = new DtoOptions(false)
                });

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
                    TagItem(season, config.HideTag, cancellationToken);
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
                UntagItem(season, config.HideTag, cancellationToken);
            }
        }

        return removedCount;
    }
}
