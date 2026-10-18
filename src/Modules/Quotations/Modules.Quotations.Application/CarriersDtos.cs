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

/// <summary>Una opción de «¿Quién paga el envío?» y si el tenant la tiene activa (spec §4.4). <c>Mode</c> viaja
/// con el nombre del enum (<c>Charged</c>, <c>OnDelivery</c>, <c>Free</c>): el texto lo pone el frontend.</summary>
public sealed record FreightChargeModeDto(string Mode, bool IsEnabled);

/// <summary>BFF (spec §4.3): la tasa del envío y **todas** las transportadoras, activas e inactivas,
/// en orden. Una sola llamada pinta la pantalla entera, y reordenar o cambiar la tasa devuelve lo
/// mismo, que es lo que la pantalla repinta.
///
/// <c>FreightChargeModes</c> trae **siempre las tres**, en el orden <c>Charged</c>, <c>OnDelivery</c>,
/// <c>Free</c>, aunque estén apagadas (§4.4): es una colección de tamaño fijo, y si la apagada faltara la
/// pantalla tendría que conocer el enum del backend para dibujar su interruptor. No la "simplifiques" a la
/// lista de las activas.
///
/// <c>DefaultFreightChargeMode</c> (§4.4.1) es <c>null</c> o el nombre de una de las activas: la que el
/// formulario elige sola cuando el documento envía y todavía no tiene quién paga.</summary>
public sealed record CarrierSettingsDto(
    FreightTaxRateDto? FreightTaxRate,
    IReadOnlyList<CarrierDto> Carriers,
    IReadOnlyList<FreightChargeModeDto> FreightChargeModes,
    string? DefaultFreightChargeMode);

/// <summary>Lo que el cotizador y el pedido necesitan para dibujar la entrega (spec §4.3): sólo las
/// activas, en orden, y la tasa con la que se calcula el IVA del envío.
///
/// <c>FreightChargeModes</c> trae las tres, apagadas incluidas, por la misma razón que en
/// <see cref="CarrierSettingsDto"/>: el formulario muestra las activas más la que el documento ya tiene
/// guardada aunque esté apagada (marcada como desactivada), y para eso tiene que saber que existe y que está
/// apagada, no inferirlo de su ausencia.
///
/// <c>DefaultFreightChargeMode</c> viaja acá porque es el formulario el que lo aplica (D-F19): el backend no
/// rellena el modo de ningún documento.</summary>
public sealed record ShippingOptionsDto(
    IReadOnlyList<ShippingCarrierOptionDto> Carriers,
    FreightTaxRateDto? FreightTaxRate,
    IReadOnlyList<FreightChargeModeDto> FreightChargeModes,
    string? DefaultFreightChargeMode);

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
        var modes = await store.GetFreightChargeModesAsync(tenantId, cancellationToken);
        return new CarrierSettingsDto(
            await FreightTaxRateAsync(store, taxRates, tenantId, cancellationToken),
            [.. list.Select(ToDto)],
            ToDto(modes),
            modes.Default?.ToString());
    }

    /// <summary>Las tres, en el orden del enum (<c>Charged</c>, <c>OnDelivery</c>, <c>Free</c>), cada una con
    /// si está activa.</summary>
    public static IReadOnlyList<FreightChargeModeDto> ToDto(FreightChargeModeSettings modes) =>
        [.. Enum.GetValues<FreightChargeMode>().Select(mode => new FreightChargeModeDto(mode.ToString(), modes.Enabled.Contains(mode)))];
}
