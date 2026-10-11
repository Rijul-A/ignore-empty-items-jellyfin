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
    ILinkedChildrenService? linkedChildrenService = null,
    Func<PluginConfiguration, bool>? isConfigurationCurrent = null)
    : BaseItemCleaner(
        libraryManager, logger, isConfigurationCurrent)
{

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

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
                IncludeOwnedItems = true,
                GroupByPresentationUniqueKey = false,
                DtoOptions = new DtoOptions(false)
            });

        Logger.LogInformation(
            "Ignore Empty Items: Checking {Count} containers",
            containers.Count);
        if (containers.Count == 0)
        {
            return 0;
        }

        // Physical descendants are loaded only for reached parents.
        var itemsById = new Dictionary<Guid, BaseItem>();
        foreach (var container in containers)
        {
            itemsById.TryAdd(container.Id, container);
        }

        var childrenByParentId = new Dictionary<Guid, List<Guid>>();
        var linkedChildrenByParentId =
            new Dictionary<Guid, List<LinkedReference>>();
        var hydratedLinkedChildrenFolders = new HashSet<Guid>();
        var attemptedLinkedChildrenFolders = new HashSet<Guid>();
        var itemsByPath = new Dictionary<string, Guid>(PathComparer);
        var deletedPhysicalItems = new HashSet<Guid>();

        var evaluations =
            new Dictionary<EvaluationKey, EvaluationState>();
        var total = containers.Count;
        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!config.HideInsteadOfDelete &&
                deletedPhysicalItems.Contains(containers[i].Id))
            {
                ReportProgress(progress, i + 1, total);
                continue;
            }

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
                itemsByPath,
                hydratedLinkedChildrenFolders,
                attemptedLinkedChildrenFolders,
                evaluations,
                cancellationToken);

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
                    if (!config.HideInsteadOfDelete)
                    {
                        MarkDeletedPhysicalSubtree(
                            container.Id, childrenByParentId,
                            deletedPhysicalItems);
                    }
                }
            }
            else if (config.HideInsteadOfDelete &&
                await UntagItemAsync(
                    container, config, cancellationToken))
            {
                changedCount++;
            }

            ReportProgress(progress, i + 1, total);
        }

        return changedCount;
    }

    private static void MarkDeletedPhysicalSubtree(
        Guid rootId,
        IDictionary<Guid, List<Guid>> childrenByParentId,
        ISet<Guid> deletedPhysicalItems)
    {
        var pending = new Stack<Guid>();
        pending.Push(rootId);
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (!deletedPhysicalItems.Add(id) ||
                !childrenByParentId.TryGetValue(id, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                pending.Push(child);
            }
        }
    }

    private BaseItem? GetItem(
        Guid itemId,
        IDictionary<Guid, BaseItem> itemsById)
    {
        if (itemId == Guid.Empty)
        {
            return null;
        }

        if (itemsById.TryGetValue(itemId, out var item))
        {
            return item;
        }

        item = LibraryManager.GetItemById(itemId);
        if (item is not null)
        {
            itemsById.TryAdd(itemId, item);
        }

        return item;
    }

    private List<Guid> GetPhysicalChildren(
        Guid parentId,
        IDictionary<Guid, BaseItem> itemsById,
        IDictionary<Guid, List<Guid>> childrenByParentId)
    {
        if (childrenByParentId.TryGetValue(parentId, out var children))
        {
            return children;
        }

        var items = LibraryManager.GetItemList(
            new InternalItemsQuery
            {
                ParentId = parentId,
                IncludeOwnedItems = true,
                GroupByPresentationUniqueKey = false,
                DtoOptions = new DtoOptions(false)
            });
        children = new List<Guid>(items.Count);
        foreach (var child in items)
        {
            if (child.ParentId != parentId)
            {
                continue;
            }

            itemsById.TryAdd(child.Id, child);
            children.Add(child.Id);
        }

        childrenByParentId[parentId] = children;
        return children;
    }

    private Guid? ResolveLinkedChildId(
        LinkedChild linkedChild,
        string? containingFolderPath,
        IDictionary<Guid, BaseItem> itemsById,
        IDictionary<string, Guid> itemsByPath)
    {
        if (linkedChild.ItemId is Guid itemId && itemId != Guid.Empty)
        {
            // A missing ItemId never redirects through stale legacy
            // identities, even when they point to a valid item.
            return GetItem(itemId, itemsById) is not null
                ? itemId
                : null;
        }

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
                    var relativePathId = ResolvePath(
                        containingPath, itemsById, itemsByPath);
                    if (relativePathId is not null)
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

            return ResolvePath(legacyPath, itemsById, itemsByPath);
        }

        if (Guid.TryParse(
                legacyLibraryItemId, out var libraryItemId) &&
            GetItem(libraryItemId, itemsById) is not null)
        {
            return libraryItemId;
        }

        return null;
    }

    private Guid? ResolvePath(
        string path,
        IDictionary<Guid, BaseItem> itemsById,
        IDictionary<string, Guid> itemsByPath)
    {
        if (!itemsByPath.TryGetValue(path, out var itemId))
        {
            var matches = LibraryManager.GetItemList(
                new InternalItemsQuery
                {
                    Path = path,
                    IncludeOwnedItems = true,
                    GroupByPresentationUniqueKey = false,
                    DtoOptions = new DtoOptions(false)
                });
            itemId = Guid.Empty;
            foreach (var item in matches)
            {
                if (!PathComparer.Equals(item.Path, path))
                {
                    continue;
                }

                if (itemId != Guid.Empty && itemId != item.Id)
                {
                    // Duplicate paths cannot identify one target.
                    itemId = Guid.Empty;
                    break;
                }

                itemId = item.Id;
                itemsById.TryAdd(item.Id, item);
            }

            itemsByPath[path] = itemId;
        }

        return itemId != Guid.Empty &&
            GetItem(itemId, itemsById) is not null
            ? itemId
            : null;
    }

    private EvaluationState Evaluate(
        Guid rootId,
        bool viaLinkedChild,
        IDictionary<Guid, BaseItem> itemsById,
        IDictionary<Guid, List<Guid>> childrenByParentId,
        IDictionary<Guid, List<LinkedReference>>
            linkedChildrenByParentId,
        IDictionary<string, Guid> itemsByPath,
        ISet<Guid> hydratedLinkedChildrenFolders,
        ISet<Guid> attemptedLinkedChildrenFolders,
        IDictionary<EvaluationKey, EvaluationState> evaluations,
        CancellationToken cancellationToken)
    {
        var rootKey = new EvaluationKey(rootId, viaLinkedChild);
        if (evaluations.TryGetValue(rootKey, out var rootEvaluation))
        {
            return rootEvaluation;
        }

        // Unknown can depend on an active ancestor. Keep it local
        // until the root's entire reachable graph is evaluated.
        var unknownEvaluations = new HashSet<EvaluationKey>();
        var active = new HashSet<EvaluationKey>();
        var frames = new Stack<EvaluationFrame>();
        frames.Push(new EvaluationFrame(rootKey));
        active.Add(rootKey);

        while (frames.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = frames.Peek();
            if (frame.WaitingFor is EvaluationKey waitingFor)
            {
                frame.WaitingFor = null;
                frame.State = Combine(
                    frame.State,
                    unknownEvaluations.Contains(waitingFor)
                        ? EvaluationState.Unknown
                        : evaluations[waitingFor]);
                continue;
            }

            if (frame.State == EvaluationState.RealMedia)
            {
                CompleteFrame(
                    frames, active, evaluations, unknownEvaluations);
                continue;
            }

            if (GetItem(frame.Key.ItemId, itemsById) is not { } item)
            {
                frame.State = EvaluationState.Unknown;
                CompleteFrame(
                    frames, active, evaluations, unknownEvaluations);
                continue;
            }

            if (frame.NextEdgeIndex == 0 && IsRealMedia(item))
            {
                frame.State = EvaluationState.RealMedia;
                CompleteFrame(
                    frames, active, evaluations, unknownEvaluations);
                continue;
            }

            var children = GetPhysicalChildren(
                frame.Key.ItemId, itemsById, childrenByParentId);
            var physicalCount = children.Count;
            if (frame.NextEdgeIndex >= physicalCount &&
                !frame.LinkedChildrenReady)
            {
                frame.LinkedChildrenReady = true;
                if (item is Folder loadedFolder &&
                    loadedFolder.LinkedChildrenLoaded)
                {
                    var loadedLinks = new List<LinkedReference>();
                    foreach (var linkedChild in
                        loadedFolder.LinkedChildren)
                    {
                        loadedLinks.Add(new LinkedReference(
                            null, linkedChild,
                            loadedFolder.ContainingFolderPath,
                            Resolved: false));
                    }

                    linkedChildrenByParentId[item.Id] = loadedLinks;
                    hydratedLinkedChildrenFolders.Add(item.Id);
                }

                if (item is Folder folder &&
                    !folder.LinkedChildrenLoaded)
                {
                    if (linkedChildrenService is not null &&
                        attemptedLinkedChildrenFolders.Add(item.Id))
                    {
                        try
                        {
                            cancellationToken
                                .ThrowIfCancellationRequested();
                            var linkedChildIds =
                                linkedChildrenService
                                    .GetLinkedChildrenIds(
                                        item.Id, null);
                            cancellationToken
                                .ThrowIfCancellationRequested();
                            if (linkedChildIds is not null)
                            {
                                var hydratedLinks =
                                    new List<LinkedReference>();
                                foreach (var linkedChildId
                                    in linkedChildIds)
                                {
                                    // Missing IDs remain unknown.
                                    hydratedLinks.Add(
                                        new LinkedReference(
                                            linkedChildId));
                                }

                                linkedChildrenByParentId[item.Id] =
                                    hydratedLinks;
                                hydratedLinkedChildrenFolders.Add(
                                    item.Id);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            cancellationToken
                                .ThrowIfCancellationRequested();
                            Logger.LogDebug(
                                ex,
                                "Ignore Empty Items: Unable to " +
                                "hydrate linked children for " +
                                "unloaded folder {FolderId}",
                                item.Id);
                        }
                    }

                    if (!hydratedLinkedChildrenFolders.Contains(
                            item.Id))
                    {
                        frame.State = EvaluationState.Unknown;
                    }
                }
            }

            linkedChildrenByParentId.TryGetValue(
                frame.Key.ItemId, out var linkedChildren);
            if (frame.NextEdgeIndex >=
                physicalCount + (linkedChildren?.Count ?? 0))
            {
                CompleteFrame(
                    frames, active, evaluations, unknownEvaluations);
                continue;
            }

            var edgeIndex = frame.NextEdgeIndex++;
            Guid childId;
            var isLinkedEdge = false;
            if (edgeIndex < physicalCount)
            {
                childId = children[edgeIndex];
            }
            else
            {
                isLinkedEdge = true;
                var linkedIndex = edgeIndex - physicalCount;
                var linkedReference = linkedChildren![linkedIndex];
                if (!linkedReference.Resolved)
                {
                    linkedReference = linkedReference with
                    {
                        ChildId = ResolveLinkedChildId(
                            linkedReference.LoadedChild!,
                            linkedReference.ContainingFolderPath,
                            itemsById,
                            itemsByPath),
                        Resolved = true
                    };
                    linkedChildren[linkedIndex] = linkedReference;
                }

                if (linkedReference.ChildId
                    is not Guid resolvedChildId)
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

            if (unknownEvaluations.Contains(childKey))
            {
                frame.State = Combine(
                    frame.State, EvaluationState.Unknown);
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

        if (unknownEvaluations.Contains(rootKey))
        {
            // A media-free root has reached every local unknown
            // node without finding real media. Those unknowns are
            // now stable across roots, including cyclic roots.
            foreach (var key in unknownEvaluations)
            {
                evaluations[key] = EvaluationState.Unknown;
            }

            return EvaluationState.Unknown;
        }

        return evaluations[rootKey];
    }

    private static void CompleteFrame(
        Stack<EvaluationFrame> frames,
        ISet<EvaluationKey> active,
        IDictionary<EvaluationKey, EvaluationState> evaluations,
        ISet<EvaluationKey> unknownEvaluations)
    {
        var frame = frames.Pop();
        active.Remove(frame.Key);
        if (frame.State == EvaluationState.Unknown)
        {
            unknownEvaluations.Add(frame.Key);
        }
        else
        {
            evaluations[frame.Key] = frame.State;
        }
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

    private readonly record struct LinkedReference(
        Guid? ChildId,
        LinkedChild? LoadedChild = null,
        string? ContainingFolderPath = null,
        bool Resolved = true);

    private sealed class EvaluationFrame(EvaluationKey key)
    {
        public EvaluationKey Key { get; } = key;

        public int NextEdgeIndex
        {
            get; set;
        }

        public bool LinkedChildrenReady
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
