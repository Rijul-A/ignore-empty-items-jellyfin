using Jellyfin.Data.Enums;
using Jellyfin.Plugin.IgnoreEmptyFolders.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IgnoreEmptyFolders.Cleaners;

public class MovieCleaner(
    ILibraryManager libraryManager,
    ILogger<MovieCleaner> logger,
    Func<PluginConfiguration, bool>? isConfigurationCurrent = null)
    : BaseItemCleaner(
        libraryManager,
        logger,
        isConfigurationCurrent)
{

    public override double Weight => CleanupWeights.Movies;

    public override bool IsEnabled(PluginConfiguration config)
    {
        return config.DeleteEmptyMovies;
    }

    public override async Task<int> CleanAsync(
        PluginConfiguration config,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var changedCount = 0;
        var movies = LibraryManager.GetItemList(
            new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie],
                GroupByPresentationUniqueKey = false,
                IncludeOwnedItems = true,
                DtoOptions = new DtoOptions(false)
            });

        Logger.LogInformation(
            "Ignore Empty Items: Checking {Count} movies",
            movies.Count);

        var total = movies.Count;
        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var movie = movies[i];
            if (IsRealMedia(movie))
            {
                if (config.HideInsteadOfDelete &&
                    await UntagItemAsync(
                        movie, config, cancellationToken))
                {
                    changedCount++;
                }
            }
            else if (await HideOrDeleteItemAsync(
                movie,
                "movie",
                config,
                cancellationToken))
            {
                changedCount++;
            }

            ReportProgress(progress, i + 1, total);
        }

        return changedCount;
    }
}
