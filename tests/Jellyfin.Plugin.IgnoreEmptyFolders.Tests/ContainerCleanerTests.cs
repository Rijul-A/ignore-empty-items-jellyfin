using Jellyfin.Plugin.IgnoreEmptyFolders.Cleaners;
using Jellyfin.Plugin.IgnoreEmptyFolders.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
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

        var changedCount = await CreateCleaner(library).CleanAsync(
            CreateConfig(),
            null,
            CancellationToken.None);

        Assert.Equal(0, changedCount);
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

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(1, changedCount);
        AssertNoScalarCounts(library);
        AssertBulkGraphLookupOnce(library);
        library.Verify(
            m => m.DeleteItem(
                playlist,
                It.Is<DeleteOptions>(o => !o.DeleteFileLocation)),
            Times.Once);
    }

    [Fact]
    public async Task FailedDeleteIsNotCounted()
    {
        var playlist = new MediaBrowser.Controller.Playlists.Playlist
        {
            Id = Guid.NewGuid(),
            LinkedChildren = []
        };
        var library = CreateLibrary([playlist], [playlist]);
        library
            .Setup(m => m.DeleteItem(
                playlist, It.IsAny<DeleteOptions>()
            ))
            .Throws(new InvalidOperationException("Delete failed."));
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(0, changedCount);
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
            Name = "Movie",
            Path = "/media/playlist-movie.mkv"
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

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(0, changedCount);
        AssertNoScalarCounts(library);
        AssertBulkGraphLookupOnce(library);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()
            ),
            Times.Never);
    }

    [Fact]
    public async Task UnresolvedPlaylistChildIsPreserved()
    {
        var playlist = new MediaBrowser.Controller.Playlists.Playlist
        {
            Id = Guid.NewGuid(),
            LinkedChildren =
            [
                new LinkedChild { ItemId = Guid.NewGuid() }
            ]
        };
        var library = CreateLibrary([playlist], [playlist]);
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(0, changedCount);
        Assert.DoesNotContain(config.HideTag, playlist.Tags);
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
    public async Task EmptyBoxSetIsTaggedInHideMode()
    {
        var boxSet = new BoxSet
        {
            Id = Guid.NewGuid(),
            LinkedChildren = []
        };

        var library = CreateLibrary([boxSet], [boxSet]);

        var changedCount = await CreateCleaner(library).CleanAsync(
            CreateConfig(),
            null,
            CancellationToken.None);

        Assert.Equal(1, changedCount);
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
    public async Task
        AlreadyTaggedEmptyBoxSetIsNotCountedOrUpdatedInHideMode()
    {
        var config = CreateConfig();
        var boxSet = new BoxSet
        {
            Id = Guid.NewGuid(),
            LinkedChildren = [],
            Tags = [config.HideTag]
        };
        var library = CreateLibrary([boxSet], [boxSet]);

        var changedCount = await CreateCleaner(library).CleanAsync(
            config,
            null,
            CancellationToken.None);

        Assert.Equal(0, changedCount);
        Assert.Contains(config.HideTag, boxSet.Tags);
        AssertNoScalarCounts(library);
        AssertBulkGraphLookupOnce(library);
        library.Verify(
            m => m.UpdateItemAsync(
                boxSet,
                It.IsAny<BaseItem>(),
                ItemUpdateType.MetadataEdit,
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TaggedBoxSetWithMediaIsUntaggedAndCounted()
    {
        var config = CreateConfig();
        var media = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Previously hidden movie",
            Path = "/media/previously-hidden-movie.mkv"
        };
        var boxSet = new BoxSet
        {
            Id = Guid.NewGuid(),
            LinkedChildren =
            [
                new LinkedChild { ItemId = media.Id }
            ],
            Tags = [config.HideTag]
        };
        var library = CreateLibrary([boxSet], [boxSet, media]);

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(1, changedCount);
        Assert.DoesNotContain(config.HideTag, boxSet.Tags);
        AssertNoScalarCounts(library);
        AssertBulkGraphLookupOnce(library);
        library.Verify(
            m => m.UpdateItemAsync(
                boxSet,
                It.IsAny<BaseItem>(),
                ItemUpdateType.MetadataEdit,
                It.IsAny<CancellationToken>()),
            Times.Once);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()
            ),
            Times.Never);
    }

    [Fact]
    public async Task FailedHideUpdatePropagatesAndRollsBack()
    {
        var config = CreateConfig();
        var boxSet = new BoxSet
        {
            Id = Guid.NewGuid(),
            LinkedChildren = []
        };
        var library = CreateLibrary([boxSet], [boxSet]);
        library
            .Setup(m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(),
                It.IsAny<BaseItem>(),
                It.IsAny<ItemUpdateType>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(
                new InvalidOperationException("Update failed.")
            );

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateCleaner(library).CleanAsync(
                config, null, CancellationToken.None));
        Assert.DoesNotContain(config.HideTag, boxSet.Tags);
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
    public async Task FailedUnhideUpdatePreservesTagAndCount()
    {
        var config = CreateConfig();
        var media = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Visible movie",
            Path = "/media/visible-movie.mkv"
        };
        var boxSet = new BoxSet
        {
            Id = Guid.NewGuid(),
            LinkedChildren =
            [
                new LinkedChild { ItemId = media.Id }
            ],
            Tags = [config.HideTag]
        };
        var library = CreateLibrary([boxSet], [boxSet, media]);
        library
            .Setup(m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(),
                It.IsAny<BaseItem>(),
                It.IsAny<ItemUpdateType>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(
                new InvalidOperationException("Update failed.")
            );

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateCleaner(library).CleanAsync(
                config, null, CancellationToken.None));
        Assert.Contains(config.HideTag, boxSet.Tags);
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
            Name = "Movie",
            Path = "/media/box-set-movie.mkv"
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

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(0, changedCount);
        AssertNoScalarCounts(library);
        AssertBulkGraphLookupOnce(library);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()
            ),
            Times.Never);
    }

    [Fact]
    public async Task LoadedBoxSetWithUnresolvedLinkedChildIsPreserved()
    {
        var boxSet = new BoxSet
        {
            Id = Guid.NewGuid(),
            LinkedChildren =
            [
                new LinkedChild { ItemId = Guid.NewGuid() }
            ]
        };
        var library = CreateLibrary([boxSet], [boxSet]);
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(0, changedCount);
        Assert.DoesNotContain(config.HideTag, boxSet.Tags);
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
            ParentId = linkedFolder.Id,
            Path = "/media/nested-linked-movie.mkv"
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

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(0, changedCount);
        AssertNoScalarCounts(library);
        AssertBulkGraphLookupOnce(library);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()
            ),
            Times.Never);
    }

    [Fact]
    public async Task LinkedChildPathResolvesToIndexedMedia()
    {
        var media = new Movie
        {
            Id = Guid.NewGuid(),
            Path = "/media/legacy-linked-movie.mkv"
        };
        var playlist = new MediaBrowser.Controller.Playlists.Playlist
        {
            Id = Guid.NewGuid(),
            LinkedChildren =
            [
                CreateLegacyLinkedChild(path: media.Path)
            ]
        };
        var library = CreateLibrary([playlist], [playlist, media]);
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None);

        Assert.Equal(0, changedCount);
        AssertNoScalarCounts(library);
        AssertBulkGraphLookupOnce(library);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StaleItemIdDoesNotRedirectToEmptyLegacyPath(
        bool hideInsteadOfDelete)
    {
        var config = CreateConfig();
        config.HideInsteadOfDelete = hideInsteadOfDelete;
        var emptyFolder = new Folder
        {
            Id = Guid.NewGuid(),
            Path = "/media/empty-legacy-linked-folder",
            LinkedChildren = []
        };
        var boxSet = new BoxSet
        {
            Id = Guid.NewGuid(),
            LinkedChildren =
            [
                CreateLegacyLinkedChild(
                    itemId: Guid.NewGuid(),
                    path: emptyFolder.Path,
                    libraryItemId: emptyFolder.Id.ToString("N"))
            ],
            Tags = [config.HideTag]
        };
        var library = CreateLibrary(
            [boxSet], [boxSet, emptyFolder]);

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None);

        Assert.Equal(0, changedCount);
        Assert.Contains(config.HideTag, boxSet.Tags);
        library.Verify(
            m => m.UpdateItemAsync(
                boxSet,
                It.IsAny<BaseItem>(),
                It.IsAny<ItemUpdateType>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task EmptyItemIdUsesLegacyPathToRestoreHiddenBoxSet()
    {
        var config = CreateConfig();
        var media = new Movie
        {
            Id = Guid.NewGuid(),
            Path = "/media/empty-id-linked-movie.mkv"
        };
        var boxSet = new BoxSet
        {
            Id = Guid.NewGuid(),
            LinkedChildren =
            [
                CreateLegacyLinkedChild(
                    itemId: Guid.Empty, path: media.Path)
            ],
            Tags = [config.HideTag]
        };
        var library = CreateLibrary([boxSet], [boxSet, media]);

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None);

        Assert.Equal(1, changedCount);
        Assert.DoesNotContain(config.HideTag, boxSet.Tags);
        library.Verify(
            m => m.UpdateItemAsync(
                boxSet,
                It.IsAny<BaseItem>(),
                ItemUpdateType.MetadataEdit,
                It.IsAny<CancellationToken>()),
            Times.Once);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task
        LinkedChildPathTakesPrecedenceOverLegacyLibraryItemId()
    {
        var emptyFolder = new Folder
        {
            Id = Guid.NewGuid(),
            Path = "/media/empty-linked-folder"
        };
        var media = new Movie
        {
            Id = Guid.NewGuid(),
            Path = "/media/legacy-preferred-movie.mkv"
        };
        var playlist = new MediaBrowser.Controller.Playlists.Playlist
        {
            Id = Guid.NewGuid(),
            LinkedChildren =
            [
                CreateLegacyLinkedChild(
                    path: media.Path,
                    libraryItemId: emptyFolder.Id.ToString("N"))
            ]
        };
        var library = CreateLibrary(
            [playlist], [playlist, emptyFolder, media]);
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None);

        Assert.Equal(0, changedCount);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task
        RelativeLinkedChildPathResolvesAgainstContainingFolder()
    {
        var linkedFolder = new Folder
        {
            Id = Guid.NewGuid(),
            Path = "/media/relative-linked-folder",
            LinkedChildren =
            [
                CreateLegacyLinkedChild(
                    path: "relative-linked-movie.mkv")
            ]
        };
        var rawPathFolder = new Folder
        {
            Id = Guid.NewGuid(),
            Path = "relative-linked-movie.mkv"
        };
        var media = new Movie
        {
            Id = Guid.NewGuid(),
            Path =
                "/media/relative-linked-folder/" +
                "relative-linked-movie.mkv"
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
            [playlist], [playlist, linkedFolder, rawPathFolder, media]);
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None);

        Assert.Equal(0, changedCount);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task LinkedChildLibraryItemIdResolvesToIndexedMedia()
    {
        var media = new Movie
        {
            Id = Guid.NewGuid(),
            Path = "/media/library-id-linked-movie.mkv"
        };
        var playlist = new MediaBrowser.Controller.Playlists.Playlist
        {
            Id = Guid.NewGuid(),
            LinkedChildren =
            [
                CreateLegacyLinkedChild(
                    libraryItemId: media.Id.ToString("N"))
            ]
        };
        var library = CreateLibrary([playlist], [playlist, media]);
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None);

        Assert.Equal(0, changedCount);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task LinkedUnloadedContainerIsPreserved()
    {
        var linkedFolder = new Folder
        {
            Id = Guid.NewGuid()
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
            [playlist], [playlist, linkedFolder]);
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None);

        Assert.Equal(0, changedCount);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task
        RegularFolderWithUnresolvedLinkedChildIsPreserved()
    {
        var folder = new Folder
        {
            Id = Guid.NewGuid(),
            LinkedChildren =
            [
                CreateLegacyLinkedChild(path: "/media/not-indexed.mkv")
            ]
        };
        var library = CreateLibrary([folder], [folder]);
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None);

        Assert.Equal(0, changedCount);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task UnloadedOrdinaryRootWithHydratedMediaIsPreserved()
    {
        var root = new Folder
        {
            Id = Guid.NewGuid(),
            Name = "Unloaded root"
        };
        var media = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Stored linked movie",
            Path = "/media/stored-linked-movie.mkv"
        };
        var linkedChildren = new Mock<ILinkedChildrenService>();
        linkedChildren
            .Setup(m => m.GetLinkedChildrenIds(root.Id, null))
            .Returns([media.Id]);
        var library = CreateLibrary([root], [root, media]);
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(
            library, linkedChildren.Object).CleanAsync(
                config, null, CancellationToken.None);

        Assert.Equal(0, changedCount);
        linkedChildren.Verify(
            m => m.GetLinkedChildrenIds(root.Id, null),
            Times.Once);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task AlternateMovieKeepsPhysicalFolder()
    {
        var root = new Folder
        {
            Id = Guid.NewGuid(),
            LinkedChildren = []
        };
        var alternate = new Movie
        {
            Id = Guid.NewGuid(),
            ParentId = root.Id,
            Path = "/media/alternate-version.mkv"
        };
        alternate.SetPrimaryVersionId(Guid.NewGuid());
        var library = CreateLibrary([root], [root, alternate]);
        library
            .Setup(m => m.GetItemList(
                It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
            {
                if (query.IncludeItemTypes.Length > 0)
                {
                    return [root];
                }

                return query.IncludeOwnedItems
                    ? new List<BaseItem> { root, alternate }
                    : new List<BaseItem> { root };
            });
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None);

        Assert.Equal(0, changedCount);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task IndexedPhysicalMediaSkipsPersistedLinkLookups()
    {
        var config = CreateConfig();
        config.HideInsteadOfDelete = true;
        var root = new Folder
        {
            Id = Guid.NewGuid(),
            Tags = [config.HideTag]
        };
        var nested = new Folder
        {
            Id = Guid.NewGuid(),
            ParentId = root.Id,
            Tags = [config.HideTag]
        };
        var movie = new Movie
        {
            Id = Guid.NewGuid(),
            ParentId = nested.Id,
            Path = "/media/nested-physical-movie.mkv"
        };
        var unrelated = new Folder { Id = Guid.NewGuid() };
        var links = new Mock<ILinkedChildrenService>();
        links
            .Setup(m => m.GetLinkedChildrenIds(root.Id, null))
            .Returns([unrelated.Id]);
        links
            .Setup(m => m.GetLinkedChildrenIds(nested.Id, null))
            .Returns([unrelated.Id]);
        var library = CreateLibrary(
            [root, nested], [root, nested, movie, unrelated]);

        var previousLibraryManager = BaseItem.LibraryManager;
        BaseItem.LibraryManager = library.Object;
        int changedCount;
        try
        {
            changedCount = await CreateCleaner(
                library, links.Object).CleanAsync(
                    config, null, CancellationToken.None);
        }
        finally
        {
            BaseItem.LibraryManager = previousLibraryManager;
        }

        Assert.Equal(2, changedCount);
        Assert.DoesNotContain(config.HideTag, root.Tags);
        Assert.DoesNotContain(config.HideTag, nested.Tags);
        links.Verify(
            m => m.GetLinkedChildrenIds(
                It.IsAny<Guid>(), null),
            Times.Never);
        library.Verify(
            m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(),
                It.IsAny<BaseItem>(),
                ItemUpdateType.MetadataEdit,
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task OnlyReachableUnloadedFolderIsHydratedOnce()
    {
        var config = CreateConfig();
        config.DeleteEmptyFolders = false;
        config.DeleteEmptyPlaylists = false;
        config.HideInsteadOfDelete = false;
        var reachable = new Folder { Id = Guid.NewGuid() };
        var media = new Movie
        {
            Id = Guid.NewGuid(),
            Path = "/media/persisted-collection-movie.mkv"
        };
        var first = new BoxSet
        {
            Id = Guid.NewGuid(),
            LinkedChildren =
            [
                new LinkedChild { ItemId = reachable.Id }
            ]
        };
        var second = new BoxSet
        {
            Id = Guid.NewGuid(),
            LinkedChildren =
            [
                new LinkedChild { ItemId = reachable.Id }
            ]
        };
        var graph = new List<BaseItem>
        {
            first, second, reachable, media
        };
        for (var i = 0; i < 64; i++)
        {
            graph.Add(new Folder { Id = Guid.NewGuid() });
        }

        var linkedChildren = new Mock<ILinkedChildrenService>();
        linkedChildren
            .Setup(m => m.GetLinkedChildrenIds(reachable.Id, null))
            .Returns([media.Id]);
        var library = CreateLibrary([first, second], graph);

        var changedCount = await CreateCleaner(
            library, linkedChildren.Object).CleanAsync(
                config, null, CancellationToken.None);

        Assert.Equal(0, changedCount);
        linkedChildren.Verify(
            m => m.GetLinkedChildrenIds(reachable.Id, null),
            Times.Once);
        linkedChildren.Verify(
            m => m.GetLinkedChildrenIds(It.IsAny<Guid>(), null),
            Times.Once);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task ReplacedConfigDoesNotUntagHydratedFolder()
    {
        var config = CreateConfig();
        config.HideInsteadOfDelete = true;
        var current = config;
        var root = new Folder
        {
            Id = Guid.NewGuid(),
            Tags = [config.HideTag]
        };
        var media = new Movie
        {
            Id = Guid.NewGuid(),
            Path = "/media/stale-configuration-movie.mkv"
        };
        var linkedChildren = new Mock<ILinkedChildrenService>();
        linkedChildren
            .Setup(m => m.GetLinkedChildrenIds(root.Id, null))
            .Returns(() =>
            {
                current = new PluginConfiguration();
                return [media.Id];
            });
        var library = CreateLibrary([root], [root, media]);
        var cleaner = new ContainerCleaner(
            library.Object,
            NullLogger<ContainerCleaner>.Instance,
            linkedChildren.Object,
            candidate => ReferenceEquals(candidate, current));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => cleaner.CleanAsync(
                config, null, CancellationToken.None));

        Assert.Contains(config.HideTag, root.Tags);
        library.Verify(
            m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(),
                It.IsAny<BaseItem>(),
                It.IsAny<ItemUpdateType>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task
        UnloadedOrdinaryRootWithHydratedEmptyLinksIsDeleted()
    {
        var root = new Folder
        {
            Id = Guid.NewGuid(),
            Name = "Unloaded empty root"
        };
        var linkedChildren = new Mock<ILinkedChildrenService>();
        linkedChildren
            .Setup(m => m.GetLinkedChildrenIds(root.Id, null))
            .Returns([]);
        var library = CreateLibrary([root], [root]);
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(
            library, linkedChildren.Object).CleanAsync(
                config, null, CancellationToken.None);

        Assert.Equal(1, changedCount);
        linkedChildren.Verify(
            m => m.GetLinkedChildrenIds(root.Id, null),
            Times.Once);
        library.Verify(
            m => m.DeleteItem(
                root,
                It.Is<DeleteOptions>(o => !o.DeleteFileLocation)),
            Times.Once);
    }

    [Fact]
    public async Task
        UnresolvedLegacyPathDoesNotFallBackToLibraryItemId()
    {
        var emptyTarget = new Folder
        {
            Id = Guid.NewGuid(),
            LinkedChildren = []
        };
        var folder = new Folder
        {
            Id = Guid.NewGuid(),
            LinkedChildren =
            [
                CreateLegacyLinkedChild(
                    path: "/media/not-indexed.mkv",
                    libraryItemId: emptyTarget.Id.ToString("N"))
            ]
        };
        var library = CreateLibrary(
            [folder], [folder, emptyTarget]);
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None);

        Assert.Equal(0, changedCount);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task CyclicFoldersWithMediaAreBothRestored()
    {
        var config = CreateConfig();
        config.HideInsteadOfDelete = true;
        var first = new Folder
        {
            Id = Guid.NewGuid(),
            Tags = [config.HideTag]
        };
        var second = new Folder
        {
            Id = Guid.NewGuid(),
            Tags = [config.HideTag]
        };
        first.LinkedChildren =
        [
            new LinkedChild { ItemId = second.Id }
        ];
        second.LinkedChildren =
        [
            new LinkedChild { ItemId = first.Id }
        ];
        var media = new Movie
        {
            Id = Guid.NewGuid(),
            ParentId = first.Id,
            Path = "/media/first-folder-movie.mkv"
        };
        var library = CreateLibrary(
            [first, second], [first, second, media]);

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None);

        Assert.Equal(2, changedCount);
        Assert.DoesNotContain(config.HideTag, first.Tags);
        Assert.DoesNotContain(config.HideTag, second.Tags);
        library.Verify(
            m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(),
                It.IsAny<BaseItem>(),
                ItemUpdateType.MetadataEdit,
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task DeepCyclicEmptyLinkedGraphIsPreserved()
    {
        const int depth = 256;
        var root = new Folder
        {
            Id = Guid.NewGuid(),
            LinkedChildren = []
        };
        var graph = new List<BaseItem> { root };
        var previous = root;
        for (var i = 0; i < depth; i++)
        {
            var current = new Folder
            {
                Id = Guid.NewGuid(),
                ParentId = previous.Id,
                LinkedChildren = []
            };
            graph.Add(current);
            previous = current;
        }

        previous.LinkedChildren =
        [
            new LinkedChild { ItemId = root.Id }
        ];
        root.LinkedChildren =
        [
            new LinkedChild { ItemId = graph[1].Id }
        ];

        var library = CreateLibrary([root], graph);
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None);

        Assert.Equal(0, changedCount);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
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

        var changedCount = await CreateCleaner(
            library, CreateEmptyLinkedChildrenService()).CleanAsync(
                config, null, CancellationToken.None
        );

        Assert.Equal(1, changedCount);
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

        var changedCount = await CreateCleaner(
            library, CreateEmptyLinkedChildrenService()).CleanAsync(
                config, null, CancellationToken.None
        );

        Assert.Equal(1, changedCount);
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
            ParentId = deep.Id,
            Path = "/media/deeply-nested-movie.mkv"
        };
        var library = CreateLibrary(
            [root], [root, nested, deep, media]
        );
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None
        );

        Assert.Equal(0, changedCount);
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

        var changedCount = await CreateCleaner(
            library, CreateEmptyLinkedChildrenService()).CleanAsync(
                config, null, CancellationToken.None
        );

        Assert.Equal(1, changedCount);
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
            IsVirtualItem = false,
            Path = null
        };
        Assert.False(missingEpisode.IsVirtualItem);
        Assert.Null(missingEpisode.Path);
        var library = CreateLibrary(
            [root], [root, nested, missingEpisode]
        );
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var changedCount = await CreateCleaner(
            library, CreateEmptyLinkedChildrenService()).CleanAsync(
                config, null, CancellationToken.None
        );

        Assert.Equal(1, changedCount);
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

    // These obsolete fields are required for compatibility
    // regression coverage.
#pragma warning disable CS0618
    private static LinkedChild CreateLegacyLinkedChild(
        string? path = null,
        string? libraryItemId = null,
        Guid? itemId = null)
    {
        var linkedChild = new LinkedChild();
        if (itemId is Guid id)
        {
            linkedChild.ItemId = id;
        }

        if (path is not null)
        {
            linkedChild.Path = path;
        }

        if (libraryItemId is not null)
        {
            linkedChild.LibraryItemId = libraryItemId;
        }

        return linkedChild;
    }
#pragma warning restore CS0618

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

    private static ILinkedChildrenService
        CreateEmptyLinkedChildrenService()
    {
        var service = new Mock<ILinkedChildrenService>();
        service
            .Setup(m => m.GetLinkedChildrenIds(
                It.IsAny<Guid>(), null))
            .Returns([]);
        return service.Object;
    }

    private static ContainerCleaner CreateCleaner(
        Mock<ILibraryManager> library,
        ILinkedChildrenService? linkedChildrenService = null
    )
    {
        return new ContainerCleaner(
            library.Object,
            NullLogger<ContainerCleaner>.Instance,
            linkedChildrenService
        );
    }
}
