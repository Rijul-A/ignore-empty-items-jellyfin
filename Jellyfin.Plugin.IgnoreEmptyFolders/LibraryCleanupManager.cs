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
    public async Task HandleConfigurationChangedAsync(
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
                await MigrateTagAsync(
                    config.PreviousHideTag,
                    string.Empty,
                    cancellationToken);
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
            await MigrateTagAsync(
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

        await EnsureUsersBlockTagAsync(
            config.HideTag,
            cancellationToken);
    }

    /// <summary>
    /// Executes all enabled cleanup tasks.
    /// </summary>
    public async Task CleanLibraryAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return;
        }

        await HandleConfigurationChangedAsync(cancellationToken);

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
        var changedCount = 0;
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

            changedCount += await cleaner.CleanAsync(
                config,
                relativeProgress,
                cancellationToken);
            currentStart += cleanerWeight;
        }

        _logger.LogInformation(
            "Ignore Empty Items: Total changed {Count} empty items",
            changedCount);

        progress.Report(100);
    }

    private async Task MigrateTagAsync(
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
            await libraryManager.UpdateItemAsync(
                item,
                item.GetParent(),
                ItemUpdateType.MetadataEdit,
                cancellationToken);
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
            await userManager.UpdatePolicyAsync(user.Id, policy);

            _logger.LogInformation(
                "Ignore Empty Items: Removed old tag \"{OldTag}\" " +
                "from user \"{User}\"",
                oldTag,
                user.Username);
        }
    }

    private async Task EnsureUsersBlockTagAsync(
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
            await SyncUserTagAsync(
                user, tag, config?.HideTagSkipAdmins ?? true);
        }
    }

    private async Task SyncUserTagAsync(
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
            await userManager.UpdatePolicyAsync(user.Id, policy);
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
            await userManager.UpdatePolicyAsync(user.Id, policy);
            _logger.LogInformation(
                "Ignore Empty Items: Removed blocked tag \"{Tag}\" " +
                "from user \"{User}\"",
                tag,
                user.Username);
        }
    }
}
