using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IgnoreEmptyFolders.Tasks;

public class CleanEmptyItemsScheduledTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<CleanEmptyItemsScheduledTask> _logger;

    public CleanEmptyItemsScheduledTask(
        ILibraryManager libraryManager,
        ILogger<CleanEmptyItemsScheduledTask> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public string Name => "Clean Empty Items";

    public string Key => "IgnoreEmptyFoldersCleanEmptyItems";

    public string Description => "Removes TV shows, seasons and movies that have no video files from the library.";

    public string Category => "Library";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var cleaner = new EmptyItemCleaner(_libraryManager, _logger);
            cleaner.CleanLibrary(progress, cancellationToken);
        }, cancellationToken);
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
