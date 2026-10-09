using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IgnoreEmptyFolders.Tasks;

public class CleanEmptyItemsScheduledTask(
    ILibraryManager libraryManager,
    IUserManager userManager,
    ILinkedChildrenService linkedChildrenService,
    ILoggerFactory loggerFactory) : IScheduledTask
{
    public string Name => "Clean Empty Items";

    public string Key => "IgnoreEmptyFoldersCleanEmptyItems";

    public string Description =>
        "Removes TV shows, seasons, movies, music artists, " +
        "albums, collections, folders and playlists that " +
        "have no media files from the library.";

    public string Category => "Library";

    public async Task ExecuteAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var cleaner = new LibraryCleanupManager(
            libraryManager,
            userManager,
            loggerFactory,
            linkedChildrenService);
        await cleaner.CleanLibraryAsync(progress, cancellationToken);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(24).Ticks
        };
    }
}
