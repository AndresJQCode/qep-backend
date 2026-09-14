using Modules.Notifications.Application;

namespace Modules.Notifications.UnitTests;

/// <summary>
/// Los dos correos de la exportación asíncrona de cotizaciones y ventas (spec 2026-09-12, D12).
/// El de "lista" sigue al de clientes —enlace escapado en el HTML, crudo en texto plano— y los dos
/// nombran el tipo según el kind del evento.
/// </summary>
public sealed class QuotationsExportEmailTemplateTests
{
    private static readonly DateTimeOffset ExpiresAt = new(2026, 9, 13, 15, 30, 0, TimeSpan.Zero);

    private const string SignedUrl =
        "https://r2.example/exports/tenants/a/jobs/b.xlsx?X-Amz-Algorithm=AWS4-HMAC-SHA256" +
        "&X-Amz-Date=20260912T153000Z&X-Amz-Expires=86400&X-Amz-Signature=deadbeef";

    [Fact]
    public void ReadyNamesTheKindAndPutsTheLinkAndTheExpiryInBothBodies()
    {
        var message = QuotationsExportReadyEmailTemplate.Render(
            "ana@qcode.co", "Orders", SignedUrl, "pedidos-2026-09-12-1530.xlsx", 42, ExpiresAt);

        Assert.Equal("ana@qcode.co", message.ToAddress);
        Assert.Equal("Tu exportación de pedidos está lista", message.Subject);
        foreach (var body in new[] { message.HtmlBody, message.TextBody })
        {
            Assert.Contains("pedidos-2026-09-12-1530.xlsx", body, StringComparison.Ordinal);
            Assert.Contains("42 pedidos", body, StringComparison.Ordinal);
            Assert.Contains("13/09/2026 15:30 UTC", body, StringComparison.Ordinal);
        }

        Assert.Contains(SignedUrl, message.TextBody, StringComparison.Ordinal);
    }

    // Sin alias (spec 2026-09-14, D5): un evento emitido antes del deploy con el kind viejo cae al
    // nombre genérico, igual que cualquier kind desconocido.
    [Fact]
    public void TheOldSalesKindFallsBackToTheGenericName()
    {
        var message = QuotationsExportFailedEmailTemplate.Render("ana@qcode.co", "Sales");

        Assert.Equal("No pudimos generar tu exportación de registros", message.Subject);
    }

    // Mismo motivo que en CustomerExportEmailTemplateTests: un `&` sin declarar en el href rompe
    // la firma de R2 según cómo normalice el HTML cada cliente de correo.
    [Fact]
    public void ReadyEscapesTheLinkInsideTheHtmlHref()
    {
        var message = QuotationsExportReadyEmailTemplate.Render(
            "ana@qcode.co", "Quotations", SignedUrl, "cotizaciones.xlsx", 3, ExpiresAt);

        Assert.Contains("&amp;X-Amz-Signature=", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("&X-Amz-Signature=", message.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadyUsesTheSingularForOneRow()
    {
        var message = QuotationsExportReadyEmailTemplate.Render(
            "ana@qcode.co", "Quotations", "https://r2.example/x", "cotizaciones.xlsx", 1, ExpiresAt);

        Assert.Equal("Tu exportación de cotizaciones está lista", message.Subject);
        Assert.Contains("(1 cotización)", message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedTellsWhichExportFailedAndToTryAgain()
    {
        var message = QuotationsExportFailedEmailTemplate.Render("ana@qcode.co", "Quotations");

        Assert.Equal("ana@qcode.co", message.ToAddress);
        Assert.Equal("No pudimos generar tu exportación de cotizaciones", message.Subject);
        foreach (var body in new[] { message.HtmlBody, message.TextBody })
        {
            Assert.Contains(
                "No pudimos generar tu exportación de cotizaciones. Intenta de nuevo.",
                body,
                StringComparison.Ordinal);
        }
    }

    // Un kind que llegue antes que su texto no deja a nadie sin correo: cae a un nombre genérico.
    [Fact]
    public void AnUnknownKindStillProducesAReadableEmail()
    {
        var message = QuotationsExportFailedEmailTemplate.Render("ana@qcode.co", "Invoices");

        Assert.Equal("No pudimos generar tu exportación de registros", message.Subject);
    }
}
