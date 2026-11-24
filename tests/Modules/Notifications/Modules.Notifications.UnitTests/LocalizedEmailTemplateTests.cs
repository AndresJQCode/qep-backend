using Modules.Notifications.Application;

namespace Modules.Notifications.UnitTests;

/// <summary>
/// Spec 2026-11-22 (i18n): los correos ya están en español neutro y tuteando, que vale en los dos
/// idiomas; sólo cambian los términos que en España se dicen distinto (owner, 2026-11-22):
/// «cotización» es «presupuesto» y «radicado» es «número de caso».
/// </summary>
public sealed class LocalizedEmailTemplateTests
{
    private static readonly EmailRegionalFormat Colombian =
        EmailRegionalFormat.Colombian(TimeZoneInfo.FindSystemTimeZoneById("America/Bogota"));

    private static readonly DateTimeOffset ExpiresAt = new(2026, 11, 23, 15, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("es-419", "cotización", "cotizaciones")]
    [InlineData("es-ES", "presupuesto", "presupuestos")]
    public void TheQuotationKindIsNamedInTheLanguage(string language, string singular, string plural)
    {
        Assert.Equal(new QuotationsExportKindNames(singular, plural), QuotationsExportKindText.Of("Quotations", language));
    }

    [Theory]
    [InlineData("es-419")]
    [InlineData("es-ES")]
    public void OrdersAndUnknownKindsAreTheSameInBothLanguages(string language)
    {
        Assert.Equal(new QuotationsExportKindNames("pedido", "pedidos"), QuotationsExportKindText.Of("Orders", language));
        Assert.Equal(new QuotationsExportKindNames("registro", "registros"), QuotationsExportKindText.Of("Sales", language));
    }

    [Fact]
    public void TheReadyEmailInSpainSpeaksOfBudgets()
    {
        var message = QuotationsExportReadyEmailTemplate.Render(
            "ana@example.com", "Quotations", "https://r2.example/x.xlsx", "presupuestos.xlsx", 3, ExpiresAt, Colombian,
            "es-ES");

        Assert.Equal("Tu exportación de presupuestos está lista", message.Subject);
        Assert.Contains("3 presupuestos", message.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("cotizaci", message.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFailedEmailInSpainSpeaksOfBudgets()
    {
        var message = QuotationsExportFailedEmailTemplate.Render("ana@example.com", "Quotations", "es-ES");

        Assert.Equal("No pudimos generar tu exportación de presupuestos", message.Subject);
    }

    // Quien no pasa idioma (un llamador anterior) recibe el de siempre: nadie ve un cambio.
    [Fact]
    public void WithoutALanguageTheEmailIsTheLatinAmericanOne()
    {
        var message = QuotationsExportFailedEmailTemplate.Render("ana@example.com", "Quotations");

        Assert.Equal("No pudimos generar tu exportación de cotizaciones", message.Subject);
    }

    [Fact]
    public void TheSupportOpeningInSpainSaysCaseNumber()
    {
        var message = SupportOpeningEmailTemplate.Render(Opening("es-ES"));

        foreach (var body in new[] { message.HtmlBody, message.TextBody })
        {
            Assert.Contains("Número de caso:", body, StringComparison.Ordinal);
            Assert.Contains("Con el número de caso y esta clave", body, StringComparison.Ordinal);
            Assert.DoesNotContain("adicado", body.Replace("radicado=SOP", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheSupportOpeningInLatinAmericaKeepsTheRadicado()
    {
        var message = SupportOpeningEmailTemplate.Render(Opening("es-419"));

        Assert.Contains("Radicado: SOP-000127", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("Con el radicado y esta clave", message.TextBody, StringComparison.Ordinal);
    }

    // El parámetro de la URL de consulta es contrato con el formulario público: no se traduce.
    [Fact]
    public void TheConsultLinkIsNotTranslated()
    {
        var message = SupportOpeningEmailTemplate.Render(Opening("es-ES"));

        Assert.Contains("consultar?radicado=SOP-000127", message.TextBody, StringComparison.Ordinal);
    }

    private static SupportOpeningEmailInput Opening(string language) => new(
        "ana@example.com",
        "Verde Cia",
        "SOP-000127",
        "Petición",
        new DateOnly(2026, 11, 27),
        "https://soporte.lulocrm.com/verde/consultar?radicado=SOP-000127",
        "048213",
        language);
}
