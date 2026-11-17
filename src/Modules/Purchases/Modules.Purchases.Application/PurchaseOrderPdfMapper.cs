using System.Globalization;
using Modules.Tenancy.Application;

namespace Modules.Purchases.Application;

/// <summary>
/// Del detalle de la orden al documento del PDF. Los montos salen con el símbolo y los decimales de la moneda
/// (<see cref="Currencies"/>) y los separadores del tenant; la fecha, con su patrón. Sin formato regional rigen los de Colombia,
/// como en el resto de los textos que arma el servidor.
/// </summary>
public static class PurchaseOrderPdfMapper
{
    public static PurchaseOrderPdfDocument Map(
        PurchaseOrderDetailDto detail,
        PurchasesSupplierRef supplier,
        string tenantName,
        TenantRegionalSettings? regional,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(supplier);

        var numbers = RegionalNumberFormat.ToNumberFormatInfo(regional?.NumberFormat);
        var currency = Currencies.Get(detail.Currency);
        string Money(decimal amount) =>
            $"{currency.Symbol} {amount.ToString($"N{currency.Decimals}", numbers)}";

        var lines = detail.Lines
            .Select(line =>
            {
                // D-5: lo confirmado manda donde existe; el subtotal del detalle ya lo usa.
                var quantity = line.ConfirmedQuantity ?? line.Quantity;
                var unitCost = line.ConfirmedUnitCost ?? line.UnitCost;
                return new PurchaseOrderPdfLine(
                    line.Code,
                    line.Name,
                    quantity.ToString("#,0.##", numbers),
                    Money(unitCost),
                    $"{line.TaxRatePercent.ToString("0.##", numbers)} %",
                    Money(line.Subtotal));
            })
            .ToList();

        return new PurchaseOrderPdfDocument(
            tenantName,
            detail.Number,
            StatusLabel(detail.Status),
            today.ToString(RegionalDateFormat.Pattern(regional?.DateFormat), CultureInfo.InvariantCulture),
            supplier.Name,
            $"{supplier.IdentificationType} {supplier.IdentificationNumber}".Trim(),
            detail.Currency,
            detail.Notes,
            lines,
            Money(detail.Totals.Subtotal),
            Money(detail.Totals.Tax),
            Money(detail.Totals.Total),
            detail.Lines.Any(line => line.ConfirmedQuantity is not null || line.ConfirmedUnitCost is not null));
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
