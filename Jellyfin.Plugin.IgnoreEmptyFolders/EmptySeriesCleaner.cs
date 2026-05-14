using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IgnoreEmptyFolders;

public class EmptySeriesCleaner
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;

    public EmptySeriesCleaner(ILibraryManager libraryManager, ILogger logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public int CleanEmptySeries(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        var deleteEmptyShows = config?.DeleteEmptyShows ?? true;
        var deleteEmptySeasons = config?.DeleteEmptySeasons ?? true;
        var logDeletions = config?.LogDeletions ?? true;

        if (!deleteEmptyShows && !deleteEmptySeasons)
        {
            _logger.LogInformation("Ignore Empty Folders: Both options are disabled, skipping");
            return 0;
        }

        var seriesList = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            DtoOptions = new DtoOptions(false) { EnableImages = false }
        });

        _logger.LogInformation(
            "Ignore Empty Folders: Checking {Count} series (DeleteEmptyShows={DeleteShows}, DeleteEmptySeasons={DeleteSeasons})",
            seriesList.Count, deleteEmptyShows, deleteEmptySeasons);

        var removedCount = 0;
        var total = seriesList.Count;

        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (seriesList[i] is not Series series)
            {
                continue;
            }

            var seriesKey = series.GetPresentationUniqueKey();

            // --- Delete empty TV shows ---
            if (deleteEmptyShows)
            {
                var episodeCount = _libraryManager.GetCount(new InternalItemsQuery
                {
                    AncestorWithPresentationUniqueKey = null,
                    SeriesPresentationUniqueKey = seriesKey,
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    IsVirtualItem = false,
                    IsMissing = false,
                    Limit = 0,
                    DtoOptions = new DtoOptions(false) { EnableImages = false }
                });

                if (episodeCount == 0)
                {
                    if (logDeletions)
                    {
                        _logger.LogInformation(
                            "Ignore Empty Folders: Removing series \"{SeriesName}\" - no video files found",
                            series.Name);
                    }

                    try
                    {
                        _libraryManager.DeleteItem(series, new DeleteOptions { DeleteFileLocation = false });
                        removedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Ignore Empty Folders: Failed to remove series \"{SeriesName}\"", series.Name);
                    }

                    // Series removed — no point checking its seasons
                    progress?.Report((double)(i + 1) / total * 100);
                    continue;
                }
            }

            // --- Delete empty seasons (only reached when the series itself is kept) ---
            if (deleteEmptySeasons)
            {
                var seasonList = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    AncestorWithPresentationUniqueKey = null,
                    SeriesPresentationUniqueKey = seriesKey,
                    IncludeItemTypes = new[] { BaseItemKind.Season },
                    DtoOptions = new DtoOptions(false) { EnableImages = false }
                });

                foreach (var item in seasonList)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (item is not Season season)
                    {
                        continue;
                    }

                    var seasonEpisodeCount = _libraryManager.GetCount(new InternalItemsQuery
                    {
                        AncestorWithPresentationUniqueKey = null,
                        SeriesPresentationUniqueKey = seriesKey,
                        ParentIndexNumber = season.IndexNumber,
                        IncludeItemTypes = new[] { BaseItemKind.Episode },
                        IsVirtualItem = false,
                        IsMissing = false,
                        Limit = 0,
                        DtoOptions = new DtoOptions(false) { EnableImages = false }
                    });

                    if (seasonEpisodeCount > 0)
                    {
                        continue;
                    }

                    if (logDeletions)
                    {
                        _logger.LogInformation(
                            "Ignore Empty Folders: Removing season \"{SeasonName}\" of \"{SeriesName}\" - no video files found",
                            season.Name, series.Name);
                    }

                    try
                    {
                        _libraryManager.DeleteItem(season, new DeleteOptions { DeleteFileLocation = false });
                        removedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Ignore Empty Folders: Failed to remove season \"{SeasonName}\" of \"{SeriesName}\"",
                            season.Name, series.Name);
                    }
                }
            }

            progress?.Report((double)(i + 1) / total * 100);
        }

        _logger.LogInformation("Ignore Empty Folders: Removed {Count} empty items", removedCount);
        return removedCount;
    }
}
