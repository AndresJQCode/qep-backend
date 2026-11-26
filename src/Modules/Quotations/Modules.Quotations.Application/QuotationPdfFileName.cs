using BuildingBlocks.Domain.Localization;

namespace Modules.Quotations.Application;

/// <summary>
/// El nombre del archivo del PDF: dice qué documento es, igual que el membrete. Una sola regla para lo que se sube a
/// <c>qcode-pdf</c> y para la descarga (spec 2026-11-22, i18n: en España la cotización es «Presupuesto»). Sin tildes, como
/// antes: el nombre viaja en cabeceras y en adjuntos de correo.
/// </summary>
public static class QuotationPdfFileName
{
    public static string For(QuotationPdfKind kind, string number, string? language) =>
        kind == QuotationPdfKind.Order
            ? $"Pedido-{number}.pdf"
            : SupportedLanguages.OrDefault(language) == SupportedLanguages.SpainSpanish
                ? $"Presupuesto-{number}.pdf"
                : $"Cotizacion-{number}.pdf";
}
