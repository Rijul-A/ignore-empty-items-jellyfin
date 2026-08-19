using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IgnoreEmptyFolders.Tasks;

public class CleanEmptyItemsPostScanTask(
    ILibraryManager libraryManager,
    IUserManager userManager,
    ILoggerFactory loggerFactory) : ILibraryPostScanTask
{
    public Task Run(
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var cleaner = new LibraryCleanupManager(
                libraryManager,
                userManager,
                loggerFactory);
            cleaner.CleanLibrary(progress, cancellationToken);
        }, cancellationToken);
    }
}
