using Jellyfin.Data.Enums;
using Jellyfin.Plugin.IgnoreEmptyFolders.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IgnoreEmptyFolders.Cleaners;

public class ContainerCleaner(
    ILibraryManager libraryManager,
    ILogger<ContainerCleaner> logger)
    : BaseItemCleaner(libraryManager, logger)
{

    public override double Weight => CleanupWeights.Containers;

    public override bool IsEnabled(PluginConfiguration config)
    {
        return config.DeleteEmptyCollections ||
               config.DeleteEmptyFolders ||
               config.DeleteEmptyPlaylists;
    }

    public override async Task<int> CleanAsync(
        PluginConfiguration config,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var removedCount = 0;
        var types = new List<BaseItemKind>();
        if (config.DeleteEmptyCollections)
        {
            types.Add(BaseItemKind.BoxSet);
        }

        if (config.DeleteEmptyFolders)
        {
            types.Add(BaseItemKind.Folder);
        }

        if (config.DeleteEmptyPlaylists)
        {
            types.Add(BaseItemKind.Playlist);
        }

        if (types.Count == 0)
        {
            return 0;
        }

        var containers = LibraryManager.GetItemList(
            new InternalItemsQuery
            {
                IncludeItemTypes = [.. types],
                DtoOptions = new DtoOptions(false)
            });

        Logger.LogInformation(
            "Ignore Empty Items: Checking {Count} containers",
            containers.Count);
        if (containers.Count == 0)
        {
            return 0;
        }

        // Snapshot the item graph once; each container is indexed in
        // memory.
        var allItems = LibraryManager.GetItemList(
            new InternalItemsQuery
            {
                DtoOptions = new DtoOptions(false)
            });

        var itemsById = new Dictionary<Guid, BaseItem>();
        foreach (var item in allItems)
        {
            itemsById.TryAdd(item.Id, item);
        }

        // Keep the roots available even if a test double or an
        // external content provider omits them from the unrestricted
        // graph query.
        foreach (var container in containers)
        {
            itemsById.TryAdd(container.Id, container);
        }

        var childrenByParentId = new Dictionary<Guid, List<Guid>>();
        foreach (var item in itemsById.Values)
        {
            if (item.ParentId == Guid.Empty)
            {
                continue;
            }

            if (!childrenByParentId.TryGetValue(
                item.ParentId, out var children
            ))
            {
                children = [];
                childrenByParentId[item.ParentId] = children;
            }

            children.Add(item.Id);
        }

        var linkedChildrenByParentId =
            new Dictionary<Guid, List<Guid>>();
        foreach (var item in allItems.Concat(containers))
        {
            if (
                item is not Folder { LinkedChildrenLoaded: true }
                folder
            )
            {
                continue;
            }

            foreach (var linkedChild in folder.LinkedChildren)
            {
                if (linkedChild.ItemId is not Guid childId ||
                    childId == Guid.Empty)
                {
                    continue;
                }

                if (!linkedChildrenByParentId.TryGetValue(
                        item.Id,
                        out var linkedChildren))
                {
                    linkedChildren = [];
                    linkedChildrenByParentId[item.Id] = linkedChildren;
                }

                linkedChildren.Add(childId);
            }
        }

        var total = containers.Count;
        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var container = containers[i];
            var typeName = container.GetType().Name;
            if ((typeName == "BoxSet" || typeName == "Playlist") &&
                container is Folder folder &&
                !folder.LinkedChildrenLoaded)
            {
                Logger.LogDebug(
                    "Ignore Empty Items: Skipping {Type} " +
                    "\"{Name}\" because linked children are " +
                    "not loaded",
                    typeName,
                    container.Name);
                ReportProgress(progress, i + 1, total);
                continue;
            }

            var childCount = CountRealMediaDescendants(
                container.Id,
                itemsById,
                childrenByParentId,
                linkedChildrenByParentId);
            var isEmpty = childCount == 0;

            if (isEmpty)
            {
                if (config.LogDeletions)
                {
                    Logger.LogInformation(
                        "Ignore Empty Items: Hiding/removing " +
                        "{Type} \"{Name}\"",
                        container.GetType().Name,
                        container.Name);
                }

                if (config.HideInsteadOfDelete)
                {
                    if (await TagItemAsync(
                        container,
                        config.HideTag,
                        cancellationToken
                    ))
                    {
                        removedCount++;
                    }
                }
                else
                {
                    try
                    {
                        LibraryManager.DeleteItem(
                            container,
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
                            "Ignore Empty Items: Failed to remove " +
                            "{Type} \"{Name}\"",
                            container.GetType().Name,
                            container.Name);
                    }
                }
            }
            else if (config.HideInsteadOfDelete)
            {
                await UntagItemAsync(
                    container, config.HideTag, cancellationToken);
            }

            ReportProgress(progress, i + 1, total);
        }

        return removedCount;
    }

    private static int CountRealMediaDescendants(
        Guid rootId,
        IReadOnlyDictionary<Guid, BaseItem> itemsById,
        IReadOnlyDictionary<Guid, List<Guid>> childrenByParentId,
        IReadOnlyDictionary<Guid, List<Guid>> linkedChildrenByParentId)
    {
        var pending = new Stack<Guid>();
        var visited = new HashSet<Guid>();
        pending.Push(rootId);

        var count = 0;
        while (pending.Count > 0)
        {
            var itemId = pending.Pop();
            if (!visited.Add(itemId) ||
                !itemsById.TryGetValue(itemId, out var item))
            {
                continue;
            }

            if (!item.IsFolder && !item.IsVirtualItem)
            {
                count++;
            }

            if (childrenByParentId.TryGetValue(
                itemId, out var children
            ))
            {
                foreach (var childId in children)
                {
                    pending.Push(childId);
                }
            }

            if (linkedChildrenByParentId.TryGetValue(
                    itemId,
                    out var linkedChildren))
            {
                foreach (var childId in linkedChildren)
                {
                    pending.Push(childId);
                }
            }
        }

        return count;
    }
}
