using System.Runtime.CompilerServices;

using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.IgnoreEmptyFolders.Cleaners;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IgnoreEmptyFolders;

/// <summary>
/// Orchestrator for managing library cleanup tasks.
/// </summary>
public class LibraryCleanupManager(
    ILibraryManager libraryManager,
    IUserManager userManager,
    ILoggerFactory loggerFactory,
    ILinkedChildrenService? linkedChildrenService = null)
{
    private static readonly SemaphoreSlim
        ConfigurationChangeSemaphore = new(1, 1);

    private static readonly ConditionalWeakTable<
        Plugin,
        MigrationState> MigrationStates = new();

    private static readonly Func<
        Configuration.PluginConfiguration,
        bool> ConfigurationIsCurrent =
        static config =>
            ReferenceEquals(config, Plugin.Instance?.Configuration);

    private readonly ILogger<LibraryCleanupManager> _logger =
        loggerFactory.CreateLogger<LibraryCleanupManager>();

    private readonly List<IItemCleaner> _cleaners =
    [
        new SeriesCleaner(
            libraryManager,
            loggerFactory.CreateLogger<SeriesCleaner>(),
            ConfigurationIsCurrent),
        new MovieCleaner(
            libraryManager,
            loggerFactory.CreateLogger<MovieCleaner>(),
            ConfigurationIsCurrent),
        new MusicCleaner(
            libraryManager,
            loggerFactory.CreateLogger<MusicCleaner>(),
            ConfigurationIsCurrent),
        new ContainerCleaner(
            libraryManager,
            loggerFactory.CreateLogger<ContainerCleaner>(),
            linkedChildrenService,
            ConfigurationIsCurrent)
    ];

    /// <summary>
    /// Handles tag migration and user enrollment
    /// when configuration changes.
    /// </summary>
    public async Task HandleConfigurationChangedAsync(
        CancellationToken cancellationToken)
    {
        await ConfigurationChangeSemaphore.WaitAsync(cancellationToken);
        try
        {
            await ReconcileConfigurationAsync(cancellationToken);
        }
        finally
        {
            ConfigurationChangeSemaphore.Release();
        }
    }

    private async Task ReconcileConfigurationAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var plugin = Plugin.Instance;
            if (plugin is null)
            {
                return;
            }

            var config = plugin.Configuration;
            if (config.HideInsteadOfDelete &&
                string.IsNullOrWhiteSpace(config.HideTag))
            {
                _logger.LogWarning(
                    "Ignore Empty Items: HideTag is empty, " +
                    "skipping hide mode processing");
                return;
            }

            var targetTag = config.HideInsteadOfDelete
                ? config.HideTag
                : string.Empty;
            var state = MigrationStates.GetValue(
                plugin,
                static _ => new MigrationState());
            InitializeState(state, config);

            if (!string.IsNullOrWhiteSpace(config.PreviousHideTag))
            {
                state.KnownTags.Add(config.PreviousHideTag);
            }

            if (!string.IsNullOrWhiteSpace(targetTag))
            {
                state.KnownTags.Add(targetTag);
            }

            var sourceTags = new List<string>();
            if (!string.IsNullOrWhiteSpace(state.LastAppliedTag))
            {
                sourceTags.Add(state.LastAppliedTag);
            }

            if (!string.IsNullOrWhiteSpace(config.PreviousHideTag))
            {
                sourceTags.Add(config.PreviousHideTag);
            }

            sourceTags.AddRange(state.KnownTags);
            sourceTags = sourceTags
                .Where(tag => !tag.Equals(
                    targetTag,
                    StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!string.IsNullOrEmpty(targetTag) &&
                !await EnsureUsersBlockTagAsync(
                    targetTag,
                    config.HideTagSkipAdmins,
                    plugin,
                    config,
                    cancellationToken))
            {
                continue;
            }

            var migrationCompleted = true;
            foreach (var sourceTag in sourceTags)
            {
                if (await MigrateTagAsync(
                        sourceTag,
                        targetTag,
                        plugin,
                        config,
                        cancellationToken))
                {
                    state.LastAppliedTag = targetTag;
                    continue;
                }

                migrationCompleted = false;
                break;
            }

            if (!migrationCompleted)
            {
                continue;
            }

            if (!ReferenceEquals(plugin, Plugin.Instance) ||
                !ReferenceEquals(config, plugin.Configuration) ||
                !IsCurrentConfiguration(plugin, targetTag))
            {
                continue;
            }

            var wasChanged =
                !string.Equals(
                    config.PreviousHideTag,
                    targetTag,
                    StringComparison.Ordinal) ||
                config.HideInsteadOfDeleteWasActive !=
                    !string.IsNullOrEmpty(targetTag);
            if (wasChanged)
            {
                config.PreviousHideTag = targetTag;
                config.HideInsteadOfDeleteWasActive =
                    !string.IsNullOrEmpty(targetTag);
                plugin.SaveConfiguration(config);
            }
            state.LastAppliedTag = targetTag;
            state.KnownTags.Clear();
            return;
        }
    }

    /// <summary>
    /// Executes all enabled cleanup tasks.
    /// </summary>
    public async Task CleanLibraryAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        await ConfigurationChangeSemaphore.WaitAsync(cancellationToken);
        try
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

            await ReconcileConfigurationAsync(cancellationToken);

            // Reconciliation may observe and replace the plugin
            // configuration. Do not run cleaners with the object
            // that was read before it.
            config = Plugin.Instance?.Configuration;
            if (config == null ||
                (config.HideInsteadOfDelete &&
                string.IsNullOrWhiteSpace(config.HideTag)))
            {
                return;
            }

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
                if (!ConfigurationIsCurrent(config))
                {
                    throw new OperationCanceledException(
                        "Plugin configuration changed during cleanup.");
                }

                var cleanerWeight = cleaner.Weight;
                var start = currentStart;

                // Map relative cleaner progress (0-100) to global
                // progress.
                var relativeProgress = new Progress<double>(p =>
                {
                    var globalProgress =
                        start + (p / 100.0 * cleanerWeight);
                    // Normalize if totalWeight != 100.
                    progress.Report(
                        globalProgress * 100.0 / totalWeight);
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
        finally
        {
            ConfigurationChangeSemaphore.Release();
        }
    }

    private static void InitializeState(
        MigrationState state,
        Configuration.PluginConfiguration config)
    {
        if (state.Initialized)
        {
            return;
        }

        state.LastAppliedTag = config.PreviousHideTag;
        if (!string.IsNullOrWhiteSpace(state.LastAppliedTag))
        {
            state.KnownTags.Add(state.LastAppliedTag);
        }

        state.Initialized = true;
    }

    private static bool IsCurrentConfiguration(
        Plugin plugin,
        string targetTag)
    {
        if (!ReferenceEquals(plugin, Plugin.Instance))
        {
            return false;
        }

        var config = plugin.Configuration;
        return string.IsNullOrEmpty(targetTag)
            ? !config.HideInsteadOfDelete
            : config.HideInsteadOfDelete &&
                !string.IsNullOrWhiteSpace(config.HideTag) &&
                config.HideTag.Equals(
                    targetTag,
                    StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> MigrateTagAsync(
        string oldTag,
        string newTag,
        Plugin plugin,
        Configuration.PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        if (oldTag.Equals(newTag, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

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

        var tagQuery = new InternalItemsQuery
        {
            Tags = [oldTag],
            IncludeOwnedItems = true,
            GroupByPresentationUniqueKey = false,
            DtoOptions = new DtoOptions(false)
        };
        var taggedItems = libraryManager.GetItemList(tagQuery);

        if (!ReferenceEquals(config, plugin.Configuration) ||
            !IsCurrentConfiguration(plugin, newTag))
        {
            return false;
        }

        foreach (var item in taggedItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(config, plugin.Configuration) ||
                !IsCurrentConfiguration(plugin, newTag))
            {
                return false;
            }

            var originalTags = item.Tags;
            var tags = originalTags
                .Where(t => !t.Equals(
                    oldTag, StringComparison.OrdinalIgnoreCase));
            item.Tags = string.IsNullOrEmpty(newTag)
                ? [.. tags]
                : [.. tags, newTag];
            try
            {
                await libraryManager.UpdateItemAsync(
                    item,
                    item.GetParent(),
                    ItemUpdateType.MetadataEdit,
                    cancellationToken);
            }
            catch
            {
                item.Tags = originalTags;
                throw;
            }
        }

        _logger.LogInformation(
            "Ignore Empty Items: Updated {Count} items " +
            "during tag migration",
            taggedItems.Count);

        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(config, plugin.Configuration) ||
            !IsCurrentConfiguration(plugin, newTag))
        {
            return false;
        }

        // A failed or deferred update must not expose an item by
        // unblocking its old tag before it is persisted without it.
        if (libraryManager.GetItemList(tagQuery).Count != 0)
        {
            if (!ReferenceEquals(config, plugin.Configuration) ||
                !IsCurrentConfiguration(plugin, newTag))
            {
                return false;
            }

            throw new InvalidOperationException(
                $"Hide tag migration from \"{oldTag}\" to " +
                $"\"{newTag}\" did not persist for all items");
        }

        foreach (var user in userManager.GetUsers())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!ReferenceEquals(config, plugin.Configuration) ||
                !IsCurrentConfiguration(plugin, newTag))
            {
                return false;
            }
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

        return true;
    }

    private async Task<bool> EnsureUsersBlockTagAsync(
        string tag,
        bool skipAdmins,
        Plugin plugin,
        Configuration.PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var users = userManager.GetUsers().ToList();
        _logger.LogInformation(
            "Ignore Empty Items: Syncing hide tag for " +
            "{Count} users",
            users.Count);

        if (!ReferenceEquals(config, plugin.Configuration) ||
            !IsCurrentConfiguration(plugin, tag))
        {
            return false;
        }

        foreach (var user in users)
        {
            if (!ReferenceEquals(config, plugin.Configuration) ||
                !IsCurrentConfiguration(plugin, tag))
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await SyncUserTagAsync(user, tag, skipAdmins);

            if (!ReferenceEquals(config, plugin.Configuration) ||
                !IsCurrentConfiguration(plugin, tag) ||
                config.HideTagSkipAdmins != skipAdmins)
            {
                return false;
            }
        }

        return true;
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
                "for user \"{User}\"",
                tag,
                user.Username);
        }
    }

    private sealed class MigrationState
    {
        public bool Initialized
        {
            get; set;
        }

        public string LastAppliedTag
        {
            get; set;
        } = string.Empty;

        public HashSet<string> KnownTags
        {
            get;
        } = new(StringComparer.OrdinalIgnoreCase);
    }
}
