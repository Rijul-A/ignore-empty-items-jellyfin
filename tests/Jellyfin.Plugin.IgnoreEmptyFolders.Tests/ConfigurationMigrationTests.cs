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
            {
                Assert.Contains(
                    BaseItemKind.Movie,
                    query.IncludeItemTypes);
                Assert.True(query.IncludeOwnedItems);
                Assert.False(query.GroupByPresentationUniqueKey);
                Assert.Null(query.IsVirtualItem);
                Assert.Null(query.IsMissing);
                return [movie];
            });
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
                query.IsVirtualItem is null &&
                query.IsMissing is null &&
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
                if (query.IncludeItemTypes.Contains(
                    BaseItemKind.Movie))
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
        Assert.Single(saves);
        Assert.Equal(["hidden"], configuration.PendingHideTags);
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

        string[] persistedItemTags = ["A"];
        string[] persistedBlockedTags = ["A"];
        var user = new User("user", "auth", "reset");
        var itemUpdateStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseItemUpdate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var library = new Mock<ILibraryManager>();
        library
            .Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
            {
                Assert.True(query.IncludeOwnedItems);
                Assert.False(query.GroupByPresentationUniqueKey);
                var tag = query.Tags.Single();
                return persistedItemTags.Contains(
                        tag,
                        StringComparer.OrdinalIgnoreCase)
                    ? [new Movie { Tags = [.. persistedItemTags] }]
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

                persistedItemTags = [.. updatedItem.Tags];
            });

        var userManager = new Mock<IUserManager>();
        userManager.Setup(m => m.GetUsers()).Returns([user]);
        userManager
            .Setup(m => m.GetUserDto(user, It.IsAny<string>()))
            .Returns(() => new UserDto
            {
                Policy = new UserPolicy
                {
                    BlockedTags = [.. persistedBlockedTags]
                }
            });
        userManager
            .Setup(m => m.UpdatePolicyAsync(
                user.Id,
                It.IsAny<UserPolicy>()))
            .Returns((Guid _, UserPolicy policy) =>
            {
                persistedBlockedTags = [.. policy.BlockedTags];
                return Task.CompletedTask;
            });

        var manager = new LibraryCleanupManager(
            library.Object,
            userManager.Object,
            NullLoggerFactory.Instance);
        var migration = manager.HandleConfigurationChangedAsync(
            TestContext.Current.CancellationToken);

        try
        {
            await itemUpdateStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.Equal(["A"], persistedItemTags);
            Assert.Equal(["A", "B"], persistedBlockedTags);
            Assert.Contains(configurationB, savedConfigurations);
            Assert.Equal(["A", "B"], configurationB.PendingHideTags);
            Assert.Equal("A", configurationB.PreviousHideTag);
            plugin.UpdateConfiguration(configurationC);
        }
        finally
        {
            releaseItemUpdate.TrySetResult(true);
            await migration;
        }

        Assert.Equal(["C"], persistedItemTags);
        Assert.Equal(["C"], persistedBlockedTags);
        Assert.Equal("C", configurationC.PreviousHideTag);
        Assert.True(configurationC.HideInsteadOfDeleteWasActive);
        Assert.Contains(configurationB, savedConfigurations);
        Assert.Contains(configurationC, savedConfigurations);
        Assert.Equal(["C"], configurationC.PendingHideTags);
    }

    [Fact]
    public async Task UnpersistedTagChangeRetainsOldBlockForLaterRetry()
    {
        var config = new PluginConfiguration
        {
            HideInsteadOfDelete = true,
            HideTag = "B",
            PreviousHideTag = "A",
            HideInsteadOfDeleteWasActive = true
        };
        var saved = new List<object>();
        _ = CreatePlugin(config, saved.Add);
        var itemId = Guid.NewGuid();
        string[] persistedTags = ["A"];
        string[] persistedBlockedTags = ["A"];
        var persistUpdates = false;
        var queries = 0;
        var updates = 0;
        var library = new Mock<ILibraryManager>();
        library
            .Setup(m => m.GetItemList(
                It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
            {
                queries++;
                Assert.True(query.IncludeOwnedItems);
                Assert.False(query.GroupByPresentationUniqueKey);
                var tag = query.Tags.Single();
                return persistedTags.Contains(
                        tag,
                        StringComparer.OrdinalIgnoreCase)
                    ? [new Movie
                    {
                        Id = itemId,
                        Tags = [.. persistedTags]
                    }]
                    : [];
            });
        library
            .Setup(m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(),
                It.IsAny<BaseItem>(),
                It.IsAny<ItemUpdateType>(),
                It.IsAny<CancellationToken>()))
            .Returns((
                BaseItem item,
                BaseItem _,
                ItemUpdateType _,
                CancellationToken _) =>
            {
                updates++;
                if (persistUpdates)
                {
                    persistedTags = [.. item.Tags];
                }

                return Task.CompletedTask;
            });
        var user = new User("user", "auth", "reset");
        var users = new Mock<IUserManager>();
        users.Setup(m => m.GetUsers()).Returns([user]);
        users
            .Setup(m => m.GetUserDto(user, It.IsAny<string>()))
            .Returns(() => new UserDto
            {
                Policy = new UserPolicy
                {
                    BlockedTags = [.. persistedBlockedTags]
                }
            });
        users
            .Setup(m => m.UpdatePolicyAsync(
                user.Id,
                It.IsAny<UserPolicy>()))
            .Returns((Guid _, UserPolicy policy) =>
            {
                persistedBlockedTags = [.. policy.BlockedTags];
                return Task.CompletedTask;
            });
        var manager = new LibraryCleanupManager(
            library.Object,
            users.Object,
            NullLoggerFactory.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.HandleConfigurationChangedAsync(
                TestContext.Current.CancellationToken));
        Assert.Equal(2, queries);
        Assert.Equal(1, updates);
        Assert.Equal(["A"], persistedTags);
        Assert.Equal(["A", "B"], persistedBlockedTags);
        Assert.Equal("A", config.PreviousHideTag);
        Assert.Contains(config, saved);
        Assert.Equal(["A", "B"], config.PendingHideTags);

        persistUpdates = true;
        await manager.HandleConfigurationChangedAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(4, queries);
        Assert.Equal(2, updates);
        Assert.Equal(["B"], persistedTags);
        Assert.Equal(["B"], persistedBlockedTags);
        Assert.Equal("B", config.PreviousHideTag);
        Assert.Contains(config, saved);
        Assert.Equal(["B"], config.PendingHideTags);
    }

    [Fact]
    public async Task
        FailedMigrationRecoversBothTagPopulationsAfterRestart()
    {
        var originalConfiguration = new PluginConfiguration
        {
            HideInsteadOfDelete = true,
            HideTag = "B",
            PreviousHideTag = "A",
            HideInsteadOfDeleteWasActive = true
        };
        var savedSnapshots = new List<PluginConfiguration>();
        static PluginConfiguration Snapshot(
            PluginConfiguration source) =>
            new()
            {
                HideInsteadOfDelete = source.HideInsteadOfDelete,
                HideTag = source.HideTag,
                PreviousHideTag = source.PreviousHideTag,
                PendingHideTags = [.. source.PendingHideTags],
                HideInsteadOfDeleteWasActive =
                    source.HideInsteadOfDeleteWasActive,
                HideTagSkipAdmins = source.HideTagSkipAdmins
            };

        _ = CreatePlugin(
            originalConfiguration,
            value => savedSnapshots.Add(
                Snapshot((PluginConfiguration)value)));
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var persistedItems = new Dictionary<Guid, string[]>
        {
            [firstId] = ["A"],
            [secondId] = ["A"]
        };
        string[] persistedBlocks = ["A"];
        var user = new User("user", "auth", "reset");
        var failSecondUpdate = true;
        var itemUpdates = 0;
        var policyUpdates = 0;
        var library = new Mock<ILibraryManager>();
        library
            .Setup(m => m.GetItemList(
                It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
            {
                Assert.True(query.IncludeOwnedItems);
                Assert.False(query.GroupByPresentationUniqueKey);
                return persistedItems
                    .Where(entry => entry.Value.Contains(
                        query.Tags.Single(),
                        StringComparer.OrdinalIgnoreCase))
                    .Select(entry => (BaseItem)new Movie
                    {
                        Id = entry.Key,
                        Tags = [.. entry.Value]
                    })
                    .ToList();
            });
        library
            .Setup(m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(),
                It.IsAny<BaseItem>(),
                It.IsAny<ItemUpdateType>(),
                It.IsAny<CancellationToken>()))
            .Returns((
                BaseItem item,
                BaseItem _,
                ItemUpdateType _,
                CancellationToken _) =>
            {
                itemUpdates++;
                Assert.NotEmpty(savedSnapshots);
                Assert.Equal(
                    failSecondUpdate ? ["A", "B"] : ["A", "B", "C"],
                    savedSnapshots[^1].PendingHideTags);
                Assert.Contains(
                    failSecondUpdate ? "B" : "C",
                    persistedBlocks);
                if (failSecondUpdate && item.Id == secondId)
                {
                    return Task.FromException(
                        new InvalidOperationException(
                            "Second item could not be saved"));
                }

                persistedItems[item.Id] = [.. item.Tags];
                return Task.CompletedTask;
            });
        var users = new Mock<IUserManager>();
        users.Setup(m => m.GetUsers()).Returns([user]);
        users
            .Setup(m => m.GetUserDto(user, It.IsAny<string>()))
            .Returns(() => new UserDto
            {
                Policy = new UserPolicy
                {
                    BlockedTags = [.. persistedBlocks]
                }
            });
        users
            .Setup(m => m.UpdatePolicyAsync(
                user.Id,
                It.IsAny<UserPolicy>()))
            .Returns((Guid _, UserPolicy policy) =>
            {
                policyUpdates++;
                Assert.NotEmpty(savedSnapshots);
                Assert.Equal(
                    failSecondUpdate ? ["A", "B"] : ["A", "B", "C"],
                    savedSnapshots[^1].PendingHideTags);
                persistedBlocks = [.. policy.BlockedTags];
                return Task.CompletedTask;
            });
        var firstManager = new LibraryCleanupManager(
            library.Object,
            users.Object,
            NullLoggerFactory.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => firstManager.HandleConfigurationChangedAsync(
                TestContext.Current.CancellationToken));

        Assert.Equal(2, itemUpdates);
        Assert.Equal(1, policyUpdates);
        Assert.Equal(["B"], persistedItems[firstId]);
        Assert.Equal(["A"], persistedItems[secondId]);
        Assert.Equal(["A", "B"], persistedBlocks);
        Assert.Equal("A", originalConfiguration.PreviousHideTag);
        Assert.Equal("A", savedSnapshots[^1].PreviousHideTag);
        Assert.Equal(["A", "B"], savedSnapshots[^1].PendingHideTags);
        Assert.NotSame(
            originalConfiguration.PendingHideTags,
            savedSnapshots[^1].PendingHideTags);

        var restartedConfiguration = Snapshot(savedSnapshots[^1]);
        restartedConfiguration.HideTag = "C";
        _ = CreatePlugin(
            restartedConfiguration,
            value => savedSnapshots.Add(
                Snapshot((PluginConfiguration)value)));
        var restartedManager = new LibraryCleanupManager(
            library.Object,
            users.Object,
            NullLoggerFactory.Instance);
        failSecondUpdate = false;

        await restartedManager.HandleConfigurationChangedAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(["C"], persistedItems[firstId]);
        Assert.Equal(["C"], persistedItems[secondId]);
        Assert.Equal(["C"], persistedBlocks);
        Assert.Equal("C", restartedConfiguration.PreviousHideTag);
        Assert.Equal(["C"], restartedConfiguration.PendingHideTags);
        Assert.Equal("C", savedSnapshots[^1].PreviousHideTag);
        Assert.Equal(["C"], savedSnapshots[^1].PendingHideTags);
    }

    [Fact]
    public async Task
        CleanupRecoversInterruptedMigrationAfterRestart()
    {
        var originalConfiguration = new PluginConfiguration
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
        static PluginConfiguration Snapshot(
            PluginConfiguration source) =>
            new()
            {
                HideInsteadOfDelete = source.HideInsteadOfDelete,
                HideTag = source.HideTag,
                PreviousHideTag = source.PreviousHideTag,
                PendingHideTags = [.. source.PendingHideTags],
                HideInsteadOfDeleteWasActive =
                    source.HideInsteadOfDeleteWasActive,
                HideTagSkipAdmins = source.HideTagSkipAdmins,
                DeleteEmptyShows = source.DeleteEmptyShows,
                DeleteEmptySeasons = source.DeleteEmptySeasons,
                DeleteEmptyMovies = source.DeleteEmptyMovies,
                DeleteEmptyMusicArtists =
                    source.DeleteEmptyMusicArtists,
                DeleteEmptyMusicAlbums = source.DeleteEmptyMusicAlbums,
                DeleteEmptyCollections = source.DeleteEmptyCollections,
                DeleteEmptyFolders = source.DeleteEmptyFolders,
                DeleteEmptyPlaylists = source.DeleteEmptyPlaylists
            };
        var savedSnapshots = new List<PluginConfiguration>();
        _ = CreatePlugin(
            originalConfiguration,
            value => savedSnapshots.Add(
                Snapshot((PluginConfiguration)value)));

        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var persistedItems = new Dictionary<Guid, string[]>
        {
            [firstId] = ["A"],
            [secondId] = ["A"]
        };
        string[] persistedBlocks = ["A"];
        var failSecondUpdate = true;
        var library = new Mock<ILibraryManager>();
        library
            .Setup(m => m.GetItemList(
                It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
            {
                return persistedItems
                    .Where(entry => entry.Value.Contains(
                        query.Tags.Single(),
                        StringComparer.OrdinalIgnoreCase))
                    .Select(entry => (BaseItem)new Movie
                    {
                        Id = entry.Key,
                        Tags = [.. entry.Value]
                    })
                    .ToList();
            });
        library
            .Setup(m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(),
                It.IsAny<BaseItem>(),
                It.IsAny<ItemUpdateType>(),
                It.IsAny<CancellationToken>()))
            .Returns((
                BaseItem item,
                BaseItem _,
                ItemUpdateType _,
                CancellationToken _) =>
            {
                Assert.NotEmpty(savedSnapshots);
                Assert.Equal(
                    ["A", "B"], savedSnapshots[^1].PendingHideTags);
                Assert.Contains("B", persistedBlocks);
                if (failSecondUpdate && item.Id == secondId)
                {
                    return Task.FromException(
                        new InvalidOperationException(
                            "Second item could not be saved"));
                }

                persistedItems[item.Id] = [.. item.Tags];
                return Task.CompletedTask;
            });
        var user = new User("user", "auth", "reset");
        var users = new Mock<IUserManager>();
        users.Setup(m => m.GetUsers()).Returns([user]);
        users
            .Setup(m => m.GetUserDto(user, It.IsAny<string>()))
            .Returns(() => new UserDto
            {
                Policy = new UserPolicy
                {
                    BlockedTags = [.. persistedBlocks]
                }
            });
        users
            .Setup(m => m.UpdatePolicyAsync(
                user.Id,
                It.IsAny<UserPolicy>()))
            .Returns((Guid _, UserPolicy policy) =>
            {
                Assert.NotEmpty(savedSnapshots);
                Assert.Equal(
                    ["A", "B"], savedSnapshots[^1].PendingHideTags);
                persistedBlocks = [.. policy.BlockedTags];
                return Task.CompletedTask;
            });
        var firstManager = new LibraryCleanupManager(
            library.Object,
            users.Object,
            NullLoggerFactory.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => firstManager.HandleConfigurationChangedAsync(
                TestContext.Current.CancellationToken));
        Assert.Equal(["B"], persistedItems[firstId]);
        Assert.Equal(["A"], persistedItems[secondId]);
        Assert.Equal(["A", "B"], persistedBlocks);
        Assert.Equal("A", savedSnapshots[^1].PreviousHideTag);
        Assert.Equal(["A", "B"], savedSnapshots[^1].PendingHideTags);

        var restartedConfiguration = Snapshot(savedSnapshots[^1]);
        Assert.Equal("B", restartedConfiguration.HideTag);
        var savesBeforeCleanup = savedSnapshots.Count;
        _ = CreatePlugin(
            restartedConfiguration,
            value => savedSnapshots.Add(
                Snapshot((PluginConfiguration)value)));
        Assert.Equal(savesBeforeCleanup, savedSnapshots.Count);
        failSecondUpdate = false;
        var restartedManager = new LibraryCleanupManager(
            library.Object,
            users.Object,
            NullLoggerFactory.Instance);

        await restartedManager.CleanLibraryAsync(
            new Progress<double>(),
            TestContext.Current.CancellationToken);

        Assert.Equal(["B"], persistedItems[firstId]);
        Assert.Equal(["B"], persistedItems[secondId]);
        Assert.Equal(["B"], persistedBlocks);
        Assert.Equal("B", restartedConfiguration.PreviousHideTag);
        Assert.Equal(["B"], restartedConfiguration.PendingHideTags);
        Assert.True(savedSnapshots.Count > savesBeforeCleanup);
        Assert.Equal("B", savedSnapshots[^1].PreviousHideTag);
        Assert.Equal(["B"], savedSnapshots[^1].PendingHideTags);
    }

    [Fact]
    public async Task MigrationCanonicalizesExistingDestinationTags()
    {
        var configuration = new PluginConfiguration
        {
            HideInsteadOfDelete = true,
            HideTag = "B",
            PreviousHideTag = "A",
            HideInsteadOfDeleteWasActive = true
        };
        _ = CreatePlugin(configuration);
        var itemId = Guid.NewGuid();
        string[] persistedTags = ["a", "b", "B", "other"];
        string[] persistedBlocks = ["A", "b"];
        var library = new Mock<ILibraryManager>();
        library
            .Setup(m => m.GetItemList(
                It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
                persistedTags.Contains(
                    query.Tags.Single(),
                    StringComparer.OrdinalIgnoreCase)
                    ? [new Movie
                    {
                        Id = itemId,
                        Tags = [.. persistedTags]
                    }]
                    : []);
        library
            .Setup(m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(),
                It.IsAny<BaseItem>(),
                It.IsAny<ItemUpdateType>(),
                It.IsAny<CancellationToken>()))
            .Returns((
                BaseItem item,
                BaseItem _,
                ItemUpdateType _,
                CancellationToken _) =>
            {
                persistedTags = [.. item.Tags];
                return Task.CompletedTask;
            });
        var user = new User("user", "auth", "reset");
        var users = new Mock<IUserManager>();
        users.Setup(m => m.GetUsers()).Returns([user]);
        users
            .Setup(m => m.GetUserDto(user, It.IsAny<string>()))
            .Returns(() => new UserDto
            {
                Policy = new UserPolicy
                {
                    BlockedTags = [.. persistedBlocks]
                }
            });
        users
            .Setup(m => m.UpdatePolicyAsync(
                user.Id,
                It.IsAny<UserPolicy>()))
            .Returns((Guid _, UserPolicy policy) =>
            {
                persistedBlocks = [.. policy.BlockedTags];
                return Task.CompletedTask;
            });
        var manager = new LibraryCleanupManager(
            library.Object,
            users.Object,
            NullLoggerFactory.Instance);

        await manager.HandleConfigurationChangedAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(["other", "B"], persistedTags);
        Assert.DoesNotContain(
            persistedTags,
            tag => tag.Equals("A", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            persistedBlocks,
            tag => tag.Equals("A", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            persistedBlocks,
            tag => tag.Equals("B", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["B"], configuration.PendingHideTags);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task FailedMigrationRestoresCachedTagsBeforeRetry(
        bool replaceTag,
        bool cancelUpdate)
    {
        var config = new PluginConfiguration
        {
            HideInsteadOfDelete = replaceTag,
            HideTag = "B",
            PreviousHideTag = "A",
            HideInsteadOfDeleteWasActive = true
        };
        var saved = new List<object>();
        _ = CreatePlugin(config, saved.Add);
        string[] originalTags = ["A", "unrelated"];
        var cachedItem = new Movie { Tags = originalTags };
        string[] blockedTags = ["A"];
        var library = new Mock<ILibraryManager>();
        library
            .Setup(m => m.GetItemList(
                It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
                cachedItem.Tags.Contains(
                    query.Tags.Single(),
                    StringComparer.OrdinalIgnoreCase)
                    ? [cachedItem]
                    : []);
        var failedUpdate = new InvalidOperationException(
            "Item persistence failed");
        using var cancellation = new CancellationTokenSource();
        var updates = 0;
        library
            .Setup(m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(),
                It.IsAny<BaseItem>(),
                It.IsAny<ItemUpdateType>(),
                It.IsAny<CancellationToken>()))
            .Returns((
                BaseItem item,
                BaseItem _,
                ItemUpdateType _,
                CancellationToken token) =>
            {
                Assert.Same(cachedItem, item);
                updates++;
                Assert.Equal(
                    replaceTag ? ["unrelated", "B"] : ["unrelated"],
                    item.Tags);
                if (updates != 1)
                {
                    return Task.CompletedTask;
                }

                if (cancelUpdate)
                {
                    cancellation.Cancel();
                    return Task.FromCanceled(token);
                }

                return Task.FromException(failedUpdate);
            });
        var user = new User("user", "auth", "reset");
        var users = new Mock<IUserManager>();
        users.Setup(m => m.GetUsers()).Returns([user]);
        users
            .Setup(m => m.GetUserDto(user, It.IsAny<string>()))
            .Returns(() => new UserDto
            {
                Policy = new UserPolicy
                {
                    BlockedTags = [.. blockedTags]
                }
            });
        users
            .Setup(m => m.UpdatePolicyAsync(
                user.Id,
                It.IsAny<UserPolicy>()))
            .Returns((Guid _, UserPolicy policy) =>
            {
                blockedTags = [.. policy.BlockedTags];
                return Task.CompletedTask;
            });
        var manager = new LibraryCleanupManager(
            library.Object,
            users.Object,
            NullLoggerFactory.Instance);

        if (cancelUpdate)
        {
            var exception = await Assert.ThrowsAnyAsync<
                OperationCanceledException>(
                () => manager.HandleConfigurationChangedAsync(
                    cancellation.Token));
            Assert.Equal(
                cancellation.Token, exception.CancellationToken);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<
                InvalidOperationException>(
                () => manager.HandleConfigurationChangedAsync(
                    cancellation.Token));
            Assert.Same(failedUpdate, exception);
        }

        Assert.Equal(1, updates);
        Assert.Same(originalTags, cachedItem.Tags);
        Assert.Equal(["A", "unrelated"], cachedItem.Tags);
        Assert.Equal(
            replaceTag ? ["A", "B"] : ["A"],
            blockedTags);
        Assert.Equal("A", config.PreviousHideTag);
        Assert.True(config.HideInsteadOfDeleteWasActive);
        Assert.Contains(config, saved);
        Assert.Equal(
            replaceTag ? ["A", "B"] : ["A"],
            config.PendingHideTags);

        await manager.HandleConfigurationChangedAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(2, updates);
        Assert.Equal(
            replaceTag ? ["unrelated", "B"] : ["unrelated"],
            cachedItem.Tags);
        Assert.Equal(
            replaceTag ? ["B"] : Array.Empty<string>(),
            blockedTags);
        Assert.Equal(
            replaceTag ? "B" : string.Empty,
            config.PreviousHideTag);
        Assert.Equal(replaceTag, config.HideInsteadOfDeleteWasActive);
        Assert.Equal(
            replaceTag ? ["B"] : Array.Empty<string>(),
            config.PendingHideTags);
        Assert.Contains(config, saved);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task MigrationRetainsOldBlockUntilAllItemsArePersisted(
        bool replaceTag,
        bool failLastUpdate)
    {
        var config = new PluginConfiguration
        {
            HideInsteadOfDelete = replaceTag,
            HideTag = "B",
            PreviousHideTag = "A",
            HideInsteadOfDeleteWasActive = true,
            HideTagSkipAdmins = true
        };
        _ = CreatePlugin(config);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var persistedItems = new Dictionary<Guid, string[]>
        {
            [firstId] = ["A"],
            [secondId] = ["A"]
        };
        var first = new User("first", "auth", "reset")
        {
            Id = Guid.NewGuid()
        };
        var second = new User("second", "auth", "reset")
        {
            Id = Guid.NewGuid()
        };
        var admin = new User("admin", "auth", "reset")
        {
            Id = Guid.NewGuid()
        };
        var persistedPolicies = new Dictionary<Guid, string[]>
        {
            [first.Id] = ["A"],
            [second.Id] = ["A"],
            [admin.Id] = ["A"]
        };
        var secondEnrollmentStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondEnrollment = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstUpdateStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstUpdate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondUpdateStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondUpdate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var library = new Mock<ILibraryManager>();
        library
            .Setup(m => m.GetItemList(
                It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
            {
                Assert.True(query.IncludeOwnedItems);
                Assert.False(query.GroupByPresentationUniqueKey);
                Assert.Null(query.IsVirtualItem);
                var tag = query.Tags.Single();
                return persistedItems
                    .Where(entry => entry.Value.Contains(
                        tag,
                        StringComparer.OrdinalIgnoreCase))
                    .Select(entry => (BaseItem)new Movie
                    {
                        Id = entry.Key,
                        Tags = [.. entry.Value]
                    })
                    .ToList();
            });
        library
            .Setup(m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(),
                It.IsAny<BaseItem>(),
                It.IsAny<ItemUpdateType>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (
                BaseItem item,
                BaseItem _,
                ItemUpdateType _,
                CancellationToken _) =>
            {
                if (item.Id == firstId)
                {
                    firstUpdateStarted.TrySetResult(true);
                    await releaseFirstUpdate.Task;
                }
                else
                {
                    secondUpdateStarted.TrySetResult(true);
                    await releaseSecondUpdate.Task;
                }

                if (failLastUpdate && item.Id == secondId)
                {
                    throw new InvalidOperationException(
                        "Item persistence failed");
                }

                persistedItems[item.Id] = [.. item.Tags];
            });
        var users = new Mock<IUserManager>();
        users.Setup(m => m.GetUsers()).Returns([first, second, admin]);
        users
            .Setup(m => m.GetUserDto(
                It.IsAny<User>(),
                It.IsAny<string>()))
            .Returns((User user, string _) => new UserDto
            {
                Policy = new UserPolicy
                {
                    IsAdministrator = user.Id == admin.Id,
                    BlockedTags = [.. persistedPolicies[user.Id]]
                }
            });
        users
            .Setup(m => m.UpdatePolicyAsync(
                It.IsAny<Guid>(),
                It.IsAny<UserPolicy>()))
            .Returns(async (Guid id, UserPolicy policy) =>
            {
                if (replaceTag &&
                    id == second.Id &&
                    policy.BlockedTags.Contains("B") &&
                    !secondEnrollmentStarted.Task.IsCompleted)
                {
                    secondEnrollmentStarted.TrySetResult(true);
                    await releaseSecondEnrollment.Task;
                }

                persistedPolicies[id] = [.. policy.BlockedTags];
            });
        var manager = new LibraryCleanupManager(
            library.Object,
            users.Object,
            NullLoggerFactory.Instance);
        var migration = manager.HandleConfigurationChangedAsync(
            TestContext.Current.CancellationToken);

        try
        {
            if (replaceTag)
            {
                await secondEnrollmentStarted.Task.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);
                Assert.Equal(["A", "B"], persistedPolicies[first.Id]);
                Assert.Equal(["A"], persistedPolicies[second.Id]);
                Assert.Equal(["A"], persistedPolicies[admin.Id]);
                Assert.False(firstUpdateStarted.Task.IsCompleted);
                releaseSecondEnrollment.TrySetResult(true);
            }

            await firstUpdateStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.Equal(["A"], persistedItems[firstId]);
            Assert.Equal(["A"], persistedItems[secondId]);
            Assert.Equal(
                replaceTag ? ["A", "B"] : ["A"],
                persistedPolicies[first.Id]);
            Assert.Equal(
                replaceTag ? ["A", "B"] : ["A"],
                persistedPolicies[second.Id]);
            Assert.Equal(["A"], persistedPolicies[admin.Id]);
            releaseFirstUpdate.TrySetResult(true);

            await secondUpdateStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.Equal(
                replaceTag ? ["B"] : Array.Empty<string>(),
                persistedItems[firstId]);
            Assert.Equal(["A"], persistedItems[secondId]);
            Assert.Contains("A", persistedPolicies[first.Id]);
            Assert.Contains("A", persistedPolicies[second.Id]);
            Assert.Contains("A", persistedPolicies[admin.Id]);
        }
        finally
        {
            releaseSecondEnrollment.TrySetResult(true);
            releaseFirstUpdate.TrySetResult(true);
            releaseSecondUpdate.TrySetResult(true);
            if (failLastUpdate)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => migration);
            }
            else
            {
                await migration;
            }
        }

        if (failLastUpdate)
        {
            Assert.Equal(["A"], persistedItems[secondId]);
            Assert.Equal(
                replaceTag ? ["A", "B"] : ["A"],
                persistedPolicies[first.Id]);
            Assert.Equal(
                replaceTag ? ["A", "B"] : ["A"],
                persistedPolicies[second.Id]);
            Assert.Equal(["A"], persistedPolicies[admin.Id]);
            Assert.Equal("A", config.PreviousHideTag);
            Assert.Equal(
                replaceTag ? ["A", "B"] : ["A"],
                config.PendingHideTags);
            return;
        }

        Assert.Equal(
            replaceTag ? ["B"] : Array.Empty<string>(),
            persistedItems[firstId]);
        Assert.Equal(
            replaceTag ? ["B"] : Array.Empty<string>(),
            persistedItems[secondId]);
        Assert.Equal(
            replaceTag ? ["B"] : Array.Empty<string>(),
            persistedPolicies[first.Id]);
        Assert.Equal(
            replaceTag ? ["B"] : Array.Empty<string>(),
            persistedPolicies[second.Id]);
        Assert.Empty(persistedPolicies[admin.Id]);
        Assert.Equal(
            replaceTag ? "B" : string.Empty,
            config.PreviousHideTag);
        Assert.Equal(
            replaceTag ? ["B"] : Array.Empty<string>(),
            config.PendingHideTags);
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
                query.IncludeOwnedItems &&
                !query.GroupByPresentationUniqueKey &&
                query.IsVirtualItem is null &&
                query.IsMissing is null
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
            Path = untag ? "/media/first.mkv" : null,
            Tags = untag ? ["hidden"] : []
        };
        var second = new Movie
        {
            Id = Guid.NewGuid(),
            IsVirtualItem = !untag,
            Path = untag ? "/media/second.mkv" : null,
            Tags = untag ? ["hidden"] : []
        };
        var library = new Mock<ILibraryManager>();
        library
            .Setup(m => m.GetItemList(
                It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) =>
                query.IncludeItemTypes.Contains(BaseItemKind.Movie) &&
                query.IncludeOwnedItems &&
                !query.GroupByPresentationUniqueKey &&
                query.IsVirtualItem is null &&
                query.IsMissing is null
                    ? [first, second]
                    : []);
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
