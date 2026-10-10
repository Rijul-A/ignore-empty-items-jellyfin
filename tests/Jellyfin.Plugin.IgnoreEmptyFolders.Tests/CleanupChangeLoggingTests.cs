using Jellyfin.Plugin.IgnoreEmptyFolders.Cleaners;
using Jellyfin.Plugin.IgnoreEmptyFolders.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.IgnoreEmptyFolders.Tests;

public sealed class CleanupChangeLoggingTests
{
    [Fact]
    public async Task HideAndRestoreAreLoggedOnlyAfterSuccessfulUpdates()
    {
        var config = new PluginConfiguration
        {
            HideInsteadOfDelete = true,
            LogDeletions = true
        };
        var empty = NewMovie("Empty");
        var alreadyHidden = NewMovie("Already hidden");
        alreadyHidden.Tags = [config.HideTag];
        var restored = NewMovie("Available");
        restored.Tags = [config.HideTag];
        restored.Path = "/media/available.mkv";
        var alreadyVisible = NewMovie("Already visible");
        alreadyVisible.Path = "/media/visible.mkv";
        var library = LibraryWithMovies(empty, alreadyHidden, restored, alreadyVisible);
        var logger = new Mock<ILogger<MovieCleaner>>();
        var updateFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        library.Setup(m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(), It.IsAny<BaseItem>(),
                ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()))
            .Returns((BaseItem item, BaseItem parent,
                ItemUpdateType updateType, CancellationToken token) =>
                item == empty ? updateFinished.Task : Task.CompletedTask);

        var clean = new MovieCleaner(library.Object, logger.Object)
            .CleanAsync(config, null, CancellationToken.None);

        Assert.False(clean.IsCompleted);
        Assert.Empty(ChangeMessages(logger));
        updateFinished.SetResult();
        Assert.Equal(2, await clean);
        Assert.Contains(config.HideTag, empty.Tags);
        Assert.DoesNotContain(config.HideTag, restored.Tags);
        Assert.Contains(config.HideTag, alreadyHidden.Tags);
        library.Verify(m => m.UpdateItemAsync(
            It.IsAny<BaseItem>(), It.IsAny<BaseItem>(),
            ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.Equal(
            ["Ignore Empty Items: Hid movie \"Empty\"",
             "Ignore Empty Items: Restored \"Available\""],
            ChangeMessages(logger));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedHideOrRestoreDoesNotLogChange(bool restore)
    {
        var config = new PluginConfiguration
        {
            HideInsteadOfDelete = true,
            LogDeletions = true
        };
        var movie = NewMovie("Failed update");
        if (restore)
        {
            movie.Tags = [config.HideTag];
            movie.Path = "/media/available.mkv";
        }
        var originalTags = movie.Tags;
        var library = LibraryWithMovies(movie);
        var logger = new Mock<ILogger<MovieCleaner>>();
        library.Setup(m => m.UpdateItemAsync(
                It.IsAny<BaseItem>(), It.IsAny<BaseItem>(),
                ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()))
            .Returns(Task.FromException(new InvalidOperationException("Save failed")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MovieCleaner(library.Object, logger.Object)
                .CleanAsync(config, null, CancellationToken.None));

        Assert.Equal(originalTags, movie.Tags);
        Assert.Empty(ChangeMessages(logger));
    }

    [Fact]
    public async Task DeleteLogsOnlyCompletedDeletesAndKeepsFailureWarning()
    {
        var config = new PluginConfiguration
        {
            HideInsteadOfDelete = false,
            LogDeletions = true
        };
        var failed = NewMovie("Failed delete");
        var deleted = NewMovie("Deleted");
        var library = LibraryWithMovies(failed, deleted);
        var logger = new Mock<ILogger<MovieCleaner>>();
        library.Setup(m => m.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()))
            .Callback((BaseItem item, DeleteOptions _) =>
            {
                Assert.Empty(ChangeMessages(logger));
                if (item == failed)
                    throw new InvalidOperationException("Delete failed");
            });

        var count = await new MovieCleaner(library.Object, logger.Object)
            .CleanAsync(config, null, CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Equal(
            ["Ignore Empty Items: Deleted movie \"Deleted\""],
            ChangeMessages(logger));
        Assert.Contains(logger.Invocations,
            invocation => invocation.Method.Name == nameof(ILogger.Log) &&
                (LogLevel)invocation.Arguments[0] == LogLevel.Warning &&
                invocation.Arguments[2]?.ToString()?.Contains("Failed delete") == true);
    }

    private static Movie NewMovie(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Tags = []
    };

    private static Mock<ILibraryManager> LibraryWithMovies(params Movie[] movies)
    {
        var library = new Mock<ILibraryManager>();
        library.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(movies.Cast<BaseItem>().ToList());
        return library;
    }

    private static string[] ChangeMessages(Mock<ILogger<MovieCleaner>> logger) =>
        logger.Invocations
            .Where(invocation => invocation.Method.Name == nameof(ILogger.Log) &&
                (LogLevel)invocation.Arguments[0] == LogLevel.Information)
            .Select(invocation => invocation.Arguments[2]?.ToString() ?? string.Empty)
            .Where(message => message.StartsWith("Ignore Empty Items: Hid ", StringComparison.Ordinal) ||
                message.StartsWith("Ignore Empty Items: Restored ", StringComparison.Ordinal) ||
                message.StartsWith("Ignore Empty Items: Deleted ", StringComparison.Ordinal))
            .ToArray();
}
