using Modules.Notifications.Application;
using Modules.Tenancy.Application;

namespace Modules.Notifications.UnitTests;

/// <summary>El correo del Excel de saldos (spec WMS 2026-10-31, S15): calcado del de proveedores, con su propio asunto y su propia
/// plantilla, y en español colombiano tuteando.</summary>
public sealed class WmsExportEmailTemplateTests
{
    private static readonly DateTimeOffset ExpiresAt =
        new(2026, 9, 1, 14, 30, 0, TimeSpan.Zero);

    private static readonly TimeZoneInfo Bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");

    private static readonly EmailRegionalFormat Colombian = EmailRegionalFormat.Colombian(Bogota);

    // Una URL prefirmada real: media docena de parámetros separados por `&`, que es justo lo que rompe si el cuerpo HTML no los escapa.
    private const string SignedUrl =
        "https://r2.example/exports/f.xlsx?X-Amz-Algorithm=AWS4-HMAC-SHA256" +
        "&X-Amz-Credential=abc%2F20260901%2Fauto%2Fs3%2Faws4_request" +
        "&X-Amz-Date=20260901T113000Z&X-Amz-Expires=86400&X-Amz-Signature=deadbeef";

    [Fact]
    public void TheTemplateIsItsOwnAndNotTheSuppliersOne()
    {
        Assert.Equal("wms.export-ready.v1", WmsExportEmailTemplate.TemplateRef);
        Assert.NotEqual(SupplierExportEmailTemplate.TemplateRef, WmsExportEmailTemplate.TemplateRef);
    }

    [Fact]
    public void RenderPutsTheLinkAndTheExpiryInBothBodies()
    {
        var message = WmsExportEmailTemplate.Render(
            "bodega@verde.co", SignedUrl, "saldos-20260901-0930.xlsx", rowCount: 42, ExpiresAt, Colombian);

        Assert.Equal("bodega@verde.co", message.ToAddress);
        Assert.Equal("Tu exportación de saldos está lista", message.Subject);

        foreach (var body in new[] { message.HtmlBody, message.TextBody })
        {
            Assert.Contains("saldos-20260901-0930.xlsx", body, StringComparison.Ordinal);
            Assert.Contains("42 filas", body, StringComparison.Ordinal);
            // 14:30 UTC son las 09:30 en Bogotá: la hora del tenant, sin etiqueta de huso.
            Assert.Contains("01/09/2026 09:30", body, StringComparison.Ordinal);
            Assert.DoesNotContain("UTC", body, StringComparison.Ordinal);
            // Tuteo colombiano, nunca voseo.
            foreach (var voseo in new[] { "descargá", "tenés", "podés", "solicitá", "hacé" })
            {
                Assert.DoesNotContain(voseo, body, StringComparison.Ordinal);
            }
        }

        // En texto plano la URL va cruda: escaparla ahí la rompería.
        Assert.Contains(SignedUrl, message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderEscapesTheLinkInsideTheHtmlHref()
    {
        var message = WmsExportEmailTemplate.Render("bodega@verde.co", SignedUrl, "saldos.xlsx", 1, ExpiresAt, Colombian);

        Assert.Contains("&amp;X-Amz-Signature=", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("&X-Amz-Signature=", message.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderUsesTheSingularForOneRow()
    {
        var message = WmsExportEmailTemplate.Render("bodega@verde.co", "https://r2.example/x", "saldos.xlsx", 1, ExpiresAt, Colombian);

        Assert.Contains("(1 fila)", message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("1 filas", message.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderUsesTheTenantDateAndNumberFormat()
    {
        var format = new EmailRegionalFormat(Bogota, "MM/dd/yyyy", RegionalNumberFormat.ToNumberFormatInfo("1,234.56"));
        var message = WmsExportEmailTemplate.Render(
            "bodega@verde.co", "https://r2.example/x", "saldos.xlsx", 1234, new DateTimeOffset(2027, 1, 1, 4, 0, 0, TimeSpan.Zero), format);

        foreach (var body in new[] { message.HtmlBody, message.TextBody })
        {
            Assert.Contains("12/31/2026 23:00", body, StringComparison.Ordinal);
            Assert.Contains("1,234 filas", body, StringComparison.Ordinal);
        }
    }
}
