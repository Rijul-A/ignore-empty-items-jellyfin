using Jellyfin.Plugin.IgnoreEmptyFolders.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IgnoreEmptyFolders.Cleaners;

/// <summary>
/// Base class for item cleaners providing common services.
/// </summary>
public abstract class BaseItemCleaner : IItemCleaner
{
    protected readonly ILibraryManager LibraryManager;
    protected readonly ILogger Logger;

    protected BaseItemCleaner(
        ILibraryManager libraryManager,
        ILogger logger)
    {
        LibraryManager = libraryManager;
        Logger = logger;
    }

    public abstract double Weight { get; }

    public abstract bool IsEnabled(PluginConfiguration config);

    public abstract int Clean(
        PluginConfiguration config,
        IProgress<double>? progress,
        CancellationToken cancellationToken);

    protected void ReportProgress(
        IProgress<double>? progress,
        int current,
        int total)
    {
        if (progress == null || total == 0) return;
        var internalProgress = (double)current / total * 100.0;
        progress.Report(internalProgress);
    }
}
