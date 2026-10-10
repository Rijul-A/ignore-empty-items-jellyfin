using Jellyfin.Plugin.IgnoreEmptyFolders.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IgnoreEmptyFolders.Cleaners;

/// <summary>
/// Base class for item cleaners providing common services.
/// </summary>
public abstract class BaseItemCleaner(
    ILibraryManager libraryManager,
    ILogger logger,
    Func<PluginConfiguration, bool>? isConfigurationCurrent = null)
    : IItemCleaner
{
    protected readonly ILibraryManager LibraryManager = libraryManager;
    protected readonly ILogger Logger = logger;

    private void ThrowIfConfigurationStale(
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isConfigurationCurrent is not null &&
            !isConfigurationCurrent(config))
        {
            throw new OperationCanceledException(
                "Plugin configuration changed during cleanup.");
        }
    }

    public abstract double Weight
    {
        get;
    }

    public abstract bool IsEnabled(PluginConfiguration config);

    public abstract Task<int> CleanAsync(
        PluginConfiguration config,
        IProgress<double>? progress,
        CancellationToken cancellationToken);

    protected void ReportProgress(
        IProgress<double>? progress,
        int current,
        int total)
    {
        if (progress == null || total == 0)
            return;
        var internalProgress = (double)current / total * 100.0;
        progress.Report(internalProgress);
    }

    protected static bool IsRealMedia(BaseItem item) =>
        !item.IsFolder &&
        !item.IsVirtualItem &&
        (!string.IsNullOrWhiteSpace(item.Path) ||
            item.ChannelId != Guid.Empty);

    protected bool HasTag(BaseItem item, string tag) =>
        item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase);

    protected async Task<bool> HideOrDeleteItemAsync(
        BaseItem item,
        string typeLabel,
        PluginConfiguration config,
        CancellationToken cancellationToken
    )
    {
        if (config.HideInsteadOfDelete)
        {
            if (!await TagItemAsync(item, config, cancellationToken))
                return false;

            if (config.LogDeletions)
            {
                Logger.LogInformation(
                    "Ignore Empty Items: Hid {Type} \"{Name}\"",
                    typeLabel,
                    item.Name);
            }

            return true;
        }

        try
        {
            ThrowIfConfigurationStale(config, cancellationToken);
            LibraryManager.DeleteItem(
                item,
                new DeleteOptions
                {
                    DeleteFileLocation = false
                });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Ignore Empty Items: Failed to delete " +
                "{Type} \"{Name}\"",
                typeLabel,
                item.Name);
            return false;
        }

        if (config.LogDeletions)
        {
            Logger.LogInformation(
                "Ignore Empty Items: Deleted {Type} \"{Name}\"",
                typeLabel,
                item.Name);
        }

        return true;
    }

    protected async Task<bool> TagItemAsync(
        BaseItem item,
        PluginConfiguration config,
        CancellationToken cancellationToken
    )
    {
        var tag = config.HideTag;
        if (string.IsNullOrWhiteSpace(tag))
            return false;
        if (HasTag(item, tag))
            return false;
        ThrowIfConfigurationStale(config, cancellationToken);
        var originalTags = item.Tags;
        item.Tags = [.. originalTags, tag];
        try
        {
            ThrowIfConfigurationStale(config, cancellationToken);
            await LibraryManager.UpdateItemAsync(
                item,
                item.GetParent(),
                ItemUpdateType.MetadataEdit,
                cancellationToken
            );
        }
        catch
        {
            item.Tags = originalTags;
            throw;
        }
        return true;
    }

    protected async Task<bool> UntagItemAsync(
        BaseItem item,
        PluginConfiguration config,
        CancellationToken cancellationToken
    )
    {
        var tag = config.HideTag;
        if (!HasTag(item, tag))
            return false;
        ThrowIfConfigurationStale(config, cancellationToken);
        var originalTags = item.Tags;
        item.Tags = [.. originalTags
            .Where(t => !t.Equals(
                tag, StringComparison.OrdinalIgnoreCase))];
        try
        {
            ThrowIfConfigurationStale(config, cancellationToken);
            await LibraryManager.UpdateItemAsync(
                item,
                item.GetParent(),
                ItemUpdateType.MetadataEdit,
                cancellationToken
            );
        }
        catch
        {
            item.Tags = originalTags;
            throw;
        }

        if (config.LogDeletions)
        {
            Logger.LogInformation(
                "Ignore Empty Items: Restored \"{Name}\"",
                item.Name);
        }
        return true;
    }

}
