using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IgnoreEmptyFolders;

public class PluginConfigurationWatcher(
    ILibraryManager libraryManager,
    IUserManager userManager,
    ILinkedChildrenService linkedChildrenService,
    ILoggerFactory loggerFactory,
    ILogger<PluginConfigurationWatcher> logger) : IHostedService
{
    private readonly object _handlerLock = new();
    private readonly HashSet<Task> _inFlightHandlers = [];
    private bool _stopping;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_handlerLock)
        {
            _stopping = false;
        }

        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged +=
                OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_handlerLock)
        {
            _stopping = true;
        }

        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged -=
                OnConfigurationChanged;
        }

        Task[] handlers;
        lock (_handlerLock)
        {
            handlers = [.. _inFlightHandlers];
        }

        if (handlers.Length > 0)
        {
            await Task.WhenAll(handlers).WaitAsync(cancellationToken);
        }
    }

    private async void OnConfigurationChanged(
        object? sender, BasePluginConfiguration e)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_handlerLock)
        {
            if (_stopping)
            {
                return;
            }

            _inFlightHandlers.Add(completion.Task);
        }

        try
        {
            await HandleConfigurationChangedAsync()
                .ConfigureAwait(false);
            completion.TrySetResult(true);
        }
        catch (Exception ex)
        {
            completion.TrySetResult(true);
            logger.LogError(
                ex,
                "Ignore Empty Items: Failed to handle " +
                "configuration change");
        }
        finally
        {
            lock (_handlerLock)
            {
                _inFlightHandlers.Remove(completion.Task);
            }
        }
    }

    private async Task HandleConfigurationChangedAsync()
    {
        try
        {
            var manager = new LibraryCleanupManager(
                libraryManager,
                userManager,
                loggerFactory,
                linkedChildrenService);

            await manager.HandleConfigurationChangedAsync(
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Ignore Empty Items: Failed to handle " +
                "configuration change");
        }
    }
}
