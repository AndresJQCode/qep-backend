using System.Globalization;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Los valores que las plantillas de WhatsApp llevan, iguales para Zenvia y para Meta: el mensaje y el PDF no pueden
/// mostrar el monto ni la vigencia de dos maneras según el proveedor (spec 2026-10-21 §4.3).
/// </summary>
public static class WhatsAppTemplateValues
{
    // Sólo la vigencia: el locale de la plantilla (`es`) fija su texto largo, y no depende del formato
    // numérico del tenant (spec 2026-10-23 D-R1). El monto usa los separadores del tenant.
    private static readonly CultureInfo Colombia = CultureInfo.GetCultureInfo("es-CO");

    /// <summary>El símbolo y los decimales de la moneda de la cotización, con los separadores del tenant
    /// (colombianos si no tiene): <c>"$ 2.450.000"</c> o <c>"€ 2,450.50"</c>.</summary>
    public static string Total(decimal total, string currency, string? numberFormat)
    {
        var info = Currencies.Get(currency);
        return $"{info.Symbol} {total.ToString($"N{info.Decimals}", RegionalNumberFormat.ToNumberFormatInfo(numberFormat))}";
    }

    /// <summary>La vigencia en español largo: <c>"30 de septiembre de 2026"</c>.</summary>
    public static string ValidUntil(DateOnly validUntil) =>
        validUntil.ToString("d 'de' MMMM 'de' yyyy", Colombia);

    /// <summary>El nombre con el que el cliente recibe el PDF: <c>"Cotizacion-COT-000123.pdf"</c>.</summary>
    public static string DocumentFileName(string quotationNumber) => $"Cotizacion-{quotationNumber}.pdf";
}
