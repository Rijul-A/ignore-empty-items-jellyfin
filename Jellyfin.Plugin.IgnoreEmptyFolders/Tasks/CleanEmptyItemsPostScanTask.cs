using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IgnoreEmptyFolders.Tasks;

public class CleanEmptyItemsPostScanTask(
    ILibraryManager libraryManager,
    ILogger logger) : ILibraryPostScanTask
{
    public Task Run(
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var cleaner = new LibraryCleanupManager(
                libraryManager,
                logger);
            cleaner.CleanLibrary(progress, cancellationToken);
        }, cancellationToken);
    }
}
