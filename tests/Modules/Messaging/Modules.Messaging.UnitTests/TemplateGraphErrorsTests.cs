using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-14 §4.8: de un <c>MessagingGraphFailure</c> al código de dominio, por <c>error_user_title</c>/<c>error_user_msg</c>
/// y nunca sólo por <c>code = 100</c>. Un token vencido o un permiso faltante (401, 403, 190, 10, 200–299) además marca la conexión.</summary>
public sealed class TemplateGraphErrorsTests
{
    [Theory]
    [InlineData(0, null, null, null, MessagingErrorCodes.TemplatesUnavailable)]
    [InlineData(502, null, null, null, MessagingErrorCodes.TemplatesUnavailable)]
    [InlineData(401, 190, null, null, MessagingErrorCodes.ConnectionUnavailable)]
    [InlineData(400, 190, "Invalid OAuth access token", null, MessagingErrorCodes.ConnectionUnavailable)]
    [InlineData(403, null, null, null, MessagingErrorCodes.ConnectionUnavailable)]
    [InlineData(400, 10, "Permission denied", null, MessagingErrorCodes.ConnectionUnavailable)]
    [InlineData(400, 200, "Permissions error", null, MessagingErrorCodes.ConnectionUnavailable)]
    [InlineData(400, 100, "Message Template Name Already Exists", "x", MessagingErrorCodes.TemplateNameTaken)]
    [InlineData(400, 100, "Invalid parameter", "A template with the same name and language already exists.", MessagingErrorCodes.TemplateNameTaken)]
    [InlineData(400, 100, "Template Limit Exceeded", "x", MessagingErrorCodes.TemplateLimitReached)]
    [InlineData(400, 100, "Invalid Format", "The body ends with a variable.", MessagingErrorCodes.TemplateRejectedByMeta)]
    [InlineData(400, 100, null, null, MessagingErrorCodes.TemplateRejectedByMeta)]
    public void EachGraphFailureHasItsCode(int status, int? code, string? title, string? message, string expected)
    {
        var exception = TemplateGraphErrors.Translate(new MessagingGraphFailure(status, code, $"http_{status}", title, message));
        Assert.Equal(expected, exception.Code);
    }

    /// <summary>Revisión final F3: Meta bloquea un tiempo el nombre (o el idioma) de una plantilla borrada. El texto exacto del error no está
    /// confirmado, así que la regla es explícita: <c>error_user_title</c> o <c>error_user_msg</c> que digan «being deleted», o «deleted»
    /// junto a «language» o «template», son <c>name_taken</c> y el aprovisionamiento salta a la versión siguiente.</summary>
    [Theory]
    [InlineData("Message template language is being deleted",
        "New es content can't be added while the existing es content is being deleted. Try again in 4 weeks or create a new message template.")]
    [InlineData("Invalid parameter", "A template with this name was deleted recently. Use a different name.")]
    [InlineData("Language Deleted", "The es language of qep_cotizacion was deleted and can't be used yet.")]
    [InlineData(null, "This template is being deleted.")]
    public void ANameOrLanguageInDeletionQuarantineIsNameTaken(string? title, string message)
    {
        var exception = TemplateGraphErrors.Translate(new MessagingGraphFailure(400, 100, "http_400", title, message));
        Assert.Equal(MessagingErrorCodes.TemplateNameTaken, exception.Code);
    }

    [Theory]
    [InlineData("Invalid Media", "The example file was deleted from the upload session.")]
    [InlineData("Invalid Format", "The body ends with a variable.")]
    public void DeletedWithoutTemplateOrLanguageIsStillARejection(string title, string message)
    {
        var exception = TemplateGraphErrors.Translate(new MessagingGraphFailure(400, 100, "http_400", title, message));
        Assert.Equal(MessagingErrorCodes.TemplateRejectedByMeta, exception.Code);
    }

    /// <summary>Producción, 2026-10-31: el subcódigo 2388293 (demasiadas variables para el texto) dice «limit» en el título y salía como
    /// tope de plantillas en una cuenta sin ninguna. Un «limit» del contenido es un rechazo, con el texto de Meta.</summary>
    [Theory]
    [InlineData("Parameters Words Ratio Exceeds Limit", "This template has too many variables for its length.")]
    [InlineData("Message Template Character Limit Exceeded", "The body is too long.")]
    public void ALimitOnTheContentIsARejectionWithMetasText(string title, string message)
    {
        var exception = TemplateGraphErrors.Translate(new MessagingGraphFailure(400, 100, "http_400", title, message));
        Assert.Equal(MessagingErrorCodes.TemplateRejectedByMeta, exception.Code);
        Assert.Contains(message, exception.Message, StringComparison.Ordinal);
    }

    // §4.8 «token vencido o permiso faltante»: los códigos de permiso de Graph son el 10 y el rango 200–299.
    [Theory]
    [InlineData(401, null, true)]
    [InlineData(403, null, true)]
    [InlineData(400, 190, true)]
    [InlineData(400, 10, true)]
    [InlineData(400, 200, true)]
    [InlineData(400, 250, true)]
    [InlineData(400, 299, true)]
    [InlineData(400, 100, false)]
    [InlineData(400, 199, false)]
    [InlineData(400, 300, false)]
    [InlineData(0, null, false)]
    [InlineData(502, null, false)]
    public void OnlyCredentialsAndPermissionsAreATokenRejection(int status, int? code, bool expected) =>
        Assert.Equal(expected, TemplateGraphErrors.IsTokenRejected(new MessagingGraphFailure(status, code, $"http_{status}")));

    [Fact]
    public void ARejectionCarriesMetasTextForThePerson()
    {
        var exception = TemplateGraphErrors.Translate(new MessagingGraphFailure(400, 100, "http_400", "Invalid Format", "The body ends with a variable."));
        Assert.Equal("WhatsApp no aceptó la plantilla: The body ends with a variable.", exception.Message);
    }

    [Fact]
    public void ARejectionWithoutTextsFallsBackToTheReason()
    {
        var exception = TemplateGraphErrors.Translate(new MessagingGraphFailure(400, 100, "http_400"));
        Assert.Equal("WhatsApp no aceptó la plantilla: http_400", exception.Message);
    }

    /// <summary>Revisión final F6: un borrado que Meta rechaza no es «WhatsApp no aceptó la plantilla». Mismo código
    /// (<c>rejected_by_meta</c>), su propio texto; y nada de <c>name_taken</c> ni <c>limit_reached</c>, que son de la creación.</summary>
    [Theory]
    [InlineData("Invalid parameter", "The template is locked by an active review.", "WhatsApp no pudo borrar la plantilla: The template is locked by an active review.")]
    [InlineData("Template is being deleted", null, "WhatsApp no pudo borrar la plantilla: Template is being deleted")]
    [InlineData("Template Limit Exceeded", "x", "WhatsApp no pudo borrar la plantilla: x")]
    [InlineData(null, null, "WhatsApp no pudo borrar la plantilla: http_400")]
    public void ADeletionRejectionHasItsOwnText(string? title, string? message, string expected)
    {
        var exception = TemplateGraphErrors.Translate(new MessagingGraphFailure(400, 100, "http_400", title, message), TemplateGraphOperation.Delete);
        Assert.Equal((MessagingErrorCodes.TemplateRejectedByMeta, expected), (exception.Code, exception.Message));
    }

    [Theory]
    [InlineData(0, null, MessagingErrorCodes.TemplatesUnavailable)]
    [InlineData(503, null, MessagingErrorCodes.TemplatesUnavailable)]
    [InlineData(401, 190, MessagingErrorCodes.ConnectionUnavailable)]
    [InlineData(400, 200, MessagingErrorCodes.ConnectionUnavailable)]
    public void ADeletionKeepsTheUnavailableAndCredentialCodes(int status, int? code, string expected) =>
        Assert.Equal(expected, TemplateGraphErrors.Translate(new MessagingGraphFailure(status, code, $"http_{status}"), TemplateGraphOperation.Delete).Code);

    [Fact]
    public async Task ADeletionFailureReportsTheTokenAndUsesTheDeletionText()
    {
        var connections = new FakeConnectionDirectory();

        var rejected = await TemplateGraphErrors.FailAsync(
            connections, Guid.CreateVersion7(), Guid.CreateVersion7(), new MessagingGraphFailure(400, 100, "http_400", "Invalid parameter", "Locked."),
            TemplateGraphOperation.Delete, TestContext.Current.CancellationToken);
        var token = await TemplateGraphErrors.FailAsync(
            connections, Guid.CreateVersion7(), Guid.CreateVersion7(), new MessagingGraphFailure(401, 190, "http_401"), TemplateGraphOperation.Delete,
            TestContext.Current.CancellationToken);

        Assert.Equal("WhatsApp no pudo borrar la plantilla: Locked.", rejected.Message);
        Assert.Equal(MessagingErrorCodes.ConnectionUnavailable, token.Code);
        Assert.Single(connections.Reported);
    }

    [Fact]
    public async Task ATokenRejectionReportsTheConnectionBeforeTranslating()
    {
        var connections = new FakeConnectionDirectory();
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();

        var exception = await TemplateGraphErrors.FailAsync(connections, tenantId, connectionId, new MessagingGraphFailure(401, 190, "http_401"), TestContext.Current.CancellationToken);

        Assert.Equal(MessagingErrorCodes.ConnectionUnavailable, exception.Code);
        var report = Assert.Single(connections.Reported);
        Assert.Equal((tenantId, connectionId, "token_expired"), (report.TenantId, report.ConnectionId, report.FailureCode));
    }

    [Fact]
    public async Task AMissingPermissionAlsoReportsTheConnection()
    {
        var connections = new FakeConnectionDirectory();

        var exception = await TemplateGraphErrors.FailAsync(
            connections, Guid.CreateVersion7(), Guid.CreateVersion7(), new MessagingGraphFailure(403, 200, "http_403"), TestContext.Current.CancellationToken);

        Assert.Equal(MessagingErrorCodes.ConnectionUnavailable, exception.Code);
        Assert.Equal(MetaErrorCodes.RejectedReason(MetaErrorCodes.TokenExpired), Assert.Single(connections.Reported).FailureCode);
    }

    [Fact]
    public async Task AContentRejectionDoesNotTouchTheConnection()
    {
        var connections = new FakeConnectionDirectory();

        await TemplateGraphErrors.FailAsync(connections, Guid.CreateVersion7(), Guid.CreateVersion7(), new MessagingGraphFailure(400, 100, "http_400", "Invalid Format", "x"), TestContext.Current.CancellationToken);

        Assert.Empty(connections.Reported);
    }
}
