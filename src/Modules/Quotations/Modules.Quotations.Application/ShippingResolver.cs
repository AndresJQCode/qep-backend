using FluentValidation;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Resuelve contra el tenant lo que el dominio no puede saber del envío (spec 2026-10-17 §5.2, P3): que la
/// transportadora exista, esté activa y haga recaudo, que el modo de cobro esté activo en el tenant (§4.4), y
/// cuál es la tasa del IVA del envío. La coherencia
/// (recogida, montos, <c>Free</c>, <c>Charged</c>) la valida el agregado en <c>SetShipping</c>.
///
/// Lo comparten la cotización y el pedido; <c>errorPrefix</c> es <c>quotation.quotation</c> u
/// <c>order.order</c>. Los handlers lo llaman <b>después</b> de aplicar partes y cuenta de cobro, así que la
/// recogida y la moneda que recibe ya son las nuevas.
/// </summary>
public sealed class ShippingResolver(
    ICarrierRepository carriers,
    IQuotationTaxRateLookup taxRates,
    IQuotationSettingsStore settingsStore)
{
    /// <summary>
    /// El envío que hay que fijar, o <c>null</c> para no tocarlo. Reglas, en orden:
    /// recogida limpia todo; sin cuerpo no se toca (P4); la transportadora se lee sólo si cambió; la
    /// contraentrega se comprueba sólo si la transportadora es nueva o se prende ahora; el modo se compara con
    /// los activos del tenant sólo si cambió; y la tasa se lee sólo si el envío cobrado en COP cambió de modo o
    /// de valor.
    /// </summary>
    public async Task<ShippingDetails?> ResolveAsync(
        Guid tenantId,
        ShippingRequest? request,
        ShippingDetails current,
        bool isStorePickup,
        string currency,
        string errorPrefix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);

        // 1. Con recogida no hay envío, venga o no en el cuerpo: el dominio rechazaría cualquier otro.
        if (isStorePickup)
        {
            return current == ShippingDetails.None ? null : ShippingDetails.None;
        }

        // 2. Un frontend que todavía no manda el envío no lo borra.
        if (request is null)
        {
            return null;
        }

        // 3. La transportadora: si no cambió se conserva la foto del nombre sin ir al repositorio, aunque se
        // haya desactivado después (un documento no pierde lo que ya eligió).
        var carrierId = request.CarrierId;
        string? carrierName = null;
        Carrier? chosen = null;
        if (carrierId is { } id)
        {
            if (id == current.CarrierId)
            {
                carrierName = current.CarrierName;
            }
            else
            {
                chosen = await carriers.FindAsync(tenantId, new CarrierId(id), cancellationToken);
                if (chosen is null || !chosen.IsActive)
                {
                    throw new QuotationsDomainException(
                        $"{errorPrefix}.carrier_not_available",
                        "The carrier does not exist or is no longer active.");
                }

                carrierName = chosen.Name;
            }
        }

        // 4. La contraentrega con una transportadora que no recauda es una contradicción que se puede
        // detectar. Sólo al elegirla o al prender la contraentrega: si la transportadora deja de recaudar
        // después, el documento conserva lo elegido.
        if (request.CashOnDelivery && carrierId is { } collectingId)
        {
            var turnedOnNow = !current.CashOnDelivery;
            if (chosen is not null || turnedOnNow)
            {
                var carrier = chosen
                    ?? await carriers.FindAsync(tenantId, new CarrierId(collectingId), cancellationToken)
                    // La guardada ya no se encuentra: no se puede afirmar que recaude.
                    ?? throw new QuotationsDomainException(
                        $"{errorPrefix}.carrier_not_available",
                        "The carrier does not exist or is no longer active.");
                if (!carrier.SupportsCashOnDelivery)
                {
                    throw new QuotationsDomainException(
                        $"{errorPrefix}.cash_on_delivery_not_supported",
                        "The carrier does not collect cash on delivery.");
                }
            }
        }

        // 5. El modo: uno que el tenant apagó no se puede elegir (spec §4.4). Como la transportadora, sólo al
        // elegirlo o cambiarlo: el documento que ya lo tenía lo conserva, y quitarlo (null) siempre se puede.
        var mode = QuotationMapping.ToFreightChargeMode(request.FreightChargeMode, errorPrefix);
        if (mode is { } chosenMode && chosenMode != current.ChargeMode)
        {
            var enabled = await settingsStore.GetEnabledFreightChargeModesAsync(tenantId, cancellationToken);
            if (!enabled.Contains(chosenMode))
            {
                throw new QuotationsDomainException(
                    $"{errorPrefix}.freight_charge_mode_disabled",
                    $"The freight charge mode '{chosenMode}' is disabled for this tenant.");
            }
        }

        // 6. El valor y la tasa.
        var amount = mode == FreightChargeMode.Free ? 0m : request.FreightAmount;
        var taxPercentage = 0;
        // Con valor 0 el dominio responde freight_amount_required, que es la causa real: leer la tasa antes
        // podría tapar ese error con uno de configuración.
        if (mode == FreightChargeMode.Charged && amount > 0 && ItemizesTax(currency))
        {
            taxPercentage = mode == current.ChargeMode && amount == current.FreightAmount
                // Un guardado que no tocó el envío conserva la foto de la tasa: cambiar la del tenant no
                // re-precia documentos ya cotizados.
                ? current.FreightTaxPercentage
                : await TenantFreightTaxPercentageAsync(tenantId, errorPrefix, cancellationToken);
        }

        return new ShippingDetails(carrierId, carrierName, mode, amount, taxPercentage, request.CashOnDelivery);
    }

    private async Task<int> TenantFreightTaxPercentageAsync(
        Guid tenantId, string errorPrefix, CancellationToken cancellationToken)
    {
        var rateId = await settingsStore.GetFreightTaxRateIdAsync(tenantId, cancellationToken);
        var rate = rateId is { } value
            ? await taxRates.FindAsync(tenantId, value, cancellationToken)
            : null;
        if (rate is null || !rate.IsActive)
        {
            throw new QuotationsDomainException(
                $"{errorPrefix}.freight_tax_rate_missing",
                "The tenant has no active freight tax rate configured.");
        }

        return rate.Percentage;
    }

    // Mismo criterio que FreightAmounts.For: sólo en COP el valor del envío lleva el IVA adentro.
    private static bool ItemizesTax(string currency) =>
        string.Equals(currency, "COP", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Las reglas de forma de <see cref="ShippingRequest"/>, compartidas por crear, guardar y previsualizar. El
/// dominio valida la coherencia con su código; esto existe para que el 422 traiga el campo en <c>errors</c>
/// (<c>Shipping.FreightAmount</c>, <c>Shipping.FreightChargeMode</c>), lo único con lo que el formulario
/// marca el input. Interno por el mismo motivo que <see cref="QuotationPartiesRequestValidator"/>.
/// </summary>
internal sealed class ShippingRequestValidator : AbstractValidator<ShippingRequest>
{
    private static readonly string[] ModeNames = Enum.GetNames<FreightChargeMode>();

    public ShippingRequestValidator()
    {
        RuleFor(shipping => shipping.FreightChargeMode)
            .Must(mode => mode is null || ModeNames.Contains(mode, StringComparer.Ordinal))
            .WithMessage($"The freight charge mode must be one of: {string.Join(", ", ModeNames)}.");

        // El dominio exige pesos enteros en COP pero no tiene tope de decimales fuera de COP: dos, como
        // la columna numeric(14,2), para que lo guardado sea lo que se escribió.
        RuleFor(shipping => shipping.FreightAmount)
            .GreaterThanOrEqualTo(0m)
            .Must(amount => decimal.Round(amount, 2) == amount)
            .WithMessage("The freight amount can have at most two decimals.");
    }
}
