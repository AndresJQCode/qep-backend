using Modules.Notifications.Application;

namespace Modules.Notifications.UnitTests;

public sealed class InvitationEmailTemplateTests
{
    private const string InvitationUrl = "https://app.example/invitaciones/aceptar?token=abc123";

    // El nombre del tenant es lo que le dice al destinatario a QUÉ organización lo invitaron: una
    // persona invitada a dos tenants recibe dos correos idénticos si el nombre no viaja en el asunto.
    [Fact]
    public void RenderPutsTheTenantNameInTheSubjectAndInBothBodies()
    {
        var message = InvitationEmailTemplate.Render(
            "ana@verde.co", InvitationUrl, "Origen Botánico");

        Assert.Equal("ana@verde.co", message.ToAddress);
        Assert.Contains("Origen Botánico", message.Subject, StringComparison.Ordinal);
        Assert.Contains("Origen Botánico", message.TextBody, StringComparison.Ordinal);

        // En el cuerpo HTML el nombre va escapado, y WebUtility.HtmlEncode también convierte los
        // acentos a entidades numéricas. Mismo criterio que CustomerExportEmailTemplate.
        Assert.Contains("Origen Bot&#225;nico", message.HtmlBody, StringComparison.Ordinal);

        Assert.Contains(InvitationUrl, message.TextBody, StringComparison.Ordinal);
    }

    // El nombre lo escribe el tenant, así que entra al HTML como texto de usuario: sin escapar, un
    // `&` o un `<` en el nombre rompe el cuerpo del correo.
    [Fact]
    public void RenderEscapesTheTenantNameInsideTheHtmlBody()
    {
        var message = InvitationEmailTemplate.Render(
            "ana@verde.co", InvitationUrl, "Flores & Co <Bogotá>");

        Assert.Contains("Flores &amp; Co &lt;Bogot&#225;&gt;", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<Bogotá>", message.HtmlBody, StringComparison.Ordinal);

        // En texto plano no hay entidades que declarar: el nombre va crudo.
        Assert.Contains("Flores & Co <Bogotá>", message.TextBody, StringComparison.Ordinal);
    }
}
