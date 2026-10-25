using System.Globalization;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Cómo imprime el historial de cambios las fechas y los números del tenant (spec 2026-10-23 D-R6). Vale para
/// las filas nuevas: las ya guardadas quedan con el texto con que se escribieron. Sin fila del tenant, o con un
/// valor desconocido, cae en el formato colombiano.
/// </summary>
public sealed record HistoryFormat(string DateFormat, NumberFormatInfo Numbers)
{
    public static HistoryFormat Colombian { get; } = For(null);

    public static HistoryFormat For(TenantRegionalSettings? settings) => new(
        RegionalDateFormat.Pattern(settings?.DateFormat),
        RegionalNumberFormat.ToNumberFormatInfo(settings?.NumberFormat));

    /// <summary>Una lectura de Tenancy por handler, antes de escribir la primera fila.</summary>
    public static async Task<HistoryFormat> ReadAsync(
        ITenantDirectory directory, Guid tenantId, CancellationToken cancellationToken) =>
        For(await directory.GetRegionalSettingsAsync(new TenantId(tenantId), cancellationToken));

    public string Date(DateOnly value) => value.ToString(DateFormat, CultureInfo.InvariantCulture);

    // Sin ceros a la derecha: "x3" y no "x3,00". La cantidad es decimal porque hay productos que
    // se venden fraccionados, pero la mayoría son enteros y el historial se lee mejor así. Con los
    // separadores del tenant (D-R6): "x1.500" en 1.234,56 y "x1,500" en 1,234.56.
    public string Number(decimal value) => value.ToString("#,##0.##", Numbers);
}
