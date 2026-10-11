using Jellyfin.Data.Enums;
using Jellyfin.Plugin.IgnoreEmptyFolders.Cleaners;
using Jellyfin.Plugin.IgnoreEmptyFolders.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.IgnoreEmptyFolders.Tests;

public sealed class MovieCleanerTests
{
    [Fact]
    public async Task
        HideModeRestoresChannelButHidesPathlessLibraryMovie()
    {
        var config = CreateConfig();
        config.HideInsteadOfDelete = true;
        var channel = NewMovie("Channel movie", config);
        channel.ChannelId = Guid.NewGuid();
        var libraryMovie = NewMovie("Pathless library movie", config);
        libraryMovie.Tags = [];
        var physical = NewMovie("Physical movie", config);
        physical.Path = "/media/physical.mkv";
        var alreadyHidden = NewMovie("Already hidden", config);
        alreadyHidden.Tags = [config.HideTag];
        var movies = new[]
        {
            channel, libraryMovie, physical, alreadyHidden
        };
        var library = CreateLibrary(movies);

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None);

        Assert.Equal(3, changedCount);
        Assert.DoesNotContain(config.HideTag, channel.Tags);
        Assert.Contains(config.HideTag, libraryMovie.Tags);
        Assert.DoesNotContain(config.HideTag, physical.Tags);
        Assert.Contains(config.HideTag, alreadyHidden.Tags);
        library.Verify(m => m.UpdateItemAsync(
            alreadyHidden,
            It.IsAny<BaseItem>(),
            It.IsAny<ItemUpdateType>(),
            It.IsAny<CancellationToken>()), Times.Never);
        library.Verify(m => m.DeleteItem(
            It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task
        DeleteModeOnlyDeletesPathlessLibraryAndVirtualMovies()
    {
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;
        var channel = NewMovie("Channel movie", config);
        channel.ChannelId = Guid.NewGuid();
        var libraryMovie = NewMovie("Pathless library movie", config);
        var virtualMovie = NewMovie("Virtual movie", config);
        virtualMovie.Path = "/media/virtual.mkv";
        virtualMovie.IsVirtualItem = true;
        var physical = NewMovie("Physical movie", config);
        physical.Path = "/media/physical.mkv";
        var library = CreateLibrary(
            [channel, libraryMovie, virtualMovie, physical]);

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None);

        Assert.Equal(2, changedCount);
        library.Verify(m => m.DeleteItem(
            libraryMovie,
            It.Is<DeleteOptions>(o => !o.DeleteFileLocation)),
            Times.Once);
        library.Verify(m => m.DeleteItem(
            virtualMovie,
            It.Is<DeleteOptions>(o => !o.DeleteFileLocation)),
            Times.Once);
        library.Verify(m => m.DeleteItem(
            channel, It.IsAny<DeleteOptions>()), Times.Never);
        library.Verify(m => m.DeleteItem(
            physical, It.IsAny<DeleteOptions>()), Times.Never);
    }

    [Fact]
    public async Task
        MissingMovieWithRetainedPathIsHiddenWithoutRestoring()
    {
        var config = CreateConfig();
        config.HideInsteadOfDelete = true;
        var missing = NewMovie("Missing", config);
        missing.Path = "/media/missing.mkv";
        missing.IsVirtualItem = true;
        missing.Tags = [];
        var alreadyHidden = NewMovie("Already hidden missing", config);
        alreadyHidden.Path = "/media/already-hidden.mkv";
        alreadyHidden.IsVirtualItem = true;
        var available = NewMovie("Available", config);
        available.Path = "/media/available.mkv";
        var library = CreateLibrary(
            [missing, alreadyHidden, available]);

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None);

        Assert.Equal(2, changedCount);
        Assert.Contains(config.HideTag, missing.Tags);
        Assert.Contains(config.HideTag, alreadyHidden.Tags);
        Assert.DoesNotContain(config.HideTag, available.Tags);
        library.Verify(m => m.UpdateItemAsync(
            alreadyHidden, It.IsAny<BaseItem>(),
            It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task
        MissingMovieWithRetainedPathIsDeletedWithoutDeletingFile()
    {
        var config = CreateConfig();
        config.HideInsteadOfDelete = false;
        var missing = NewMovie("Missing", config);
        missing.Path = "/media/missing.mkv";
        missing.IsVirtualItem = true;
        var available = NewMovie("Available", config);
        available.Path = "/media/available.mkv";
        var library = CreateLibrary([missing, available]);

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None);

        Assert.Equal(1, changedCount);
        library.Verify(m => m.DeleteItem(
            missing, It.Is<DeleteOptions>(o => !o.DeleteFileLocation)),
            Times.Once);
        library.Verify(m => m.DeleteItem(
            available, It.IsAny<DeleteOptions>()), Times.Never);
    }

    [Fact]
    public async Task OwnedVersionWithSharedPresentationKeyIsCleaned()
    {
        var config = CreateConfig();
        config.HideInsteadOfDelete = true;
        var physical = NewMovie("Available version", config);
        physical.Path = "/media/available.mkv";
        physical.Tags = [];
        physical.PresentationUniqueKey = "shared-version";
        var owned = NewMovie("Missing owned version", config);
        owned.OwnerId = physical.Id;
        owned.Tags = [];
        owned.PresentationUniqueKey = physical.PresentationUniqueKey;
        var library = CreateLibrary([physical, owned]);

        var changedCount = await CreateCleaner(library).CleanAsync(
            config, null, CancellationToken.None);

        Assert.Equal(1, changedCount);
        Assert.DoesNotContain(config.HideTag, physical.Tags);
        Assert.Contains(config.HideTag, owned.Tags);
    }

    private static Movie NewMovie(
        string name, PluginConfiguration config)
    {
        return new Movie
        {
            Id = Guid.NewGuid(),
            Name = name,
            IsVirtualItem = false,
            Tags = [config.HideTag]
        };
    }

    private static PluginConfiguration CreateConfig()
    {
        return new PluginConfiguration
        {
            DeleteEmptyMovies = true,
            LogDeletions = false
        };
    }

    private static MovieCleaner CreateCleaner(
        Mock<ILibraryManager> library)
    {
        return new MovieCleaner(
            library.Object, NullLogger<MovieCleaner>.Instance);
    }

    private static Mock<ILibraryManager> CreateLibrary(
        IReadOnlyCollection<Movie> movies)
    {
        var library = new Mock<ILibraryManager>();
        library
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
            {
                Assert.True(query.IncludeItemTypes.SequenceEqual(
                    [BaseItemKind.Movie]));
                IEnumerable<Movie> selected = movies;
                if (!query.IncludeOwnedItems)
                {
                    selected = selected.Where(movie =>
                        movie.OwnerId == Guid.Empty);
                }

                if ((query.IsVirtualItem ?? query.IsMissing) is
                    bool isVirtual)
                {
                    selected = selected.Where(movie =>
                        movie.IsVirtualItem == isVirtual);
                }

                if (query.GroupByPresentationUniqueKey)
                {
                    selected = selected.DistinctBy(movie =>
                        movie.GetPresentationUniqueKey());
                }

                return selected.Cast<BaseItem>().ToList();
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
}
