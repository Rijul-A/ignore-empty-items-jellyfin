using Jellyfin.Data.Enums;
using Jellyfin.Plugin.IgnoreEmptyFolders.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IgnoreEmptyFolders.Cleaners;

public class MovieCleaner : BaseItemCleaner
{
    public MovieCleaner(
        ILibraryManager libraryManager,
        ILogger logger)
        : base(libraryManager, logger)
    {
    }

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
        var movies = LibraryManager.GetItemList(
            new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                IsVirtualItem = true,
                DtoOptions = new DtoOptions(false)
            });

        var missingMovies = LibraryManager.GetItemList(
            new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                IsMissing = true,
                DtoOptions = new DtoOptions(false)
            });

        var allEmptyMovies = movies.Concat(missingMovies).ToList();

        Logger.LogInformation(
            "Ignore Empty Folders: Checking {Count} empty movies",
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
                    "Ignore Empty Folders: Removing movie \"{Name}\"",
                    movie.Name);
            }

            try
            {
                LibraryManager.DeleteItem(
                    movie,
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
                    "Ignore Empty Folders: Failed to remove " +
                    "movie \"{Name}\"",
                    movie.Name);
            }

            ReportProgress(progress, i + 1, total);
        }

        return removedCount;
    }
}
