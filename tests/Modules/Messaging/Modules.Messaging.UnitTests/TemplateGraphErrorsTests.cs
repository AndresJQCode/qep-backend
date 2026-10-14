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
