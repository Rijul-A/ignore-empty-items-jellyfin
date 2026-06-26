using Jellyfin.Plugin.IgnoreEmptyFolders.Cleaners;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
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
    /// Handles tag migration and user enrollment
    /// when configuration changes.
    /// </summary>
    public void HandleConfigurationChanged(
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.HideInsteadOfDelete)
        {
            return;
        }

        if (!string.IsNullOrEmpty(config.PreviousHideTag) &&
            !config.PreviousHideTag.Equals(
                config.HideTag,
                StringComparison.OrdinalIgnoreCase))
        {
            MigrateTag(
                config.PreviousHideTag,
                config.HideTag,
                cancellationToken);
        }

        if (!config.HideTag.Equals(
                config.PreviousHideTag,
                StringComparison.OrdinalIgnoreCase))
        {
            config.PreviousHideTag = config.HideTag;
            // HandleConfigurationChanged is not called via this
            // only via UpdateConfiguration
            Plugin.Instance?.SaveConfiguration(config);
        }

        EnsureUsersBlockTag(config.HideTag, cancellationToken);
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

        HandleConfigurationChanged(cancellationToken);

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

    private void MigrateTag(
        string oldTag,
        string newTag,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Ignore Empty Folders: Migrating hide tag " +
            "from \"{OldTag}\" to \"{NewTag}\"",
            oldTag,
            newTag);

        var taggedItems = libraryManager.GetItemList(
            new InternalItemsQuery
            {
                Tags = [oldTag],
                DtoOptions = new DtoOptions(false)
            });

        foreach (var item in taggedItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            item.Tags = [.. item.Tags
                .Where(t => !t.Equals(
                    oldTag, StringComparison.OrdinalIgnoreCase))];
            libraryManager.UpdateItemAsync(
                item,
                item.GetParent(),
                ItemUpdateType.MetadataEdit,
                cancellationToken)
                .GetAwaiter().GetResult();
        }

        _logger.LogInformation(
            "Ignore Empty Folders: Removed old tag from {Count} items",
            taggedItems.Count);

        foreach (var user in userManager.GetUsers())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dto = userManager.GetUserDto(user);
            var policy = dto.Policy!;

            if (!policy.BlockedTags.Contains(
                    oldTag, StringComparer.OrdinalIgnoreCase))
                continue;

            policy.BlockedTags = [.. policy.BlockedTags
                .Where(t => !t.Equals(
                    oldTag, StringComparison.OrdinalIgnoreCase))];
            userManager.UpdatePolicyAsync(user.Id, policy)
                .GetAwaiter().GetResult();

            _logger.LogInformation(
                "Ignore Empty Folders: Removed old tag \"{OldTag}\" " +
                "from user \"{User}\"",
                oldTag,
                user.Username);
        }
    }

    private void EnsureUsersBlockTag(
        string tag,
        CancellationToken cancellationToken)
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
                    StringComparer.OrdinalIgnoreCase))
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
