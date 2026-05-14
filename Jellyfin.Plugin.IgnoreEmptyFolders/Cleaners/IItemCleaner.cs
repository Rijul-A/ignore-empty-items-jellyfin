using Jellyfin.Plugin.IgnoreEmptyFolders.Configuration;

namespace Jellyfin.Plugin.IgnoreEmptyFolders.Cleaners;

/// <summary>
/// Interface for library item cleaners.
/// </summary>
public interface IItemCleaner
{
    /// <summary>
    /// Gets the weight of this cleaner for progress reporting.
    /// </summary>
    double Weight { get; }

    /// <summary>
    /// Determines if the cleaner is enabled based on configuration.
    /// </summary>
    bool IsEnabled(PluginConfiguration config);

    /// <summary>
    /// Performs the cleanup operation.
    /// </summary>
    int Clean(
        PluginConfiguration config,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}
