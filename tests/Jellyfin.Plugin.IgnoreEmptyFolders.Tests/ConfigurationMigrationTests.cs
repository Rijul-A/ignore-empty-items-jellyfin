using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.IgnoreEmptyFolders.Cleaners;
using Jellyfin.Plugin.IgnoreEmptyFolders.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.IgnoreEmptyFolders.Tests;

public sealed class ConfigurationMigrationTests
{
    [Fact]
    public async Task ConcurrentManagersDoNotOverlapTagMigration()
    {
        var configuration = new PluginConfiguration
        {
            HideInsteadOfDelete = true,
            HideTag = "new-hide-tag",
            PreviousHideTag = "old-hide-tag",
            HideInsteadOfDeleteWasActive = true
        };
        _ = CreatePlugin(configuration);

        var firstMigrationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstMigration = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstLibrary = new Mock<ILibraryManager>();
        firstLibrary
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery _) =>
            {
                firstMigrationStarted.TrySetResult(true);
                releaseFirstMigration.Task.GetAwaiter().GetResult();
                return [];
            });

        var secondLibrary = new Mock<ILibraryManager>();
        secondLibrary
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns([]);

        var firstUserManager = new Mock<IUserManager>();
        firstUserManager.Setup(m => m.GetUsers()).Returns([]);
        var secondUserManager = new Mock<IUserManager>();
        secondUserManager.Setup(m => m.GetUsers()).Returns([]);

        var firstManager = new LibraryCleanupManager(
            firstLibrary.Object,
            firstUserManager.Object,
            NullLoggerFactory.Instance);
        var secondManager = new LibraryCleanupManager(
            secondLibrary.Object,
            secondUserManager.Object,
            NullLoggerFactory.Instance);

        var firstTask = Task.Run(
            () => firstManager.HandleConfigurationChangedAsync(
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        try
        {
            await firstMigrationStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);

            using var secondCancellation = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<
                OperationCanceledException>(
                () => secondManager.HandleConfigurationChangedAsync(
                    secondCancellation.Token));

            secondLibrary.Verify(
                m => m.GetItemList(It.IsAny<InternalItemsQuery>()),
                Times.Never);
        }
        finally
        {
            releaseFirstMigration.TrySetResult(true);
            await firstTask;
        }
    }

    [Fact]
    public async Task
        CleanupUsesConfigurationAfterMigrationReconciliation()
    {
        var configurationB = new PluginConfiguration
        {
            HideInsteadOfDelete = true,
            HideTag = "B",
            PreviousHideTag = "A",
            HideInsteadOfDeleteWasActive = true,
            DeleteEmptyShows = false,
            DeleteEmptySeasons = false,
            DeleteEmptyMovies = false,
            DeleteEmptyMusicArtists = false,
            DeleteEmptyMusicAlbums = false,
            DeleteEmptyCollections = false,
            DeleteEmptyFolders = false,
            DeleteEmptyPlaylists = false
        };
        var configurationC = new PluginConfiguration
        {
            HideInsteadOfDelete = true,
            HideTag = "C",
            PreviousHideTag = "B",
            HideInsteadOfDeleteWasActive = true,
            DeleteEmptyShows = false,
            DeleteEmptySeasons = false,
            DeleteEmptyMovies = true,
            DeleteEmptyMusicArtists = false,
            DeleteEmptyMusicAlbums = false,
            DeleteEmptyCollections = false,
            DeleteEmptyFolders = false,
            DeleteEmptyPlaylists = false
        };
        var plugin = CreatePlugin(configurationB);
        var movie = new Movie
        {
            IsVirtualItem = true
        };
        var configurationReplaced = false;

        var library = new Mock<ILibraryManager>();
        library
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
            {
                if (query.Tags.Length > 0)
                {
                    if (!configurationReplaced &&
                        query.Tags.Contains("A"))
                    {
                        configurationReplaced = true;
                        plugin.UpdateConfiguration(configurationC);
                    }

                    return [];
                }

                if (query.IncludeItemTypes.Contains(BaseItemKind.Movie))
                {
                    return [movie];
                }

                return [];
            });
        library
            .Setup(m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(),
                It.IsAny<BaseItem>(),
                It.IsAny<ItemUpdateType>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var userManager = new Mock<IUserManager>();
        userManager.Setup(m => m.GetUsers()).Returns([]);

        var manager = new LibraryCleanupManager(
            library.Object,
            userManager.Object,
            NullLoggerFactory.Instance);

        await manager.CleanLibraryAsync(
            new Progress<double>(),
            TestContext.Current.CancellationToken);

        Assert.Contains("C", movie.Tags);
        library.Verify(
            m => m.UpdateItemAsync(
                movie,
                It.IsAny<BaseItem>(),
                ItemUpdateType.MetadataEdit,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \t  ")]
    public async Task InvalidHideTagSkipsEligibleMovieCleanup(
        string hideTag)
    {
        var configuration = new PluginConfiguration
        {
            HideInsteadOfDelete = true,
            HideTag = hideTag,
            DeleteEmptyShows = false,
            DeleteEmptySeasons = false,
            DeleteEmptyMovies = true,
            DeleteEmptyMusicArtists = false,
            DeleteEmptyMusicAlbums = false,
            DeleteEmptyCollections = false,
            DeleteEmptyFolders = false,
            DeleteEmptyPlaylists = false
        };
        _ = CreatePlugin(configuration);
        var movie = new Movie { IsVirtualItem = true };
        var library = new Mock<ILibraryManager>();
        library
            .Setup(m => m.GetItemList(
                It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
                query.IncludeItemTypes.Contains(BaseItemKind.Movie)
                    ? [movie]
                    : []);
        var users = new Mock<IUserManager>();
        users.Setup(m => m.GetUsers()).Returns([]);
        var manager = new LibraryCleanupManager(
            library.Object,
            users.Object,
            NullLoggerFactory.Instance);

        await manager.CleanLibraryAsync(
            new Progress<double>(),
            TestContext.Current.CancellationToken);

        Assert.Empty(movie.Tags);
        library.Verify(
            m => m.GetItemList(It.IsAny<InternalItemsQuery>()),
            Times.Never);
        library.Verify(
            m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(),
                It.IsAny<BaseItem>(),
                It.IsAny<ItemUpdateType>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(),
                It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \t  ")]
    public async Task StandaloneCleanerDoesNotApplyInvalidTag(
        string hideTag)
    {
        var movie = new Movie { IsVirtualItem = true };
        var library = new Mock<ILibraryManager>();
        library
            .Setup(m => m.GetItemList(
                It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
                query.IsVirtualItem == true
                    ? [movie]
                    : []);
        var cleaner = new MovieCleaner(
            library.Object,
            NullLogger<MovieCleaner>.Instance);

        var changed = await cleaner.CleanAsync(
            new PluginConfiguration
            {
                HideInsteadOfDelete = true,
                HideTag = hideTag
            },
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, changed);
        Assert.Empty(movie.Tags);
        library.Verify(
            m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(),
                It.IsAny<BaseItem>(),
                It.IsAny<ItemUpdateType>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(),
                It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task OwnedRealMovieLosesStaleHideTag()
    {
        const string hideTag = "hidden";
        var movie = new Movie
        {
            Id = Guid.NewGuid(),
            Path = "/media/alternate-version.mkv",
            Tags = [hideTag]
        };
        movie.SetPrimaryVersionId(Guid.NewGuid());
        var library = new Mock<ILibraryManager>();
        library
            .Setup(m => m.GetItemList(
                It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
                query.IncludeOwnedItems &&
                query.IncludeItemTypes.Contains(
                    BaseItemKind.Movie) &&
                query.Tags.Contains(hideTag) &&
                query.IsVirtualItem == false &&
                query.IsMissing == false &&
                !query.GroupByPresentationUniqueKey
                    ? [movie]
                    : []);
        library
            .Setup(m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(),
                It.IsAny<BaseItem>(),
                It.IsAny<ItemUpdateType>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var cleaner = new MovieCleaner(
            library.Object,
            NullLogger<MovieCleaner>.Instance);

        var changed = await cleaner.CleanAsync(
            new PluginConfiguration
            {
                HideInsteadOfDelete = true,
                HideTag = hideTag
            },
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, changed);
        Assert.Empty(movie.Tags);
        library.Verify(
            m => m.UpdateItemAsync(
                movie,
                It.IsAny<BaseItem>(),
                ItemUpdateType.MetadataEdit,
                It.IsAny<CancellationToken>()),
            Times.Once);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(),
                It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task ReplacementWithInvalidTagSkipsMovieCleanup()
    {
        var configuration = new PluginConfiguration
        {
            HideInsteadOfDelete = true,
            HideTag = "B",
            PreviousHideTag = "A",
            HideInsteadOfDeleteWasActive = true,
            DeleteEmptyMovies = true
        };
        var plugin = CreatePlugin(configuration);
        var movie = new Movie
        {
            IsVirtualItem = true,
            Tags = ["A"]
        };
        var library = new Mock<ILibraryManager>();
        library
            .Setup(m => m.GetItemList(
                It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
            {
                if (query.Tags.Contains("A"))
                {
                    plugin.UpdateConfiguration(
                        new PluginConfiguration
                        {
                            HideInsteadOfDelete = true,
                            HideTag = " ",
                            DeleteEmptyMovies = true
                        });
                    return [movie];
                }

                return query.IncludeItemTypes.Contains(
                    BaseItemKind.Movie)
                    ? [movie]
                    : [];
            });
        var users = new Mock<IUserManager>();
        users.Setup(m => m.GetUsers()).Returns([]);
        var manager = new LibraryCleanupManager(
            library.Object,
            users.Object,
            NullLoggerFactory.Instance);

        await manager.CleanLibraryAsync(
            new Progress<double>(),
            TestContext.Current.CancellationToken);

        Assert.Equal(["A"], movie.Tags);
        library.Verify(
            m => m.GetItemList(It.Is<InternalItemsQuery>(
                query => query.IncludeItemTypes.Contains(
                    BaseItemKind.Movie))),
            Times.Never);
        library.Verify(
            m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(),
                It.IsAny<BaseItem>(),
                It.IsAny<ItemUpdateType>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(),
                It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    [Fact]
    public async Task UnchangedReconciliationDoesNotSaveAgain()
    {
        var configuration = new PluginConfiguration
        {
            HideInsteadOfDelete = true,
            HideTag = "hidden",
            PreviousHideTag = "hidden",
            HideInsteadOfDeleteWasActive = true,
            DeleteEmptyShows = false,
            DeleteEmptySeasons = false,
            DeleteEmptyMovies = true,
            DeleteEmptyMusicArtists = false,
            DeleteEmptyMusicAlbums = false,
            DeleteEmptyCollections = false,
            DeleteEmptyFolders = false,
            DeleteEmptyPlaylists = false
        };
        var saves = new List<object>();
        _ = CreatePlugin(configuration, saves.Add);
        var movies = new[]
        {
            new Movie { IsVirtualItem = true },
            new Movie { IsVirtualItem = true }
        };
        var lookup = 0;
        var library = new Mock<ILibraryManager>();
        library
            .Setup(m => m.GetItemList(
                It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
            {
                if (query.IsVirtualItem == true)
                {
                    return [movies[lookup++]];
                }

                return [];
            });
        library
            .Setup(m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(),
                It.IsAny<BaseItem>(),
                It.IsAny<ItemUpdateType>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var user = new User("user", "auth", "reset");
        var userDto = new UserDto
        {
            Policy = new UserPolicy
            {
                BlockedTags = []
            }
        };
        var users = new Mock<IUserManager>();
        users.Setup(m => m.GetUsers()).Returns([user]);
        users
            .Setup(m => m.GetUserDto(user, It.IsAny<string>()))
            .Returns(userDto);
        users
            .Setup(m => m.UpdatePolicyAsync(
                It.IsAny<Guid>(),
                It.IsAny<UserPolicy>()))
            .Returns(Task.CompletedTask);
        var manager = new LibraryCleanupManager(
            library.Object,
            users.Object,
            NullLoggerFactory.Instance);

        await manager.CleanLibraryAsync(
            new Progress<double>(),
            TestContext.Current.CancellationToken);
        await manager.CleanLibraryAsync(
            new Progress<double>(),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, lookup);
        Assert.All(
            movies,
            movie => Assert.Contains("hidden", movie.Tags));
        Assert.Equal(["hidden"], userDto.Policy!.BlockedTags);
        Assert.Empty(saves);
        library.Verify(
            m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(),
                It.IsAny<BaseItem>(),
                ItemUpdateType.MetadataEdit,
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task
        ConfigurationReplacementDuringMigrationConvergesToLatestTag()
    {
        var configurationB = new PluginConfiguration
        {
            HideInsteadOfDelete = true,
            HideTag = "B",
            PreviousHideTag = "A",
            HideInsteadOfDeleteWasActive = true
        };
        var savedConfigurations = new List<object>();
        var plugin = CreatePlugin(
            configurationB,
            savedConfigurations.Add);
        var configurationC = new PluginConfiguration
        {
            HideInsteadOfDelete = true,
            HideTag = "C",
            PreviousHideTag = "B",
            HideInsteadOfDeleteWasActive = true
        };

        var item = new Movie { Tags = ["A"] };
        var user = new User("user", "auth", "reset");
        var userDto = new UserDto
        {
            Policy = new UserPolicy
            {
                BlockedTags = ["A"]
            }
        };
        var itemUpdateStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseItemUpdate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var library = new Mock<ILibraryManager>();
        library
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
            {
                var tag = query.Tags.Single();
                return item.Tags.Contains(
                        tag,
                        StringComparer.OrdinalIgnoreCase)
                    ? [item]
                    : [];
            });
        library
            .Setup(m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(),
                It.IsAny<BaseItem>(),
                It.IsAny<ItemUpdateType>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (
                BaseItem updatedItem,
                BaseItem _,
                ItemUpdateType _,
                CancellationToken _) =>
            {
                if (updatedItem.Tags.Contains(
                        "B",
                        StringComparer.OrdinalIgnoreCase) &&
                    !itemUpdateStarted.Task.IsCompleted)
                {
                    itemUpdateStarted.TrySetResult(true);
                    await releaseItemUpdate.Task;
                }
            });

        var userManager = new Mock<IUserManager>();
        userManager.Setup(m => m.GetUsers()).Returns([user]);
        userManager
            .Setup(m => m.GetUserDto(user, It.IsAny<string>()))
            .Returns(userDto);
        userManager
            .Setup(m => m.UpdatePolicyAsync(
                It.IsAny<Guid>(),
                It.IsAny<UserPolicy>()))
            .Returns(Task.CompletedTask);

        var manager = new LibraryCleanupManager(
            library.Object,
            userManager.Object,
            NullLoggerFactory.Instance);
        var migration = manager.HandleConfigurationChangedAsync(
            TestContext.Current.CancellationToken);

        await itemUpdateStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        plugin.UpdateConfiguration(configurationC);
        releaseItemUpdate.TrySetResult(true);
        await migration;

        Assert.Equal(["C"], item.Tags);
        Assert.Equal(["C"], userDto.Policy!.BlockedTags);
        Assert.Equal("C", configurationC.PreviousHideTag);
        Assert.True(configurationC.HideInsteadOfDeleteWasActive);
        Assert.DoesNotContain(configurationB, savedConfigurations);
        Assert.Contains(configurationC, savedConfigurations);
    }

    [Fact]
    public async Task
        ReplacingConfigurationAfterFirstDeleteStopsCleanup()
    {
        var config = new PluginConfiguration
        {
            HideInsteadOfDelete = false,
            DeleteEmptyShows = false,
            DeleteEmptySeasons = false,
            DeleteEmptyMovies = true,
            DeleteEmptyMusicArtists = false,
            DeleteEmptyMusicAlbums = false,
            DeleteEmptyCollections = false,
            DeleteEmptyFolders = false,
            DeleteEmptyPlaylists = false
        };
        var plugin = CreatePlugin(config);
        var first = new Movie
        {
            Id = Guid.NewGuid(),
            IsVirtualItem = true
        };
        var second = new Movie
        {
            Id = Guid.NewGuid(),
            IsVirtualItem = true
        };
        var library = new Mock<ILibraryManager>();
        library
            .Setup(m => m.GetItemList(
                It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
                query.IncludeItemTypes.Contains(BaseItemKind.Movie) &&
                query.IsVirtualItem == true
                    ? [first, second]
                    : []);
        library
            .Setup(m => m.DeleteItem(
                It.IsAny<BaseItem>(),
                It.IsAny<DeleteOptions>()))
            .Callback<BaseItem, DeleteOptions>((item, _) =>
            {
                Assert.Same(first, item);
                plugin.UpdateConfiguration(
                    new PluginConfiguration
                    {
                        HideInsteadOfDelete = true,
                        HideTag = "new-tag",
                        DeleteEmptyMovies = true
                    });
            });
        var users = new Mock<IUserManager>();
        users.Setup(m => m.GetUsers()).Returns([]);
        var manager = new LibraryCleanupManager(
            library.Object,
            users.Object,
            NullLoggerFactory.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.CleanLibraryAsync(
                new Progress<double>(),
                TestContext.Current.CancellationToken));

        library.Verify(
            m => m.DeleteItem(first, It.IsAny<DeleteOptions>()),
            Times.Once);
        library.Verify(
            m => m.DeleteItem(second, It.IsAny<DeleteOptions>()),
            Times.Never);
        library.Verify(
            m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(),
                It.IsAny<BaseItem>(),
                It.IsAny<ItemUpdateType>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacingConfigurationStopsFurtherTagChanges(
        bool untag)
    {
        var config = new PluginConfiguration
        {
            HideInsteadOfDelete = true,
            HideTag = "hidden",
            PreviousHideTag = "hidden",
            HideInsteadOfDeleteWasActive = true,
            DeleteEmptyShows = false,
            DeleteEmptySeasons = false,
            DeleteEmptyMovies = true,
            DeleteEmptyMusicArtists = false,
            DeleteEmptyMusicAlbums = false,
            DeleteEmptyCollections = false,
            DeleteEmptyFolders = false,
            DeleteEmptyPlaylists = false
        };
        var plugin = CreatePlugin(config);
        var first = new Movie
        {
            Id = Guid.NewGuid(),
            IsVirtualItem = !untag,
            Tags = untag ? ["hidden"] : []
        };
        var second = new Movie
        {
            Id = Guid.NewGuid(),
            IsVirtualItem = !untag,
            Tags = untag ? ["hidden"] : []
        };
        var library = new Mock<ILibraryManager>();
        library
            .Setup(m => m.GetItemList(
                It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
            {
                if (!query.IncludeItemTypes.Contains(
                    BaseItemKind.Movie))
                {
                    return [];
                }

                if (untag && query.Tags.Contains("hidden"))
                {
                    return [first, second];
                }

                return !untag && query.IsVirtualItem == true
                    ? [first, second]
                    : [];
            });
        library
            .Setup(m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(),
                It.IsAny<BaseItem>(),
                It.IsAny<ItemUpdateType>(),
                It.IsAny<CancellationToken>()))
            .Callback<BaseItem, BaseItem, ItemUpdateType,
                CancellationToken>((item, _, _, _) =>
            {
                Assert.Same(first, item);
                plugin.UpdateConfiguration(
                    new PluginConfiguration
                    {
                        HideInsteadOfDelete = false,
                        DeleteEmptyMovies = true
                    });
            })
            .Returns(Task.CompletedTask);
        var users = new Mock<IUserManager>();
        users.Setup(m => m.GetUsers()).Returns([]);
        var manager = new LibraryCleanupManager(
            library.Object,
            users.Object,
            NullLoggerFactory.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.CleanLibraryAsync(
                new Progress<double>(),
                TestContext.Current.CancellationToken));

        Assert.Equal(
            untag ? Array.Empty<string>() : ["hidden"],
            first.Tags);
        Assert.Equal(
            untag ? ["hidden"] : Array.Empty<string>(),
            second.Tags);
        library.Verify(
            m => m.UpdateItemAsync(
                first,
                It.IsAny<BaseItem>(),
                ItemUpdateType.MetadataEdit,
                It.IsAny<CancellationToken>()),
            Times.Once);
        library.Verify(
            m => m.UpdateItemAsync(
                second,
                It.IsAny<BaseItem>(),
                It.IsAny<ItemUpdateType>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        library.Verify(
            m => m.DeleteItem(
                It.IsAny<BaseItem>(),
                It.IsAny<DeleteOptions>()),
            Times.Never);
    }

    private static Plugin CreatePlugin(
        PluginConfiguration configuration,
        Action<object>? onSave = null)
    {
        var paths = new Mock<IApplicationPaths>();
        var tempPath = Path.GetTempPath();
        paths.SetupGet(p => p.ProgramDataPath).Returns(tempPath);
        paths.SetupGet(p => p.WebPath).Returns(tempPath);
        paths.SetupGet(p => p.ProgramSystemPath).Returns(tempPath);
        paths.SetupGet(p => p.DataPath).Returns(tempPath);
        paths.SetupGet(p => p.ImageCachePath).Returns(tempPath);
        paths.SetupGet(p => p.PluginsPath).Returns(tempPath);
        paths.SetupGet(p => p.PluginConfigurationsPath)
            .Returns(tempPath);
        paths.SetupGet(p => p.LogDirectoryPath).Returns(tempPath);
        paths.SetupGet(p => p.ConfigurationDirectoryPath)
            .Returns(tempPath);
        paths.SetupGet(p => p.SystemConfigurationFilePath)
            .Returns(Path.Combine(tempPath, "system.xml"));
        paths.SetupGet(p => p.CachePath).Returns(tempPath);
        paths.SetupGet(p => p.TempDirectory).Returns(tempPath);
        paths.SetupGet(p => p.VirtualDataPath).Returns(tempPath);
        paths.SetupGet(p => p.TrickplayPath).Returns(tempPath);
        paths.SetupGet(p => p.BackupPath).Returns(tempPath);
        var serializer = new Mock<IXmlSerializer>();
        serializer
            .Setup(s => s.DeserializeFromFile(
                typeof(PluginConfiguration),
                It.IsAny<string>()))
            .Returns(configuration);
        serializer
            .Setup(s => s.SerializeToFile(
                It.IsAny<object>(),
                It.IsAny<string>()))
            .Callback<object, string>(
                (value, _) => onSave?.Invoke(value));

        return new Plugin(paths.Object, serializer.Object);
    }
}
