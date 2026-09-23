using Jellyfin.Plugin.IgnoreEmptyFolders.Cleaners;
using Jellyfin.Plugin.IgnoreEmptyFolders.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
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
        var playlist = new MediaBrowser.Controller.Playlists.Playlist();
        var library = CreateLibrary(playlist);
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
            LinkedChildren = []
        };
        var library = CreateLibrary(playlist);
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
        var playlist = new MediaBrowser.Controller.Playlists.Playlist
        {
            LinkedChildren =
            [
                new LinkedChild
                {
                    ItemId = Guid.NewGuid()
                }
            ]
        };
        var library = CreateLibrary(playlist);
        library
            .Setup(m => m.GetCount(
                It.IsAny<InternalItemsQuery>()))
            .Returns(1);
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
            LinkedChildren = []
        };
        var library = CreateLibrary(boxSet);
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

    private static PluginConfiguration CreateConfig()
    {
        return new PluginConfiguration
        {
            DeleteEmptyCollections = true,
            DeleteEmptyFolders = false,
            DeleteEmptyPlaylists = true,
            LogDeletions = false
        };
    }

    private static Mock<ILibraryManager> CreateLibrary(BaseItem item)
    {
        var library = new Mock<ILibraryManager>();
        library
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns([item]);
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
