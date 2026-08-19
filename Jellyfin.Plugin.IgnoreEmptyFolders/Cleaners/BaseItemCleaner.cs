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

    public abstract int Clean(
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

    protected bool HasHideTag(BaseItem item, string tag) =>
        item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase);

    protected void TagItem(
        BaseItem item,
        string tag,
        CancellationToken ct
    )
    {
        if (HasHideTag(item, tag))
            return;
        item.Tags = [.. item.Tags, tag];
        LibraryManager.UpdateItemAsync(
            item,
            item.GetParent(),
            ItemUpdateType.MetadataEdit,
            ct
        ).GetAwaiter().GetResult();
    }

    protected void UntagItem(
        BaseItem item,
        string tag,
        CancellationToken ct
    )
    {
        if (!HasHideTag(item, tag))
            return;
        item.Tags = [.. item.Tags
            .Where(t => !t.Equals(
                tag, StringComparison.OrdinalIgnoreCase))];
        LibraryManager.UpdateItemAsync(
            item,
            item.GetParent(),
            ItemUpdateType.MetadataEdit,
            ct
        ).GetAwaiter().GetResult();
    }
}
