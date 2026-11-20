using System.Globalization;
using Modules.Tenancy.Application;

namespace Modules.Purchases.Application;

/// <summary>
/// Del detalle de la orden al documento del PDF. Los montos salen con el símbolo y los decimales de la moneda
/// (<see cref="Currencies"/>) y los separadores del tenant; la fecha, con su patrón. Sin formato regional rigen los de Colombia,
/// como en el resto de los textos que arma el servidor. La fecha es la de la orden, no la del día en que se descarga (R11): la del
/// envío si ya salió al proveedor, la del alta si nunca se envió, llevada al día local del tenant.
/// </summary>
public static class PurchaseOrderPdfMapper
{
    public static PurchaseOrderPdfDocument Map(
        PurchaseOrderDetailDto detail,
        PurchasesSupplierRef supplier,
        string tenantName,
        TenantRegionalSettings? regional,
        TimeZoneInfo tenantTimeZone)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(supplier);
        ArgumentNullException.ThrowIfNull(tenantTimeZone);

        var numbers = RegionalNumberFormat.ToNumberFormatInfo(regional?.NumberFormat);
        var currency = Currencies.Get(detail.Currency);
        // Sin monto (orden vinculada que nació sin precio y todavía no se aprueba, spec 2026-11-19) se imprime «—», igual que
        // una cantidad ausente: el monto no existe en la base, así que no hay nada que enmascarar.
        string Money(decimal? amount) => amount is { } value
            ? $"{currency.Symbol} {value.ToString($"N{currency.Decimals}", numbers)}"
            : "—";

        var lines = detail.Lines
            .Select(line =>
            {
                // D-5: lo confirmado manda donde existe; el subtotal del detalle ya lo usa.
                var quantity = line.ConfirmedQuantity ?? line.Quantity;
                var unitCost = line.ConfirmedUnitCost ?? line.UnitCost;
                return new PurchaseOrderPdfLine(
                    line.Code,
                    line.Name,
                    // Una línea Added no tiene cantidad pedida: sin la confirmada se imprime «—».
                    quantity?.ToString("#,0.##", numbers) ?? "—",
                    Money(unitCost),
                    $"{line.TaxRatePercent.ToString("0.##", numbers)} %",
                    Money(line.Subtotal));
            })
            .ToList();

        return new PurchaseOrderPdfDocument(
            tenantName,
            detail.Number,
            StatusLabel(detail.Status),
            IssuedOn(detail, tenantTimeZone).ToString(RegionalDateFormat.Pattern(regional?.DateFormat), CultureInfo.InvariantCulture),
            supplier.Name,
            $"{supplier.IdentificationType} {supplier.IdentificationNumber}".Trim(),
            detail.Currency,
            detail.Notes,
            lines,
            Money(detail.Totals?.Subtotal),
            Money(detail.Totals?.Tax),
            Money(detail.Totals?.Total),
            detail.Lines.Any(line => line.ConfirmedQuantity is not null || line.ConfirmedUnitCost is not null),
            detail.ShowPrices);
    }

    // El envío se busca en el historial y no por el estado: una orden cancelada después de enviarse conserva la fecha del envío.
    private static DateOnly IssuedOn(PurchaseOrderDetailDto detail, TimeZoneInfo tenantTimeZone)
    {
        var sent = detail.History.FirstOrDefault(entry => entry.Status == "Sent");
        var instant = sent?.At ?? detail.CreatedAt;
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, tenantTimeZone).DateTime);
    }

    // Un estado nuevo sin etiqueta se imprime con su nombre antes que tumbar el PDF.
    private static string StatusLabel(string status) => status switch
    {
        "Draft" => "Borrador",
        "Sent" => "Enviada",
        "Confirmed" => "Confirmada",
        "Received" => "Recibida",
        "Cancelled" => "Cancelada",
        _ => status,
    };
}
