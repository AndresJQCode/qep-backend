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

    // En Linux, "/login" es una ruta de archivo absoluta valida y Uri.TryCreate(...,
    // UriKind.Absolute, ...) la acepta como file:///login — NonAbsoluteLoginUrlFails pasaba
    // en Windows por la razon equivocada y fallaba en CI. Esta prueba no depende del
    // sistema operativo: ftp:// es una URI absoluta en cualquier plataforma, asi que expone
    // la falta de chequeo de esquema sin importar donde corra.
    [Fact]
    public void NonHttpSchemeLoginUrlFails()
    {
        var options = new NotificationsOptions { LoginUrl = "ftp://example.com/login" };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
    }
}
