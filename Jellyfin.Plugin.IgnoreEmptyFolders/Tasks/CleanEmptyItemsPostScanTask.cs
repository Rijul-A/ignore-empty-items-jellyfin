using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IgnoreEmptyFolders.Tasks;

public class CleanEmptyItemsPostScanTask : ILibraryPostScanTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<CleanEmptyItemsPostScanTask> _logger;

    public CleanEmptyItemsPostScanTask(
        ILibraryManager libraryManager,
        ILogger<CleanEmptyItemsPostScanTask> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var cleaner = new EmptyItemCleaner(_libraryManager, _logger);
            cleaner.CleanLibrary(progress, cancellationToken);
        }, cancellationToken);
    }
}
