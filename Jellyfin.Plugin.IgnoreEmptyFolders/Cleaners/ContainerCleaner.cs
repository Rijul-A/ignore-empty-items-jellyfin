using Jellyfin.Data.Enums;
using Jellyfin.Plugin.IgnoreEmptyFolders.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
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
        var changedCount = 0;
        var containerTypes = new List<BaseItemKind>();
        if (config.DeleteEmptyCollections)
        {
            containerTypes.Add(BaseItemKind.BoxSet);
        }

        if (config.DeleteEmptyFolders)
        {
            containerTypes.Add(BaseItemKind.Folder);
        }

        if (config.DeleteEmptyPlaylists)
        {
            containerTypes.Add(BaseItemKind.Playlist);
        }

        if (containerTypes.Count == 0)
        {
            return 0;
        }

        var containers = LibraryManager.GetItemList(
            new InternalItemsQuery
            {
                IncludeItemTypes = [.. containerTypes],
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
        var libraryItems = LibraryManager.GetItemList(
            new InternalItemsQuery
            {
                DtoOptions = new DtoOptions(false)
            });

        var itemsById = new Dictionary<Guid, BaseItem>();
        foreach (var item in libraryItems)
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
        foreach (var item in libraryItems.Concat(containers))
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
            var containerTypeLabel = container switch
            {
                BoxSet => nameof(BoxSet),
                Playlist => nameof(Playlist),
                _ => nameof(Folder)
            };
            if (container is BoxSet or Playlist &&
                container is Folder folder &&
                !folder.LinkedChildrenLoaded)
            {
                Logger.LogDebug(
                    "Ignore Empty Items: Skipping {Type} " +
                    "\"{Name}\" because linked children are " +
                    "not loaded",
                    containerTypeLabel,
                    container.Name);
                ReportProgress(progress, i + 1, total);
                continue;
            }

            var hasRealMedia = HasRealMediaDescendant(
                container.Id,
                itemsById,
                childrenByParentId,
                linkedChildrenByParentId,
                out var unresolvedLinkedChild);
            if (!hasRealMedia &&
                unresolvedLinkedChild &&
                container is BoxSet or Playlist)
            {
                Logger.LogDebug(
                    "Ignore Empty Items: Skipping {Type} " +
                    "\"{Name}\" because a linked child is unresolved",
                    containerTypeLabel,
                    container.Name);
                ReportProgress(progress, i + 1, total);
                continue;
            }

            if (!hasRealMedia)
            {
                if (await HideOrDeleteItemAsync(
                    container,
                    containerTypeLabel,
                    config,
                    cancellationToken))
                {
                    changedCount++;
                }
            }
            else if (config.HideInsteadOfDelete &&
                await UntagItemAsync(
                    container, config.HideTag, cancellationToken))
            {
                changedCount++;
            }

            ReportProgress(progress, i + 1, total);
        }

        return changedCount;
    }

    private static bool HasRealMediaDescendant(
        Guid rootId,
        IReadOnlyDictionary<Guid, BaseItem> itemsById,
        IReadOnlyDictionary<Guid, List<Guid>> childrenByParentId,
        IReadOnlyDictionary<Guid, List<Guid>> linkedChildrenByParentId,
        out bool unresolvedLinkedChild)
    {
        var pending = new Stack<(Guid ItemId, bool IsLinkedChild)>();
        var visited = new HashSet<Guid>();
        var unresolvedLinkedChildIds = new HashSet<Guid>();
        pending.Push((rootId, false));

        while (pending.Count > 0)
        {
            var (itemId, isLinkedChild) = pending.Pop();
            if (!itemsById.TryGetValue(itemId, out var item))
            {
                if (isLinkedChild)
                {
                    unresolvedLinkedChildIds.Add(itemId);
                }

                continue;
            }

            if (!visited.Add(itemId))
            {
                continue;
            }

            if (IsRealMedia(item))
            {
                unresolvedLinkedChild =
                    unresolvedLinkedChildIds.Count > 0;
                return true;
            }

            if (childrenByParentId.TryGetValue(
                itemId, out var children
            ))
            {
                foreach (var childId in children)
                {
                    pending.Push((childId, false));
                }
            }

            if (linkedChildrenByParentId.TryGetValue(
                    itemId,
                    out var linkedChildren))
            {
                foreach (var childId in linkedChildren)
                {
                    pending.Push((childId, true));
                }
            }
        }

        unresolvedLinkedChild = unresolvedLinkedChildIds.Count > 0;
        return false;
    }

}
