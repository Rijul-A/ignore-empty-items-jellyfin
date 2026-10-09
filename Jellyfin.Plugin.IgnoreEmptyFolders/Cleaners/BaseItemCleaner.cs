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
    ILogger logger) : IItemCleaner
{
    protected readonly ILibraryManager LibraryManager = libraryManager;
    protected readonly ILogger Logger = logger;

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
        // Jellyfin classifies pathless non-channel items as virtual.
        !string.IsNullOrWhiteSpace(item.Path);

    protected bool HasTag(BaseItem item, string tag) =>
        item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase);

    protected async Task<bool> HideOrDeleteItemAsync(
        BaseItem item,
        string typeLabel,
        PluginConfiguration config,
        CancellationToken cancellationToken
    )
    {
        if (config.LogDeletions)
        {
            Logger.LogInformation(
                "Ignore Empty Items: Hiding/removing {Type} \"{Name}\"",
                typeLabel,
                item.Name);
        }

        if (config.HideInsteadOfDelete)
        {
            return await TagItemAsync(
                item,
                config.HideTag,
                cancellationToken);
        }

        try
        {
            LibraryManager.DeleteItem(
                item,
                new DeleteOptions
                {
                    DeleteFileLocation = false
                });
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "Ignore Empty Items: Failed to hide/remove " +
                "{Type} \"{Name}\"",
                typeLabel,
                item.Name);
            return false;
        }
    }

    protected async Task<bool> TagItemAsync(
        BaseItem item,
        string tag,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(tag))
            return false;
        if (HasTag(item, tag))
            return false;
        var originalTags = item.Tags;
        item.Tags = [.. originalTags, tag];
        try
        {
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
        string tag,
        CancellationToken cancellationToken
    )
    {
        if (!HasTag(item, tag))
            return false;
        var originalTags = item.Tags;
        item.Tags = [.. originalTags
            .Where(t => !t.Equals(
                tag, StringComparison.OrdinalIgnoreCase))];
        try
        {
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

}
