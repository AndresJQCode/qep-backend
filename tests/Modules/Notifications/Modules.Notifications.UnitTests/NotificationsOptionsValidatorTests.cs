using Microsoft.Extensions.Options;
using Modules.Notifications.Infrastructure;
using Modules.Notifications.Infrastructure.Channels;

namespace Modules.Notifications.UnitTests;

public sealed class NotificationsOptionsValidatorTests
{
    private readonly NotificationsOptionsValidator validator = new();

    [Fact]
    public void DefaultLogConfigurationIsValid()
    {
        var result = validator.Validate(null, new NotificationsOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void InfobipWithCompleteCredentialsIsValid()
    {
        var options = new NotificationsOptions
        {
            EmailProvider = NotificationsOptions.InfobipProvider,
            Infobip = new InfobipOptions
            {
                BaseUrl = "https://example.api.infobip.com",
                ApiKey = "secret",
                SenderEmail = "noreply@qep.dev",
            },
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void InfobipMissingApiKeyFails()
    {
        var options = new NotificationsOptions
        {
            EmailProvider = NotificationsOptions.InfobipProvider,
            Infobip = new InfobipOptions
            {
                BaseUrl = "https://example.api.infobip.com",
                SenderEmail = "noreply@qep.dev",
            },
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, message => message.Contains("ApiKey"));
    }

    [Fact]
    public void UnknownProviderFails()
    {
        var options = new NotificationsOptions { EmailProvider = "smtp" };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void NonAbsoluteLoginUrlFails()
    {
        var options = new NotificationsOptions { LoginUrl = "/login" };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
    }
}
