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

    public override int Clean(
        PluginConfiguration config,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
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
                UntagItem(movie, config.HideTag, cancellationToken);
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

        var removedCount = 0;
        var total = allEmptyMovies.Count;

        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var movie = allEmptyMovies[i];

            if (config.LogDeletions)
            {
                Logger.LogInformation(
                    "Ignore Empty Items: Removing movie \"{Name}\"",
                    movie.Name);
            }

            if (config.HideInsteadOfDelete)
            {
                TagItem(movie, config.HideTag, cancellationToken);
                removedCount++;
            }
            else
            {
                try
                {
                    LibraryManager.DeleteItem(
                        movie,
                        new DeleteOptions
                        {
                            DeleteFileLocation = false
                        }
                    );
                    removedCount++;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(
                        ex,
                        "Ignore Empty Items: Failed to remove " +
                        "movie \"{Name}\"",
                        movie.Name);
                }
            }

            ReportProgress(progress, i + 1, total);
        }

        return removedCount;
    }
}
