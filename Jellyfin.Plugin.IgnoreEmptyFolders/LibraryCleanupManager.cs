using Jellyfin.Plugin.IgnoreEmptyFolders.Cleaners;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IgnoreEmptyFolders;

/// <summary>
/// Orchestrator for managing library cleanup tasks.
/// </summary>
public class LibraryCleanupManager
{
    private readonly ILogger _logger;
    private readonly List<IItemCleaner> _cleaners;

    public LibraryCleanupManager(
        ILibraryManager libraryManager,
        ILogger logger)
    {
        _logger = logger;
        _cleaners = new List<IItemCleaner>
        {
            new SeriesCleaner(libraryManager, logger),
            new MovieCleaner(libraryManager, logger),
            new MusicCleaner(libraryManager, logger),
            new ContainerCleaner(libraryManager, logger)
        };
    }

    /// <summary>
    /// Executes all enabled cleanup tasks.
    /// </summary>
    public void CleanLibrary(
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return;
        }

        var enabledCleaners = _cleaners
            .Where(c => c.IsEnabled(config))
            .ToList();

        if (enabledCleaners.Count == 0)
        {
            _logger.LogInformation(
                "Ignore Empty Folders: No cleanup tasks enabled.");
            progress.Report(100);
            return;
        }

        var totalWeight = enabledCleaners.Sum(c => c.Weight);
        var removedCount = 0;
        double currentStart = 0;

        foreach (var cleaner in enabledCleaners)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cleanerWeight = cleaner.Weight;
            var start = currentStart;

            var relativeProgress = new Progress<double>(p =>
            {
                var globalProgress = start +
                    (p / 100.0 * cleanerWeight);
                progress.Report(globalProgress * 100.0 / totalWeight);
            });

            removedCount += cleaner.Clean(
                config,
                relativeProgress,
                cancellationToken);

            currentStart += cleanerWeight;
        }

        _logger.LogInformation(
            "Ignore Empty Folders: Total removed {Count} empty items",
            removedCount);

        progress.Report(100);
    }
}
