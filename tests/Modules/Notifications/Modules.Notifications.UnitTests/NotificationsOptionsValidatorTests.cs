using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Modules.Notifications.Infrastructure;
using Modules.Notifications.Infrastructure.Channels;

namespace Modules.Notifications.UnitTests;

/// <summary>
/// El proveedor `log` es el default: registra el correo y no lo manda. Eso es lo que deja andar
/// el desarrollo local y las pruebas de integración sin aprovisionar Infobip. En producción es
/// una trampa silenciosa — la invitación queda marcada como entregada, el worker no registra
/// ningún error y el correo nunca sale. Mismo criterio que `QuotationsOptionsValidator` con
/// Zenvia: se prefiere que el arranque falle a que el negocio pierda altas sin enterarse.
/// </summary>
public sealed class NotificationsOptionsValidatorTests
{
    private static readonly InfobipOptions CompleteInfobip = new()
    {
        BaseUrl = "https://example.api.infobip.com",
        ApiKey = "secret",
        SenderEmail = "noreply@qep.dev",
    };

    [Fact]
    public void DefaultLogConfigurationIsValid()
    {
        var result = ValidatorFor(Environments.Development)
            .Validate(null, new NotificationsOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void InfobipWithCompleteCredentialsIsValid()
    {
        var options = new NotificationsOptions
        {
            EmailProvider = NotificationsOptions.InfobipProvider,
            Infobip = CompleteInfobip,
        };

        var result = ValidatorFor(Environments.Development).Validate(null, options);

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

        var result = ValidatorFor(Environments.Development).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, message => message.Contains("ApiKey"));
    }

    [Fact]
    public void UnknownProviderFails()
    {
        var options = new NotificationsOptions { EmailProvider = "smtp" };

        var result = ValidatorFor(Environments.Development).Validate(null, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void NonAbsoluteInvitationUrlFails()
    {
        var options = new NotificationsOptions { InvitationUrl = "/invitations" };

        var result = ValidatorFor(Environments.Development).Validate(null, options);

        Assert.True(result.Failed);
    }

    // En Linux, "/invitations" es una ruta de archivo absoluta valida y Uri.TryCreate(...,
    // UriKind.Absolute, ...) la acepta como file:///invitations — la variante NonAbsolute
    // pasaba en Windows por la razon equivocada y fallaba en CI. Esta prueba no depende del
    // sistema operativo: ftp:// es una URI absoluta en cualquier plataforma, asi que expone
    // la falta de chequeo de esquema sin importar donde corra.
    [Fact]
    public void NonHttpSchemeInvitationUrlFails()
    {
        var options = new NotificationsOptions { InvitationUrl = "ftp://example.com/invitations" };

        var result = ValidatorFor(Environments.Development).Validate(null, options);

        Assert.True(result.Failed);
    }

    /// <summary>
    /// El fallback a `log` sigue siendo válido fuera de producción: es lo que deja levantar la
    /// API y correr las pruebas de integración sin credenciales de Infobip.
    /// </summary>
    [Fact]
    public void DevelopmentWithTheLogProviderIsValid()
    {
        var options = new NotificationsOptions
        {
            EmailProvider = NotificationsOptions.LogProvider,
        };

        var result = ValidatorFor(Environments.Development).Validate(null, options);

        Assert.True(result.Succeeded);
    }

    /// <summary>
    /// En producción el no-op no se distingue de un envío real: 200, invitación entregada,
    /// ningún log de error y el destinatario sin correo. El arranque tiene que frenarse.
    /// </summary>
    [Fact]
    public void ProductionWithTheLogProviderFails()
    {
        var options = new NotificationsOptions
        {
            EmailProvider = NotificationsOptions.LogProvider,
        };

        var result = ValidatorFor(Environments.Production).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Notifications:EmailProvider", result.FailureMessage);
    }

    [Fact]
    public void ProductionWithCompleteInfobipCredentialsIsValid()
    {
        var options = new NotificationsOptions
        {
            EmailProvider = NotificationsOptions.InfobipProvider,
            Infobip = CompleteInfobip,
        };

        var result = ValidatorFor(Environments.Production).Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/login")]
    [InlineData("ftp://qep.qcode.co")]
    public void AnInvalidAppBaseUrlFails(string value)
    {
        var result = ValidatorFor(Environments.Development)
            .Validate(null, new NotificationsOptions { AppBaseUrl = value });

        Assert.Contains("Notifications:AppBaseUrl must be an absolute URL.", result.Failures!);
    }

    // Spec 2026-11-22 D-11: the retention of the bell is 1 to 365 days.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(366)]
    public void RetentionDaysOutOfRangeIsInvalid(int days)
    {
        var result = ValidatorFor(Environments.Development)
            .Validate(null, new NotificationsOptions { InApp = new InAppOptions { RetentionDays = days } });

        Assert.Contains(result.Failures!, failure => failure.Contains("Notifications:InApp:RetentionDays", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(90)]
    [InlineData(365)]
    public void RetentionDaysInRangeIsValid(int days)
    {
        var result = ValidatorFor(Environments.Development)
            .Validate(null, new NotificationsOptions { InApp = new InAppOptions { RetentionDays = days } });

        Assert.True(result.Succeeded);
    }

    // Task-notices spec D-4: the wait before a task notice escalates is one global number of minutes, one minute to one day.
    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public void RejectsAnEscalationOutsideOneMinuteToOneDay(int minutes)
    {
        var result = ValidatorFor(Environments.Development)
            .Validate(null, new NotificationsOptions { TaskNotice = new TaskNoticeOptions { EscalationAfterMinutes = minutes } });

        Assert.Contains(
            result.Failures!, failure => failure.Contains("Notifications:TaskNotice:EscalationAfterMinutes", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(1440)]
    public void AcceptsAnEscalationInsideTheRange(int minutes)
    {
        var result = ValidatorFor(Environments.Development)
            .Validate(null, new NotificationsOptions { TaskNotice = new TaskNoticeOptions { EscalationAfterMinutes = minutes } });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void TheEscalationDefaultsToThirtyMinutes() =>
        Assert.Equal(30, new NotificationsOptions().TaskNotice.EscalationAfterMinutes);

    private static NotificationsOptionsValidator ValidatorFor(string environmentName) =>
        new(new StubHostEnvironment(environmentName));

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Modules.Notifications.UnitTests";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
