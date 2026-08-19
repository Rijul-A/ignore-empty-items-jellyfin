using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IgnoreEmptyFolders;

public class UserCreatedHandler(
    IUserManager userManager,
    ILoggerFactory loggerFactory) : IEventConsumer<UserCreatedEventArgs>
{
    public Task OnEvent(UserCreatedEventArgs eventArgs)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.HideInsteadOfDelete)
        {
            return Task.CompletedTask;
        }

        var logger = loggerFactory.CreateLogger<UserCreatedHandler>();
        var user = eventArgs.Argument;
        var dto = userManager.GetUserDto(user);
        var policy = dto.Policy!;

        if (config.HideTagSkipAdmins && policy.IsAdministrator)
        {
            return Task.CompletedTask;
        }

        if (policy.BlockedTags.Contains(
                config.HideTag,
                StringComparer.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        policy.BlockedTags = [.. policy.BlockedTags, config.HideTag];

        logger.LogInformation(
            "Ignore Empty Items: Added blocked tag \"{Tag}\" " +
            "for new user \"{User}\"",
            config.HideTag,
            user.Username);

        return userManager.UpdatePolicyAsync(user.Id, policy);
    }
}
