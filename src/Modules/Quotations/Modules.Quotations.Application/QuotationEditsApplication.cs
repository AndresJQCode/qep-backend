using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Lo que quedó cambiado, para que el guardado sepa qué historial y qué auditoría
/// escribir. Las dos fotos del encabezado viajan en vez del resumen ya armado: el texto lleva el
/// formato del tenant (spec 2026-10-23 D-R6), y sólo el guardado lo lee —el cálculo previo, que
/// comparte este paso, no escribe historial y no gana una lectura de Tenancy.</summary>
internal sealed record QuotationEditsOutcome(
    QuotationHeaderSnapshot HeaderBefore,
    QuotationHeaderSnapshot HeaderAfter,
    IReadOnlyList<QuotationItemEdit> ItemEdits);

/// <summary>
/// Los pasos que el guardado de una vez y su cálculo previo comparten, en el orden en que tienen
/// que correr. Está acá y no copiado en los dos handlers porque el contrato del preview es
/// literalmente «lo que quedaría si guardaras»: dos copias del orden se desincronizan y la
/// pantalla muestra un total que el guardado no deja.
///
/// Lo que **no** está acá es lo que sólo hace el guardado: historial, auditoría y
/// <c>SaveChangesAsync</c>.
/// </summary>
internal static class QuotationEditsApplication
{
    public static async Task<QuotationEditsOutcome> ApplyAsync(
        Quotation quotation,
        IQuotationEdits edits,
        Guid tenantId,
        IQuotationCompanyLookup companyLookup,
        IQuotationProductPricingLookup pricingLookup,
        IQuotationSettingsStore settingsStore,
        ITenantPriceRounding priceRounding,
        ShippingResolver shippingResolver,
        QuotationWithholdingContext withholdings,
        MemberId updatedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var billingAccount = await QuotationBillingAccountResolver.ResolveAsync(
            companyLookup, tenantId, edits.BillingAccount, cancellationToken);

        // Cambiar a una cuenta en otra moneda cambia la moneda de la cotización entera, y con ella
        // el precio de cada línea: lo guardado es el precio del producto en la moneda vieja, no una
        // cifra convertible. Se piden todos los precios juntos y **antes** de tocar el agregado
        // (UpdateQuotation.cs:69-79), así una línea sin precio en la moneda nueva corta el guardado
        // completo en vez de dejar la cotización con dos monedas adentro.
        var currency = quotation.CurrencyFor(billingAccount);
        var changesCurrency = currency != quotation.Currency;
        var repricing = !changesCurrency
            ? null
            : await QuotationProductPricingResolver.ResolveManyAsync(
                pricingLookup,
                tenantId,
                quotation.Items
                    .Select(item => (item.VariantId, item.Quantity))
                    .ToArray(),
                currency, edits.IsRetail,
                cancellationToken);

        // La foto de antes, para poder decir en el historial **qué** se editó: el cuerpo manda el
        // encabezado entero en cada guardado, así que sin comparar no hay forma de saberlo.
        var before = QuotationHeaderSnapshot.Of(quotation);
        // El encabezado va antes que las líneas y no al revés: es el que fija la moneda, y las
        // líneas nuevas del diff se valorizan contra la que quede.
        quotation.UpdateDetails(
            edits.ValidUntil,
            edits.PaymentMethod,
            edits.Notes,
            edits.Parties.ToDomain(),
            billingAccount,
            repricing,
            edits.IsRetail,
            edits.GlobalScaleFloor,
            updatedBy,
            now);
        // Los selectores después del encabezado: se validan contra el país de la cuenta que acaba de quedar (spec
        // impuestos por tipo §7.5), y antes de la foto de después, para que el historial cuente el cambio.
        await ApplyBillingWithholdingsAsync(quotation, edits.Parties, tenantId, withholdings, updatedBy, now, cancellationToken);
        // El envío después del encabezado: valida contra la recogida y la moneda que acaban de quedar, y
        // antes de la foto de después, para que el historial cuente el cambio de transportadora o de valor.
        //
        // D-F15 (R15): si el guardado cambia la moneda, el formulario igual reenvía el `shipping` que tenía en
        // pantalla, con el valor en la moneda vieja. Aplicarlo deshacería la limpieza de ApplyBillingAccount
        // (15.000 COP pasarían a ser 15.000 USD). Se descartan el modo y el valor del cuerpo; la transportadora
        // y la contraentrega no dependen de la moneda y sí se aplican.
        var shippingRequest = changesCurrency && edits.Shipping is { } requested
            ? requested with { FreightChargeMode = null, FreightAmount = 0m }
            : edits.Shipping;
        var shipping = await shippingResolver.ResolveAsync(
            tenantId, shippingRequest, quotation.ShippingDetails, quotation.IsStorePickup, quotation.Currency,
            "quotation.quotation", cancellationToken);
        if (shipping is not null)
        {
            quotation.SetShipping(shipping, updatedBy, now);
        }

        var after = QuotationHeaderSnapshot.Of(quotation);

        var itemEdits = await QuotationItemEdits.ApplyAsync(
            quotation, edits.Items, pricingLookup, tenantId, updatedBy, now, edits.IsRetail, cancellationToken);

        // Una sola pasada al final: el descuento por escala depende de la cotización entera, así
        // que resolverlo en medio del diff lo calcularía contra un estado a medio aplicar. Y el
        // mínimo de compra se mide sobre el total resultante.
        await QuotationPricingRecalculation.ApplyAsync(
            pricingLookup, settingsStore, priceRounding, tenantId, quotation, now, cancellationToken);

        return new QuotationEditsOutcome(before, after, itemEdits);
    }

    // Con facturación propia y la lista presente, la lista reemplaza los selectores; null los conserva (el frontend
    // viejo no la manda y no puede borrarlos). Con los datos del cliente o a consumidor final no hay selectores que
    // editar y la lista se ignora: los guardados se quedan para cuando se vuelva a la facturación propia (spec §7.5).
    private static async Task ApplyBillingWithholdingsAsync(
        Quotation quotation,
        QuotationPartiesRequest? parties,
        Guid tenantId,
        QuotationWithholdingContext withholdings,
        MemberId updatedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (quotation.Billing is null || parties?.BillingWithholdingTaxRateIds is not { } ids)
        {
            return;
        }

        // La fecha sólo hace falta para resolver ids: vaciar no consulta ni el catálogo ni el calendario.
        IReadOnlyList<WithholdingRate> rates = ids.Count == 0
            ? []
            : await WithholdingSelection.ResolveAsync(
                withholdings.Lookup,
                tenantId,
                ids,
                quotation.BillingAccount?.Country,
                await withholdings.DocumentDate(cancellationToken),
                "quotation",
                cancellationToken);
        quotation.SetBillingWithholdings(rates, updatedBy, now);
    }
}

/// <summary>
/// Con qué resuelve <see cref="QuotationEditsApplication"/> los selectores de retención: el puerto al catálogo y la
/// fecha del documento para la UVT. La fecha es perezosa porque leerla cuesta el calendario del tenant, y sólo hace
/// falta cuando llegan ids; al crear ya se conoce (el hoy del tenant).
/// </summary>
internal sealed record QuotationWithholdingContext(
    IQuotationWithholdingLookup Lookup,
    Func<CancellationToken, Task<DateOnly>> DocumentDate)
{
    /// <summary>La de una cotización ya guardada: su <c>CreatedAt</c> en la zona del tenant.</summary>
    public static QuotationWithholdingContext For(
        IQuotationWithholdingLookup lookup, ITenantClock tenantClock, Guid tenantId, Quotation quotation) =>
        new(lookup, async cancellationToken => DocumentDates.LocalDate(
            await tenantClock.GetAsync(tenantId, cancellationToken), quotation.CreatedAt));
}
