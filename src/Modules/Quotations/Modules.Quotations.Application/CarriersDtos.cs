using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

public sealed record CarrierDto(
    Guid Id,
    string Name,
    IReadOnlyList<string> ExternalNames,
    bool SupportsCashOnDelivery,
    int SortOrder,
    bool IsActive,
    long Version);

/// <summary><c>IsActive</c> viaja aunque sólo se pueda asignar una activa: la tasa se puede
/// desactivar en Catálogo después, y la pantalla tiene que poder avisarlo (cobrar un envío con ella
/// da <c>freight_tax_rate_missing</c>, spec §4.2).</summary>
public sealed record FreightTaxRateDto(Guid Id, string Name, int Percentage, bool IsActive);

/// <summary>BFF (spec §4.3): la tasa del envío y **todas** las transportadoras, activas e inactivas,
/// en orden. Una sola llamada pinta la pantalla entera, y reordenar o cambiar la tasa devuelve lo
/// mismo, que es lo que la pantalla repinta.</summary>
public sealed record CarrierSettingsDto(FreightTaxRateDto? FreightTaxRate, IReadOnlyList<CarrierDto> Carriers);

/// <summary>Lo que el cotizador y el pedido necesitan para dibujar la entrega (spec §4.3): sólo las
/// activas, en orden, y la tasa con la que se calcula el IVA del envío.</summary>
public sealed record ShippingOptionsDto(
    IReadOnlyList<ShippingCarrierOptionDto> Carriers, FreightTaxRateDto? FreightTaxRate);

public sealed record ShippingCarrierOptionDto(Guid Id, string Name, bool SupportsCashOnDelivery);

internal static class CarrierMapping
{
    public static CarrierDto ToDto(Carrier carrier) => new(
        carrier.Id.Value,
        carrier.Name,
        carrier.ExternalNames,
        carrier.SupportsCashOnDelivery,
        carrier.SortOrder,
        carrier.IsActive,
        carrier.Version);

    public static FreightTaxRateDto ToDto(QuotationTaxRateRef taxRate) =>
        new(taxRate.Id, taxRate.Name, taxRate.Percentage, taxRate.IsActive);

    /// <summary>La tasa guardada, leída de Catálogo. Si se borró allá después de asignarla, viaja
    /// <c>null</c>: para la pantalla es lo mismo que no tener tasa.</summary>
    public static async Task<FreightTaxRateDto?> FreightTaxRateAsync(
        IQuotationSettingsStore store,
        IQuotationTaxRateLookup taxRates,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var taxRateId = await store.GetFreightTaxRateIdAsync(tenantId, cancellationToken);
        if (taxRateId is not { } id)
        {
            return null;
        }

        var taxRate = await taxRates.FindAsync(tenantId, id, cancellationToken);
        return taxRate is null ? null : ToDto(taxRate);
    }

    public static async Task<CarrierSettingsDto> SettingsAsync(
        ICarrierRepository carriers,
        IQuotationSettingsStore store,
        IQuotationTaxRateLookup taxRates,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var list = await carriers.ListAsync(tenantId, cancellationToken);
        return new CarrierSettingsDto(
            await FreightTaxRateAsync(store, taxRates, tenantId, cancellationToken),
            [.. list.Select(ToDto)]);
    }
}
