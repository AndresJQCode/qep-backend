using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Lo que quedó cambiado, para que el guardado sepa qué historial y qué auditoría
/// escribir. <paramref name="HeaderSummary"/> es null cuando el encabezado llegó igual a como
/// estaba — mismo criterio que <c>SaveQuotationHandler</c>: apretar Guardar sin cambiar nada no
/// es un evento del historial.</summary>
internal sealed record QuotationEditsOutcome(
    string? HeaderSummary,
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
        IQuotationCustomerLookup customerLookup,
        IQuotationCompanyLookup companyLookup,
        IQuotationProductPricingLookup pricingLookup,
        IQuotationSettingsStore settingsStore,
        ShippingResolver shippingResolver,
        MemberId updatedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Deja la retención/excedente de IVA al día con el cliente maestro antes de recalcular
        // — ver Quotation.RefreshCustomerTaxProfile. Una sola vez para todo el guardado.
        var customer = await customerLookup.FindAsync(tenantId, quotation.ClientId, cancellationToken);
        if (customer is not null)
        {
            quotation.RefreshCustomerTaxProfile(customer.WithRetention, customer.VatSurplus);
        }

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
                    .Select(item => (item.ProductId, item.Quantity))
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

        var headerSummary = QuotationChangeSummary.HeaderChanged(
            before, QuotationHeaderSnapshot.Of(quotation));

        var itemEdits = await QuotationItemEdits.ApplyAsync(
            quotation, edits.Items, pricingLookup, tenantId, updatedBy, now, edits.IsRetail, cancellationToken);

        // Una sola pasada al final: el descuento por escala depende de la cotización entera, así
        // que resolverlo en medio del diff lo calcularía contra un estado a medio aplicar. Y el
        // mínimo de compra se mide sobre el total resultante.
        await QuotationPricingRecalculation.ApplyAsync(
            pricingLookup, settingsStore, tenantId, quotation, now, cancellationToken);

        return new QuotationEditsOutcome(headerSummary, itemEdits);
    }
}
