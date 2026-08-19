using Jellyfin.Database.Implementations.Entities;
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
        if (config == null)
        {
            return;
        }

        if (config.HideInsteadOfDelete &&
            string.IsNullOrWhiteSpace(config.HideTag))
        {
            _logger.LogWarning(
                "Ignore Empty Items: HideTag is empty, " +
                "skipping hide mode processing");
            return;
        }

        if (!config.HideInsteadOfDelete)
        {
            if (config.HideInsteadOfDeleteWasActive &&
                !string.IsNullOrEmpty(config.PreviousHideTag))
            {
                MigrateTag(
                    config.PreviousHideTag,
                    string.Empty,
                    cancellationToken);
                config.PreviousHideTag = string.Empty;
                config.HideInsteadOfDeleteWasActive = false;
                Plugin.Instance?.SaveConfiguration(config);
            }

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

        var needsSave = false;

        if (!config.HideTag.Equals(
                config.PreviousHideTag,
                StringComparison.OrdinalIgnoreCase))
        {
            config.PreviousHideTag = config.HideTag;
            needsSave = true;
        }

        if (!config.HideInsteadOfDeleteWasActive)
        {
            config.HideInsteadOfDeleteWasActive = true;
            needsSave = true;
        }

        if (needsSave)
        {
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
                "Ignore Empty Items: No cleanup tasks enabled.");
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
            "Ignore Empty Items: Total removed {Count} empty items",
            removedCount);

        progress.Report(100);
    }

    private void MigrateTag(
        string oldTag,
        string newTag,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(newTag))
        {
            _logger.LogInformation(
                "Ignore Empty Items: Removing hide tag \"{OldTag}\"",
                oldTag);
        }
        else
        {
            _logger.LogInformation(
                "Ignore Empty Items: Migrating hide tag " +
                "from \"{OldTag}\" to \"{NewTag}\"",
                oldTag,
                newTag);
        }

        var taggedItems = libraryManager.GetItemList(
            new InternalItemsQuery
            {
                Tags = [oldTag],
                DtoOptions = new DtoOptions(false)
            });

        foreach (var item in taggedItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tags = item.Tags
                .Where(t => !t.Equals(
                    oldTag, StringComparison.OrdinalIgnoreCase));
            item.Tags = string.IsNullOrEmpty(newTag)
                ? [.. tags]
                : [.. tags, newTag];
            libraryManager.UpdateItemAsync(
                item,
                item.GetParent(),
                ItemUpdateType.MetadataEdit,
                cancellationToken)
                .GetAwaiter().GetResult();
        }

        _logger.LogInformation(
            "Ignore Empty Items: Updated {Count} items " +
            "during tag migration",
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
                "Ignore Empty Items: Removed old tag \"{OldTag}\" " +
                "from user \"{User}\"",
                oldTag,
                user.Username);
        }
    }

    private void EnsureUsersBlockTag(
        string tag,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        var users = userManager.GetUsers().ToList();
        _logger.LogInformation(
            "Ignore Empty Items: Syncing hide tag for " +
            "{Count} users",
            users.Count);

        foreach (var user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SyncUserTag(user, tag, config?.HideTagSkipAdmins ?? true);
        }
    }

    private void SyncUserTag(
        User user,
        string tag,
        bool skipAdmins)
    {
        var dto = userManager.GetUserDto(user);
        var policy = dto.Policy!;
        var shouldHaveTag = !skipAdmins || !policy.IsAdministrator;
        var hasTag = policy.BlockedTags.Contains(
            tag, StringComparer.OrdinalIgnoreCase);

        if (shouldHaveTag && !hasTag)
        {
            policy.BlockedTags = [.. policy.BlockedTags, tag];
            userManager.UpdatePolicyAsync(user.Id, policy)
                .GetAwaiter().GetResult();
            _logger.LogInformation(
                "Ignore Empty Items: Added blocked tag \"{Tag}\" " +
                "for user \"{User}\"",
                tag,
                user.Username);
        }
        else if (!shouldHaveTag && hasTag)
        {
            policy.BlockedTags = [.. policy.BlockedTags
                .Where(t => !t.Equals(
                    tag, StringComparison.OrdinalIgnoreCase))];
            userManager.UpdatePolicyAsync(user.Id, policy)
                .GetAwaiter().GetResult();
            _logger.LogInformation(
                "Ignore Empty Items: Removed blocked tag \"{Tag}\" " +
                "from user \"{User}\"",
                tag,
                user.Username);
        }
    }
}
