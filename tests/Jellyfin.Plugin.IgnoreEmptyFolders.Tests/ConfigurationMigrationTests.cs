using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
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
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                secondManager.HandleConfigurationChangedAsync(
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
    public async Task CleanupUsesConfigurationAfterMigrationReconciliation()
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

    [Fact]
    public async Task ConfigurationReplacementDuringMigrationConvergesToLatestTag()
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
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(tempPath);
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
            .Callback<object, string>((value, _) => onSave?.Invoke(value));

        return new Plugin(paths.Object, serializer.Object);
    }
}
