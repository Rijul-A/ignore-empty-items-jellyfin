namespace Jellyfin.Plugin.IgnoreEmptyFolders.Cleaners;

/// <summary>
/// Centralized progress weights for different cleaner types.
/// </summary>
public static class CleanupWeights
{
    public const double Series = 30.0;
    public const double Movies = 20.0;
    public const double Music = 20.0;
    public const double Containers = 30.0;
}
