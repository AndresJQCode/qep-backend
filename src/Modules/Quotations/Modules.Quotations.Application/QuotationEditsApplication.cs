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
        var repricing = currency == quotation.Currency
            ? null
            : await QuotationProductPricingResolver.ResolveManyAsync(
                pricingLookup,
                tenantId,
                quotation.Items
                    .Select(item => (item.ProductId, item.Quantity))
                    .ToArray(),
                currency,
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
            updatedBy,
            now);
        var headerSummary = QuotationChangeSummary.HeaderChanged(
            before, QuotationHeaderSnapshot.Of(quotation));

        var itemEdits = await QuotationItemEdits.ApplyAsync(
            quotation, edits.Items, pricingLookup, tenantId, updatedBy, now, cancellationToken);

        // Una sola pasada al final: el descuento por escala depende de la cotización entera, así
        // que resolverlo en medio del diff lo calcularía contra un estado a medio aplicar. Y el
        // mínimo de compra se mide sobre el total resultante.
        await QuotationPricingRecalculation.ApplyAsync(
            pricingLookup, tenantId, quotation, now, cancellationToken);

        return new QuotationEditsOutcome(headerSummary, itemEdits);
    }
}
