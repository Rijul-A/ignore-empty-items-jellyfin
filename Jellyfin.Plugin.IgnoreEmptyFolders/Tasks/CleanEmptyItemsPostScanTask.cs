using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using Microsoft.Extensions.Logging;
namespace Jellyfin.Plugin.IgnoreEmptyFolders.Tasks;

public class CleanEmptyItemsPostScanTask(
    ILibraryManager libraryManager,
    IUserManager userManager,
    ILinkedChildrenService linkedChildrenService,
    ILoggerFactory loggerFactory) : ILibraryPostScanTask
{
    public async Task Run(
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
}
