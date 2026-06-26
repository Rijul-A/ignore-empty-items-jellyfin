using Jellyfin.Plugin.IgnoreEmptyFolders.Cleaners;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IgnoreEmptyFolders;

/// <summary>
/// Orchestrator for managing library cleanup tasks.
/// </summary>
public class LibraryCleanupManager(
    ILibraryManager libraryManager,
    IUserManager userManager,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger<LibraryCleanupManager> _logger =
        loggerFactory.CreateLogger<LibraryCleanupManager>();

    private readonly List<IItemCleaner> _cleaners =
    [
        new SeriesCleaner(
            libraryManager,
            loggerFactory.CreateLogger<SeriesCleaner>()),
        new MovieCleaner(
            libraryManager,
            loggerFactory.CreateLogger<MovieCleaner>()),
        new MusicCleaner(
            libraryManager,
            loggerFactory.CreateLogger<MusicCleaner>()),
        new ContainerCleaner(
            libraryManager,
            loggerFactory.CreateLogger<ContainerCleaner>())
    ];

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

        if (config.HideInsteadOfDelete)
        {
            EnsureUsersBlockTag(config.HideTag, cancellationToken);
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

            // Map relative cleaner progress (0-100) to global progress
            var relativeProgress = new Progress<double>(p =>
            {
                var globalProgress =
                    start + (p / 100.0 * cleanerWeight);
                // Normalize if totalWeight != 100
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

    private void EnsureUsersBlockTag(
        string tag,
        CancellationToken cancellationToken
    )
    {
        var users = userManager.GetUsers().ToList();
        _logger.LogInformation(
            "Ignore Empty Folders: EnsureUsersBlockTag found " +
            "{Count} users",
            users.Count);

        foreach (var user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dto = userManager.GetUserDto(user);
            var policy = dto.Policy!;

            _logger.LogInformation(
                "Ignore Empty Folders: Checking user \"{User}\"",
                user.Username);

            if (policy.BlockedTags.Contains(
                    tag,
                    StringComparer.OrdinalIgnoreCase
                ))
                continue;

            policy.BlockedTags = [.. policy.BlockedTags, tag];
            userManager.UpdatePolicyAsync(user.Id, policy)
                .GetAwaiter().GetResult();

            _logger.LogInformation(
                "Ignore Empty Folders: Added blocked tag \"{Tag}\" " +
                "for user \"{User}\"",
                tag,
                user.Username);
        }
    }
}
