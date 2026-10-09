using Jellyfin.Plugin.IgnoreEmptyFolders.Cleaners;
using Jellyfin.Plugin.IgnoreEmptyFolders.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.IgnoreEmptyFolders.Tests;

public sealed class ContainerCleanerTests
{
    [Fact]
    public async Task UnloadedPlaylistIsSkipped()
    {
        var playlist = new MediaBrowser.Controller.Playlists.Playlist
        {
            Id = Guid.NewGuid()
        };
        var library = CreateLibrary([playlist], [playlist]);

        var removed = await CreateCleaner(library).CleanAsync(
            CreateConfig(),
            null,
            CancellationToken.None);

        Assert.Equal(0, removed);
        AssertNoScalarCounts(library);
        AssertBulkGraphLookupOnce(library);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()
            ),
            Times.Never);
        library.Verify(
            m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(),
                It.IsAny<BaseItem>(),
                It.IsAny<ItemUpdateType>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task LoadedEmptyPlaylistIsDeletedWithoutDeletingFiles()
    {
        var playlist = new MediaBrowser.Controller.Playlists.Playlist
        {
            Id = Guid.NewGuid(),
            LinkedChildren = []
        };
        var library = CreateLibrary([playlist], [playlist]);
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var removed = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(1, removed);
        AssertNoScalarCounts(library);
        AssertBulkGraphLookupOnce(library);
        library.Verify(
            m => m.DeleteItem(
                playlist,
                It.Is<DeleteOptions>(o => !o.DeleteFileLocation)),
            Times.Once);
    }

    [Fact]
    public async Task PlaylistWithMediaIsNotDeleted()
    {
        var media = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Movie"
        };
        var playlist = new MediaBrowser.Controller.Playlists.Playlist
        {
            Id = Guid.NewGuid(),
            LinkedChildren =
            [
                new LinkedChild { ItemId = media.Id }
            ]
        };
        var library = CreateLibrary([playlist], [playlist, media]);
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var removed = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(0, removed);
        AssertNoScalarCounts(library);
        AssertBulkGraphLookupOnce(library);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()
            ),
            Times.Never);
    }

    [Fact]
    public async Task EmptyBoxSetIsTaggedInHideMode()
    {
        var boxSet = new BoxSet
        {
            Id = Guid.NewGuid(),
            LinkedChildren = []
        };

        var library = CreateLibrary([boxSet], [boxSet]);

        var removed = await CreateCleaner(library).CleanAsync(
            CreateConfig(),
            null,
            CancellationToken.None);

        Assert.Equal(1, removed);
        Assert.Contains(CreateConfig().HideTag, boxSet.Tags);
        AssertNoScalarCounts(library);
        AssertBulkGraphLookupOnce(library);
        library.Verify(
            m => m.UpdateItemAsync(
                boxSet,
                It.IsAny<BaseItem>(),
                ItemUpdateType.MetadataEdit,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task BoxSetWithMediaIsPreserved()
    {
        var media = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Movie"
        };
        var boxSet = new BoxSet
        {
            Id = Guid.NewGuid(),
            LinkedChildren =
            [
                new LinkedChild { ItemId = media.Id }
            ]
        };
        var library = CreateLibrary([boxSet], [boxSet, media]);
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var removed = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(0, removed);
        AssertNoScalarCounts(library);
        AssertBulkGraphLookupOnce(library);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()
            ),
            Times.Never);
    }
    [Fact]
    public async Task PlaylistWithNestedLinkedMediaIsNotDeleted()
    {
        var linkedFolder = new Folder
        {
            Id = Guid.NewGuid(),
            Name = "Linked folder"
        };
        var media = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Nested linked movie",
            ParentId = linkedFolder.Id
        };
        var playlist = new MediaBrowser.Controller.Playlists.Playlist
        {
            Id = Guid.NewGuid(),
            LinkedChildren =
            [
                new LinkedChild { ItemId = linkedFolder.Id }
            ]
        };
        var library = CreateLibrary(
            [playlist], [playlist, linkedFolder, media]
        );
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var removed = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(0, removed);
        AssertNoScalarCounts(library);
        AssertBulkGraphLookupOnce(library);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()
            ),
            Times.Never);
    }

    [Fact]
    public async Task NestedEmptyFoldersAreDeleted()
    {
        var root = new Folder { Id = Guid.NewGuid(), Name = "Root" };
        var nested = new Folder
        {
            Id = Guid.NewGuid(),
            Name = "Nested empty folder",
            ParentId = root.Id
        };
        var library = CreateLibrary([root], [root, nested]);
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var removed = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(1, removed);
        AssertNoScalarCounts(library);
        AssertBulkGraphLookupOnce(library);
        library.Verify(
            m => m.DeleteItem(
                root,
                It.Is<DeleteOptions>(o => !o.DeleteFileLocation)),
            Times.Once);
    }

    [Fact]
    public async Task MetadataOnlyFoldersAreDeleted()
    {
        var root = new Folder
        {
            Id = Guid.NewGuid(),
            Name = "Metadata-only root"
        };
        var first = new Folder
        {
            Id = Guid.NewGuid(),
            Name = "Metadata folder",
            ParentId = root.Id
        };
        var second = new Folder
        {
            Id = Guid.NewGuid(),
            Name = "Another metadata folder",
            ParentId = root.Id
        };
        var library = CreateLibrary([root], [root, first, second]);
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var removed = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(1, removed);
        AssertNoScalarCounts(library);
        AssertBulkGraphLookupOnce(library);
        library.Verify(
            m => m.DeleteItem(root, It.IsAny<DeleteOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task DeeplyNestedRealMediaPreservesContainingFolder()
    {
        var root = new Folder
        {
            Id = Guid.NewGuid(),
            Name = "Media root"
        };
        var nested = new Folder
        {
            Id = Guid.NewGuid(),
            Name = "Nested folder",
            ParentId = root.Id
        };
        var deep = new Folder
        {
            Id = Guid.NewGuid(),
            Name = "Deeply nested folder",
            ParentId = nested.Id
        };
        var media = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Real movie",
            ParentId = deep.Id
        };
        var library = CreateLibrary(
            [root], [root, nested, deep, media]
        );
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var removed = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(0, removed);
        AssertNoScalarCounts(library);
        AssertBulkGraphLookupOnce(library);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()
            ),
            Times.Never);
    }

    [Fact]
    public async Task FolderWithOnlyVirtualMediaIsDeleted()
    {
        var root = new Folder
        {
            Id = Guid.NewGuid(),
            Name = "Virtual media root"
        };
        var nested = new Folder
        {
            Id = Guid.NewGuid(),
            ParentId = root.Id
        };
        var media = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Virtual movie",
            ParentId = nested.Id,
            IsVirtualItem = true
        };
        var library = CreateLibrary([root], [root, nested, media]);
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var removed = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(1, removed);
        AssertNoScalarCounts(library);
        AssertBulkGraphLookupOnce(library);
        library.Verify(m => m.DeleteItem(
            root,
            It.IsAny<DeleteOptions>()),
            Times.Once
        );
    }

    [Fact]
    public async Task FolderWithOnlyMissingMediaIsDeleted()
    {
        var root = new Folder
        {
            Id = Guid.NewGuid(),
            Name = "Missing media root"
        };
        var nested = new Folder
        {
            Id = Guid.NewGuid(),
            ParentId = root.Id
        };
        var missingEpisode = new Episode
        {
            Id = Guid.NewGuid(),
            Name = "Missing episode",
            ParentId = nested.Id,
            IsVirtualItem = true
        };
        var library = CreateLibrary(
            [root], [root, nested, missingEpisode]
        );
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var removed = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(1, removed);
        AssertNoScalarCounts(library);
        AssertBulkGraphLookupOnce(library);
        library.Verify(
            m => m.DeleteItem(
                root,
                It.IsAny<DeleteOptions>()
                ),
            Times.Once
        );
    }

    private static PluginConfiguration CreateConfig()
    {
        return new PluginConfiguration
        {
            DeleteEmptyCollections = true,
            DeleteEmptyFolders = true,
            DeleteEmptyPlaylists = true,
            LogDeletions = false
        };
    }

    private static Mock<ILibraryManager> CreateLibrary(
        IReadOnlyCollection<BaseItem> containers,
        IReadOnlyCollection<BaseItem> graph)
    {
        var library = new Mock<ILibraryManager>();
        library
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
            {
                if (query.IncludeItemTypes.Length > 0)
                {
                    return containers.ToList();
                }

                Assert.Null(query.IsFolder);
                Assert.Null(query.IsVirtualItem);
                Assert.Null(query.IsMissing);
                return graph.ToList();
            });
        library
            .Setup(m => m.GetCount(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery _) =>
                throw new InvalidOperationException(
                    "ContainerCleaner must not call GetCount."
                ));
        library
            .Setup(m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(),
                It.IsAny<BaseItem>(),
                It.IsAny<ItemUpdateType>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return library;
    }

    private static void AssertNoScalarCounts(
        Mock<ILibraryManager> library
    )
    {
        library.Verify(
            m => m.GetCount(
                It.IsAny<InternalItemsQuery>()
            ),
            Times.Never
        );
    }

    private static void AssertBulkGraphLookupOnce(
        Mock<ILibraryManager> library
    )
    {
        library.Verify(
            m => m.GetItemList(
                It.Is<InternalItemsQuery>(
                    q => q.IncludeItemTypes.Length == 0
                )
            ),
            Times.Once
        );
    }

    private static ContainerCleaner CreateCleaner(
        Mock<ILibraryManager> library
    )
    {
        return new ContainerCleaner(
            library.Object,
            NullLogger<ContainerCleaner>.Instance
        );
    }
}
