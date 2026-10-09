using Jellyfin.Data.Enums;
using Jellyfin.Plugin.IgnoreEmptyFolders.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IgnoreEmptyFolders.Cleaners;

public class MovieCleaner(
    ILibraryManager libraryManager,
    ILogger<MovieCleaner> logger)
    : BaseItemCleaner(libraryManager, logger)
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
        if (config.HideInsteadOfDelete)
        {
            // movies that have the tag and are neither virtual
            // nor missing should be untagged.
            var taggedMovies = LibraryManager.GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Movie],
                    Tags = [config.HideTag],
                    IsVirtualItem = false,
                    IsMissing = false,
                    DtoOptions = new DtoOptions(false)
                });

            foreach (var movie in taggedMovies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await UntagItemAsync(
                    movie,
                    config.HideTag,
                    cancellationToken
                ))
                {
                    changedCount++;
                }
            }
        }

        var movies = LibraryManager.GetItemList(
            new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie],
                IsVirtualItem = true,
                DtoOptions = new DtoOptions(false)
            });

        var missingMovies = LibraryManager.GetItemList(
            new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie],
                IsMissing = true,
                DtoOptions = new DtoOptions(false)
            });

        var allEmptyMovies = movies.Concat(missingMovies).ToList();

        Logger.LogInformation(
            "Ignore Empty Items: Checking {Count} empty movies",
            allEmptyMovies.Count);

        var total = allEmptyMovies.Count;

        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var movie = allEmptyMovies[i];

            if (await HideOrDeleteItemAsync(
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
