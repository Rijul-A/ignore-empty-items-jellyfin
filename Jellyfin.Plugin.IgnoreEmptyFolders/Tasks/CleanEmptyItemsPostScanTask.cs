using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IgnoreEmptyFolders.Tasks;

public class CleanEmptyItemsPostScanTask(
    ILibraryManager libraryManager,
    IUserManager userManager,
    ILoggerFactory loggerFactory) : ILibraryPostScanTask
{
    public async Task Run(
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var cleaner = new LibraryCleanupManager(
            libraryManager,
            userManager,
            loggerFactory);
        await cleaner.CleanLibraryAsync(progress, cancellationToken);
    }
}
