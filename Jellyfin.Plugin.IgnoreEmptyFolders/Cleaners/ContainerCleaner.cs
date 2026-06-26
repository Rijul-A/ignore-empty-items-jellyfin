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

    public override int Clean(
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

        var containers = LibraryManager.GetItemList(
            new InternalItemsQuery
            {
                IncludeItemTypes = [.. types],
                DtoOptions = new DtoOptions(false)
            });

        Logger.LogInformation(
            "Ignore Empty Folders: Checking {Count} containers",
            containers.Count);

        var total = containers.Count;
        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var container = containers[i];
            var isEmpty = false;

            var typeName = container.GetType().Name;
            if (typeName == "BoxSet" || typeName == "Playlist")
            {
                if (container is Folder folder)
                {
                    var links = folder.LinkedChildren;
                    if (links.Length == 0)
                    {
                        isEmpty = true;
                    }
                    else
                    {
                        var itemIds = links
                            .Where(l => l.ItemId.HasValue)
                            .Select(l => l.ItemId!.Value)
                            .ToArray();

                        var mediaCount = LibraryManager.GetCount(
                            new InternalItemsQuery
                            {
                                ItemIds = itemIds,
                                IsVirtualItem = false,
                                IsMissing = false,
                                DtoOptions = new DtoOptions(false)
                            });

                        isEmpty = mediaCount == 0;
                    }
                }
            }
            else
            {
                var childCount = LibraryManager.GetCount(
                    new InternalItemsQuery
                    {
                        ParentId = container.Id,
                        Recursive = true,
                        IsVirtualItem = false,
                        IsMissing = false,
                        Limit = 0,
                        DtoOptions = new DtoOptions(false)
                    });
                isEmpty = childCount == 0;
            }

            if (isEmpty)
            {
                if (config.LogDeletions)
                {
                    Logger.LogInformation(
                        "Ignore Empty Folders: Hiding/removing " +
                        "{Type} \"{Name}\"",
                        container.GetType().Name,
                        container.Name);
                }

                if (config.HideInsteadOfDelete)
                {
                    TagItem(
                        container,
                        config.HideTag,
                        cancellationToken
                    );
                    removedCount++;
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
                            "Ignore Empty Folders: Failed to remove " +
                            "{Type} \"{Name}\"",
                            container.GetType().Name,
                            container.Name);
                    }
                }
            }
            else if (config.HideInsteadOfDelete)
            {
                UntagItem(container, config.HideTag, cancellationToken);
            }

            ReportProgress(progress, i + 1, total);
        }

        return removedCount;
    }
}
