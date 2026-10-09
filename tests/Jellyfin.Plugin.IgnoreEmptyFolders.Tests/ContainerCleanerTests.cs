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
    public void UnloadedPlaylistIsSkipped()
    {
        var playlist = new MediaBrowser.Controller.Playlists.Playlist
        {
            Id = Guid.NewGuid()
        };
        var library = CreateLibrary((playlist, 0));
        var cleaner = CreateCleaner(library);
        var config = CreateConfig();

        var removed = cleaner.Clean(
            config,
            null,
            CancellationToken.None);

        Assert.Equal(0, removed);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(),
                It.IsAny<DeleteOptions>()),
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
    public void LoadedEmptyPlaylistIsDeletedWithoutDeletingFiles()
    {
        var playlist = new MediaBrowser.Controller.Playlists.Playlist
        {
            Id = Guid.NewGuid(),
            LinkedChildren = []
        };
        var library = CreateLibrary((playlist, 0));
        var cleaner = CreateCleaner(library);
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var removed = cleaner.Clean(
            config,
            null,
            CancellationToken.None);

        Assert.Equal(1, removed);
        library.Verify(
            m => m.DeleteItem(
                playlist,
                It.Is<DeleteOptions>(o => !o.DeleteFileLocation)),
            Times.Once);
    }

    [Fact]
    public void PlaylistWithMediaIsNotDeleted()
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
                new LinkedChild
                {
                    ItemId = media.Id
                }
            ]
        };
        var library = CreateLibrary((playlist, 1));
        var cleaner = CreateCleaner(library);
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var removed = cleaner.Clean(
            config,
            null,
            CancellationToken.None);

        Assert.Equal(0, removed);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(),
                It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public void EmptyBoxSetIsTaggedInHideMode()
    {
        var boxSet = new BoxSet
        {
            Id = Guid.NewGuid(),
            LinkedChildren = []
        };
        var library = CreateLibrary((boxSet, 0));
        var cleaner = CreateCleaner(library);
        var config = CreateConfig();

        var removed = cleaner.Clean(
            config,
            null,
            CancellationToken.None);

        Assert.Equal(1, removed);
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
    public void BoxSetWithMediaIsPreserved()
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
                new LinkedChild
                {
                    ItemId = media.Id
                }
            ]
        };
        var library = CreateLibrary((boxSet, 1));
        var cleaner = CreateCleaner(library);
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var removed = cleaner.Clean(
            config,
            null,
            CancellationToken.None);

        Assert.Equal(0, removed);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(),
                It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public void NestedEmptyFoldersAreDeleted()
    {
        var folder = new Folder
        {
            Id = Guid.NewGuid(),
            Name = "Root",
            Children =
            [
                new Folder
                {
                    Id = Guid.NewGuid(),
                    Name = "Nested empty folder",
                    Children = []
                }
            ]
        };
        var library = CreateLibrary((folder, 0));
        var cleaner = CreateCleaner(library);
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var removed = cleaner.Clean(
            config,
            null,
            CancellationToken.None);

        Assert.Equal(1, removed);
        library.Verify(
            m => m.DeleteItem(
                folder,
                It.Is<DeleteOptions>(o => !o.DeleteFileLocation)),
            Times.Once);
    }

    [Fact]
    public void MetadataOnlyFoldersAreDeleted()
    {
        var folder = new Folder
        {
            Id = Guid.NewGuid(),
            Name = "Metadata-only root",
            Children =
            [
                new Folder
                {
                    Id = Guid.NewGuid(),
                    Name = "Metadata folder",
                    Children = []
                },
                new Folder
                {
                    Id = Guid.NewGuid(),
                    Name = "Another metadata folder",
                    Children = []
                }
            ]
        };
        var library = CreateLibrary((folder, 0));
        var cleaner = CreateCleaner(library);
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var removed = cleaner.Clean(
            config,
            null,
            CancellationToken.None);

        Assert.Equal(1, removed);
        library.Verify(
            m => m.DeleteItem(
                folder,
                It.IsAny<DeleteOptions>()),
            Times.Once);
    }

    [Fact]
    public void DeeplyNestedRealMediaPreservesContainingFolder()
    {
        var folder = new Folder
        {
            Id = Guid.NewGuid(),
            Name = "Media root",
            Children =
            [
                new Folder
                {
                    Id = Guid.NewGuid(),
                    Name = "Nested folder",
                    Children =
                    [
                        new Folder
                        {
                            Id = Guid.NewGuid(),
                            Name = "Deeply nested folder",
                            Children =
                            [
                                new Movie
                                {
                                    Id = Guid.NewGuid(),
                                    Name = "Real movie",
                                    IsVirtualItem = false,
                                }
                            ]
                        }
                    ]
                }
            ]
        };
        var library = CreateLibrary((folder, 1));
        var cleaner = CreateCleaner(library);
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var removed = cleaner.Clean(
            config,
            null,
            CancellationToken.None);

        Assert.Equal(0, removed);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(),
                It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public void FolderWithOnlyVirtualMediaIsDeleted()
    {
        var folder = new Folder
        {
            Id = Guid.NewGuid(),
            Name = "Virtual media root",
            Children =
            [
                new Folder
                {
                    Id = Guid.NewGuid(),
                    Children =
                    [
                        new Movie
                        {
                            Id = Guid.NewGuid(),
                            Name = "Virtual movie",
                            IsVirtualItem = true,
                        }
                    ]
                }
            ]
        };
        var library = CreateLibrary((folder, 0));
        var cleaner = CreateCleaner(library);
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var removed = cleaner.Clean(
            config,
            null,
            CancellationToken.None);

        Assert.Equal(1, removed);
        library.Verify(
            m => m.DeleteItem(
                folder,
                It.IsAny<DeleteOptions>()),
            Times.Once);
    }

    [Fact]
    public void FolderWithOnlyMissingMediaIsDeleted()
    {
        var folder = new Folder
        {
            Id = Guid.NewGuid(),
            Name = "Missing media root",
            Children =
            [
                new Folder
                {
                    Id = Guid.NewGuid(),
                    Children =
                    [
                        new Episode
                        {
                            Id = Guid.NewGuid(),
                            Name = "Missing episode",
                            IsVirtualItem = true
                        }
                    ]
                }
            ]
        };
        var library = CreateLibrary((folder, 0));
        var cleaner = CreateCleaner(library);
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;

        var removed = cleaner.Clean(
            config,
            null,
            CancellationToken.None);

        Assert.Equal(1, removed);
        library.Verify(
            m => m.DeleteItem(
                folder,
                It.IsAny<DeleteOptions>()),
            Times.Once);
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
        params (BaseItem Item, int RealMediaCount)[] scenarios)
    {
        var library = new Mock<ILibraryManager>();
        var scenariosForQuery = scenarios;

        library
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(
                scenarios.Select(scenario => scenario.Item).ToList()
            );
        library
            .Setup(m => m.GetCount(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
            {
                Assert.True(query.Recursive);
                Assert.Equal(false, query.IsFolder);
                Assert.Equal(false, query.IsVirtualItem);
                Assert.Equal(false, query.IsMissing);
                Assert.Equal(0, query.Limit);
                var scenario = scenariosForQuery.FirstOrDefault(
                    candidate => candidate.Item.Id == query.ParentId);
                Assert.NotNull(scenario.Item);
                return scenario.RealMediaCount;
            });

        library
            .Setup(m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(),
                It.IsAny<BaseItem>(),
                It.IsAny<ItemUpdateType>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return library;
    }

    private static ContainerCleaner CreateCleaner(
        Mock<ILibraryManager> library)
    {
        return new ContainerCleaner(
            library.Object,
            NullLogger<ContainerCleaner>.Instance);
    }
}
