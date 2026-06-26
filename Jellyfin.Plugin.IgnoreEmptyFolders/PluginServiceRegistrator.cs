using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.IgnoreEmptyFolders;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(
        IServiceCollection serviceCollection,
        IServerApplicationHost applicationHost)
    {
        serviceCollection
            .AddHostedService<
            PluginConfigurationWatcher>();
        serviceCollection
            .AddScoped<
                IEventConsumer<UserCreatedEventArgs>,
                UserCreatedHandler>();
    }
}
