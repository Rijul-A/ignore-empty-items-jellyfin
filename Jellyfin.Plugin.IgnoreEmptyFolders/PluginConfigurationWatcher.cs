using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IgnoreEmptyFolders;

public class PluginConfigurationWatcher(
    ILibraryManager libraryManager,
    IUserManager userManager,
    ILoggerFactory loggerFactory) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged +=
                OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged -=
                OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    private async void OnConfigurationChanged(
        object? sender, BasePluginConfiguration e)
    {
        var manager = new LibraryCleanupManager(
            libraryManager,
            userManager,
            loggerFactory);

        await manager.HandleConfigurationChangedAsync(
            CancellationToken.None);
    }
}
