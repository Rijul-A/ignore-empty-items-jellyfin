using Jellyfin.Data.Enums;
using Jellyfin.Plugin.IgnoreEmptyFolders.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Playlists;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IgnoreEmptyFolders.Cleaners;

public class ContainerCleaner(
    ILibraryManager libraryManager,
    ILogger<ContainerCleaner> logger,
    ILinkedChildrenService? linkedChildrenService = null)
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
        var itemsByPath = new Dictionary<string, Guid>(
            StringComparer.OrdinalIgnoreCase);
        var itemsByLibraryId = new Dictionary<string, Guid>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var item in libraryItems)
        {
            itemsById.TryAdd(item.Id, item);
            AddUniqueIdentity(itemsByPath, item.Path, item.Id);
            AddUniqueIdentity(
                itemsByLibraryId,
                item.Id.ToString("N"),
                item.Id);
            AddUniqueIdentity(
                itemsByLibraryId,
                item.Id.ToString(),
                item.Id);
        }

        // Keep the roots available even if a test double or an
        // external content provider omits them from the unrestricted
        // graph query.
        foreach (var container in containers)
        {
            itemsById.TryAdd(container.Id, container);
            AddUniqueIdentity(
                itemsByPath,
                container.Path,
                container.Id);
            AddUniqueIdentity(
                itemsByLibraryId,
                container.Id.ToString("N"),
                container.Id);
            AddUniqueIdentity(
                itemsByLibraryId,
                container.Id.ToString(),
                container.Id);
        }

        var childrenByParentId = new Dictionary<Guid, List<Guid>>();
        foreach (var item in itemsById.Values)
        {
            if (item.ParentId == Guid.Empty)
            {
                continue;
            }

            if (!childrenByParentId.TryGetValue(
                item.ParentId, out var children))
            {
                children = [];
                childrenByParentId[item.ParentId] = children;
            }

            children.Add(item.Id);
        }

        var linkedChildrenByParentId =
            new Dictionary<Guid, List<LinkedReference>>();
        var hydratedLinkedChildrenFolders = new HashSet<Guid>();
        foreach (var item in itemsById.Values)
        {
            if (item is not Folder folder)
            {
                continue;
            }

            if (folder.LinkedChildrenLoaded)
            {
                hydratedLinkedChildrenFolders.Add(item.Id);
                foreach (var linkedChild in folder.LinkedChildren)
                {
                    var childId = ResolveLinkedChildId(
                        linkedChild,
                        folder.ContainingFolderPath,
                        itemsById,
                        itemsByPath,
                        itemsByLibraryId);
                    if (!linkedChildrenByParentId.TryGetValue(
                            item.Id, out var linkedChildren))
                    {
                        linkedChildren = [];
                        linkedChildrenByParentId[item.Id] =
                            linkedChildren;
                    }

                    // An unresolved entry is intentionally retained
                    // as an unknown edge. Dropping it would make a
                    // loaded container look empty and could delete
                    // the container.
                    linkedChildren.Add(
                        new LinkedReference(childId));
                }

                continue;
            }

            // BoxSet and Playlist have historically remained unknown
            // while unloaded. Only ordinary folders use the
            // persistence service.
            if (item is BoxSet or Playlist ||
                linkedChildrenService is null)
            {
                continue;
            }

            try
            {
                var linkedChildIds =
                    linkedChildrenService.GetLinkedChildrenIds(
                        item.Id, null);
                if (linkedChildIds is null)
                {
                    continue;
                }

                hydratedLinkedChildrenFolders.Add(item.Id);
                foreach (var linkedChildId in linkedChildIds)
                {
                    if (!linkedChildrenByParentId.TryGetValue(
                            item.Id, out var linkedChildren))
                    {
                        linkedChildren = [];
                        linkedChildrenByParentId[item.Id] =
                            linkedChildren;
                    }

                    // Keep IDs absent from the bulk snapshot as
                    // unresolved edges so they conservatively
                    // evaluate as Unknown.
                    linkedChildren.Add(
                        new LinkedReference(linkedChildId));
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(
                    ex,
                    "Ignore Empty Items: Unable to hydrate linked " +
                    "children for unloaded folder {FolderId}",
                    item.Id);
            }
        }

        var evaluations =
            new Dictionary<EvaluationKey, EvaluationState>();
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
            var evaluation = Evaluate(
                container.Id,
                viaLinkedChild: false,
                itemsById,
                childrenByParentId,
                linkedChildrenByParentId,
                hydratedLinkedChildrenFolders,
                evaluations);

            if (evaluation == EvaluationState.Unknown)
            {
                Logger.LogDebug(
                    "Ignore Empty Items: Skipping {Type} " +
                    "\"{Name}\" because its contents are unresolved",
                    containerTypeLabel,
                    container.Name);
                ReportProgress(progress, i + 1, total);
                continue;
            }

            if (evaluation == EvaluationState.Empty)
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

    private static void AddUniqueIdentity(
        IDictionary<string, Guid> identities,
        string? value,
        Guid itemId)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!identities.TryAdd(value, itemId) &&
            identities[value] != itemId)
        {
            // Multiple items with one identity cannot be
            // resolved safely.
            identities[value] = Guid.Empty;
        }
    }

    private static Guid? ResolveLinkedChildId(
        LinkedChild linkedChild,
        string? containingFolderPath,
        IReadOnlyDictionary<Guid, BaseItem> itemsById,
        IReadOnlyDictionary<string, Guid> itemsByPath,
        IReadOnlyDictionary<string, Guid> itemsByLibraryId)
    {
        if (linkedChild.ItemId is Guid itemId && itemId != Guid.Empty)
        {
            // Unlike Jellyfin's legacy fallback, a non-empty ItemId
            // absent from this snapshot stays unknown: stale Path or
            // LibraryItemId data must not redirect the link and risk
            // deleting its containing collection.
            return itemsById.ContainsKey(itemId) ? itemId : null;
        }

        // These obsolete fields remain necessary for
        // resolving linked-child data written by older
        // Jellyfin versions.
#pragma warning disable CS0618
        var legacyPath = linkedChild.Path;
        var legacyLibraryItemId = linkedChild.LibraryItemId;
#pragma warning restore CS0618

        if (!string.IsNullOrWhiteSpace(legacyPath))
        {
            var isAbsolutePath =
                Path.IsPathRooted(legacyPath) &&
                (!OperatingSystem.IsWindows() || legacyPath[0] != '\\');
            if (!isAbsolutePath &&
                !legacyPath.StartsWith(
                    @"\\", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(containingFolderPath))
            {
                try
                {
                    var containingPath = Path.GetFullPath(
                        Path.Join(containingFolderPath, legacyPath));
                    if (TryResolveIdentity(
                            itemsByPath,
                            containingPath,
                            out var relativePathId))
                    {
                        return relativePathId;
                    }
                }
                catch (ArgumentException)
                {
                    // Fall through to the raw legacy path.
                }
                catch (PathTooLongException)
                {
                    // Fall through to the raw legacy path.
                }
                catch (NotSupportedException)
                {
                    // Fall through to the raw legacy path.
                }
            }

            if (TryResolveIdentity(
                    itemsByPath, legacyPath, out var pathItemId))
            {
                return pathItemId;
            }

            return null;
        }

        if (!string.IsNullOrWhiteSpace(legacyLibraryItemId) &&
            TryResolveIdentity(
                itemsByLibraryId,
                legacyLibraryItemId,
                out var libraryItemId))
        {
            return libraryItemId;
        }

        return null;
    }

    private static bool TryResolveIdentity(
        IReadOnlyDictionary<string, Guid> identities,
        string identity,
        out Guid itemId)
    {
        if (identities.TryGetValue(identity, out itemId) &&
            itemId != Guid.Empty)
        {
            return true;
        }

        itemId = Guid.Empty;
        return false;
    }

    private static EvaluationState Evaluate(
        Guid rootId,
        bool viaLinkedChild,
        IReadOnlyDictionary<Guid, BaseItem> itemsById,
        IReadOnlyDictionary<Guid, List<Guid>> childrenByParentId,
        IReadOnlyDictionary<Guid, List<LinkedReference>>
            linkedChildrenByParentId,
        IReadOnlySet<Guid> hydratedLinkedChildrenFolders,
        IDictionary<EvaluationKey, EvaluationState> evaluations)
    {
        var rootKey = new EvaluationKey(rootId, viaLinkedChild);
        if (evaluations.TryGetValue(rootKey, out var rootEvaluation))
        {
            return rootEvaluation;
        }

        var active = new HashSet<EvaluationKey>();
        var frames = new Stack<EvaluationFrame>();
        frames.Push(new EvaluationFrame(rootKey));
        active.Add(rootKey);

        while (frames.Count > 0)
        {
            var frame = frames.Peek();
            if (frame.WaitingFor is EvaluationKey waitingFor)
            {
                frame.WaitingFor = null;
                frame.State = Combine(
                    frame.State,
                    evaluations[waitingFor]);
                continue;
            }

            if (!itemsById.TryGetValue(frame.Key.ItemId, out var item))
            {
                frame.State = EvaluationState.Unknown;
                CompleteFrame(frames, active, evaluations);
                continue;
            }

            if (frame.NextEdgeIndex == 0)
            {
                if (IsRealMedia(item))
                {
                    frame.State = EvaluationState.RealMedia;
                    CompleteFrame(frames, active, evaluations);
                    continue;
                }

                if (item is Folder folder &&
                    !folder.LinkedChildrenLoaded &&
                    (item is BoxSet or Playlist ||
                     !hydratedLinkedChildrenFolders.Contains(item.Id)))
                {
                    frame.State = EvaluationState.Unknown;
                }
            }

            var edgeCount =
                (childrenByParentId.TryGetValue(
                    frame.Key.ItemId, out var children)
                    ? children.Count
                    : 0) +
                (linkedChildrenByParentId.TryGetValue(
                    frame.Key.ItemId, out var linkedChildren)
                    ? linkedChildren.Count
                    : 0);
            if (frame.NextEdgeIndex >= edgeCount)
            {
                CompleteFrame(frames, active, evaluations);
                continue;
            }

            var edgeIndex = frame.NextEdgeIndex++;
            Guid childId;
            var isLinkedEdge = false;
            if (children is not null && edgeIndex < children.Count)
            {
                childId = children[edgeIndex];
            }
            else
            {
                isLinkedEdge = true;
                var linkedIndex = edgeIndex - (children?.Count ?? 0);
                var linkedReference = linkedChildren![linkedIndex];
                if (linkedReference.ChildId is not Guid resolvedChildId)
                {
                    frame.State = Combine(
                        frame.State, EvaluationState.Unknown);
                    continue;
                }

                childId = resolvedChildId;
            }

            var childKey = new EvaluationKey(childId, isLinkedEdge);
            if (evaluations.TryGetValue(
                childKey, out var childEvaluation))
            {
                frame.State = Combine(frame.State, childEvaluation);
                continue;
            }

            if (!active.Add(childKey))
            {
                // A cycle is not evidence of emptiness.
                frame.State = Combine(
                    frame.State, EvaluationState.Unknown);
                continue;
            }

            frame.WaitingFor = childKey;
            frames.Push(new EvaluationFrame(childKey));
        }

        return evaluations[rootKey];
    }

    private static void CompleteFrame(
        Stack<EvaluationFrame> frames,
        ISet<EvaluationKey> active,
        IDictionary<EvaluationKey, EvaluationState> evaluations)
    {
        var frame = frames.Pop();
        active.Remove(frame.Key);
        evaluations[frame.Key] = frame.State;
    }

    private static EvaluationState Combine(
        EvaluationState current,
        EvaluationState candidate)
    {
        if (current == EvaluationState.RealMedia ||
            candidate == EvaluationState.RealMedia)
        {
            return EvaluationState.RealMedia;
        }

        if (current == EvaluationState.Unknown ||
            candidate == EvaluationState.Unknown)
        {
            return EvaluationState.Unknown;
        }

        return EvaluationState.Empty;
    }

    private enum EvaluationState
    {
        Empty,
        RealMedia,
        Unknown
    }

    private readonly record struct EvaluationKey(
        Guid ItemId,
        bool ViaLinkedChild);

    private readonly record struct LinkedReference(Guid? ChildId);

    private sealed class EvaluationFrame(EvaluationKey key)
    {
        public EvaluationKey Key { get; } = key;

        public int NextEdgeIndex
        {
            get; set;
        }

        public EvaluationKey? WaitingFor
        {
            get; set;
        }

        public EvaluationState State
        {
            get; set;
        }
    }

}
