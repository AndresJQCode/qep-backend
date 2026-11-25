using BuildingBlocks.Domain.Taxes;
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
    IReadOnlyList<QuotationItemEdit> ItemEdits)
{
    /// <summary>La re-resolución municipal del cuerpo (spec ReteICA por empresa §7.6): el municipio de antes y el de
    /// después, y los porcentajes editados que se perdieron.</summary>
    public MunicipalReResolution Municipal { get; init; } = MunicipalReResolution.None;

    /// <summary>Las filas con porcentaje editado que quitó «Sin retención» (plan P12), para anotarlas como la
    /// re-resolución.</summary>
    public IReadOnlyList<WithholdingRate> ClearedOverrides { get; init; } = [];

    /// <summary>Los porcentajes que cambió el cuerpo (§7.7), en el orden en que llegaron.</summary>
    public IReadOnlyList<WithholdingPercentageChange> PercentageChanges { get; init; } = [];

    /// <summary>Las tasas municipales que el asesor aplicó a mano con facturación propia (R11): el guardado las nombra
    /// en el resumen del <c>Edited</c> con el municipio.</summary>
    public IReadOnlyList<WithholdingRate> ManualMunicipalRates { get; init; } = [];
}

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
        CancellationToken cancellationToken,
        MunicipalContext? municipal = null)
    {
        // Una empresa sin cuenta (spec 2026-11-17, R3) sale en la moneda de la propia cotización: elegir sólo la empresa
        // nunca reprecia las líneas.
        var billingAccount = await QuotationBillingAccountResolver.ResolveAsync(
            companyLookup, tenantId, edits.BillingAccount, edits.BillingCompanyId, quotation.Currency, cancellationToken);

        // «Sin retención» se valida antes de tocar el agregado y siempre, también con cambio de empresa o de municipio
        // (plan P12): una marca contradictoria es 422, nunca un id ignorado en silencio como los de A11.
        string[] cleared = [.. (edits.ClearedWithholdingTypes ?? []).Distinct(StringComparer.Ordinal)];
        if (cleared.Length > 0)
        {
            var selectorTypes = await WithholdingSelection.MunicipalTypesOfAsync(
                withholdings.Lookup, tenantId, edits.Parties?.BillingWithholdingTaxRateIds,
                await withholdings.DocumentDate(cancellationToken), cancellationToken);
            MunicipalSelectionMarker.Validate(
                cleared, billingAccount?.Country, selectorTypes, edits.WithholdingPercentages, "quotation");
        }

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

        // Spec ReteICA por empresa §7.7: cuenta → municipio → re-resolución → selectores → «Sin retención» → porcentajes.
        // El municipio resultante se calcula antes de consultar (plan P4); al crear, el armado ya trae el contexto con el
        // que nació la cotización (P13), y la misma empresa con el mismo municipio no dispara la re-resolución.
        municipal ??= await MunicipalContexts.ResolveAsync(
            withholdings.Lookup,
            withholdings.Geography,
            tenantId,
            quotation.ClientId,
            billingAccount,
            quotation.OperationCityFor(billingAccount, edits.OperationCity),
            explicitCity: edits.OperationCity.Kind == OperationCityChoiceKind.Explicit,
            await withholdings.DateForAsync(billingAccount, cancellationToken),
            "quotation",
            cancellationToken);

        // Spec 2026-11-17 (R2): pasar de una empresa sin IVA a una con IVA vuelve a gravar el envío cobrado, y su tasa
        // es la del tenant. Se lee antes de tocar el agregado, para que una tasa faltante sea 422 sin nada a medias.
        int? freightTaxPercentageWithVat = quotation.NeedsFreightTaxRateFor(billingAccount)
            ? await shippingResolver.FreightTaxPercentageAsync(tenantId, "quotation.quotation", cancellationToken)
            : null;

        // The region of a foreign party must exist and belong to its country; checked before the aggregate changes, so a bad
        // region is a 422 with nothing half-written.
        var parties = edits.Parties.ToDomain();
        await PartyRegionRules.EnsureAsync(withholdings.Geography, parties, cancellationToken);

        // El encabezado va antes que las líneas y no al revés: es el que fija la moneda, y las
        // líneas nuevas del diff se valorizan contra la que quede.
        var reResolution = quotation.UpdateDetails(
            edits.ValidUntil,
            edits.PaymentMethod,
            edits.Notes,
            parties,
            billingAccount,
            repricing,
            edits.IsRetail,
            edits.GlobalScaleFloor,
            municipal,
            updatedBy,
            now,
            freightTaxPercentageWithVat);
        // Los selectores después del encabezado: se validan contra el país de la cuenta que acaba de quedar (spec
        // impuestos por tipo §7.5), y antes de la foto de después, para que el historial cuente el cambio.
        var selection = await ApplyBillingWithholdingsAsync(
            quotation, edits.Parties, tenantId, withholdings, municipal, reResolution.Triggered, cleared, updatedBy, now,
            cancellationToken);
        IReadOnlyList<WithholdingRate> clearedOverrides = cleared.Length == 0
            ? []
            : [.. selection.DroppedClearedOverrides, .. quotation.ClearMunicipalWithholdings(cleared, updatedBy, now)];
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
            quotation.ChargesVat, "quotation.quotation", cancellationToken);
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

        var percentageChanges = ApplyPercentages(quotation, edits, reResolution.Triggered, updatedBy, now);

        // Spec retenciones por UVT §5.4: al final, sobre las filas que dejaron los selectores y la re-resolución, con la
        // fecha del documento. Recalcula por su cuenta y no deja historial: el guardado y la vista previa ven lo mismo.
        await TaxUnitRefresh.RefreshAsync(quotation, withholdings.Lookup, withholdings.DocumentDate, cancellationToken);

        return new QuotationEditsOutcome(before, after, itemEdits)
        {
            Municipal = reResolution,
            ClearedOverrides = clearedOverrides,
            PercentageChanges = percentageChanges,
            ManualMunicipalRates = selection.ManualMunicipalRates,
        };
    }

    // Spec ReteICA por empresa §7.7: al final, sobre la fuente vigente. Con cambio de empresa o de municipio en el mismo
    // cuerpo (A11), un porcentaje municipal sin fila de su tipo se ignora en silencio: la SPA borra los porcentajes
    // municipales del borrador al cambiar, así que el que llega se tecleó después. Sin cambio, la regla es la de siempre.
    private static List<WithholdingPercentageChange> ApplyPercentages(
        Quotation quotation, IQuotationEdits edits, bool contextChanged, MemberId updatedBy, DateTimeOffset now)
    {
        var changes = new List<WithholdingPercentageChange>();
        foreach (var entry in edits.WithholdingPercentages ?? [])
        {
            if (contextChanged
                && TaxCountries.IsMunicipal(quotation.BillingAccount?.Country, entry.Type)
                && !quotation.ActiveWithholdings.Any(row => string.Equals(row.Type, entry.Type, StringComparison.Ordinal)))
            {
                continue;
            }

            if (quotation.SetWithholdingPercentage(entry.Type, entry.Percentage, updatedBy, now) is { } change)
            {
                changes.Add(change);
            }
        }

        return changes;
    }

    /// <summary>Lo que dejaron los selectores de la facturación propia, para el historial.</summary>
    private sealed record BillingSelection(
        IReadOnlyList<WithholdingRate> ManualMunicipalRates,
        IReadOnlyList<WithholdingRate> DroppedClearedOverrides)
    {
        public static BillingSelection None { get; } = new([], []);
    }

    // Con facturación propia y la lista presente, la lista reemplaza los selectores; null los conserva (el frontend
    // viejo no la manda y no puede borrarlos). Con los datos del cliente o a consumidor final no hay selectores que
    // editar y la lista se ignora: los guardados se quedan para cuando se vuelva a la facturación propia (spec §7.5).
    //
    // Un id municipal sólo vale si es la tasa de la empresa en el municipio de la operación (A4). Si el cuerpo cambió la
    // empresa o el municipio (A11, plan P6), el id que no coincide se ignora y queda la fila de la re-resolución.
    private static async Task<BillingSelection> ApplyBillingWithholdingsAsync(
        Quotation quotation,
        QuotationPartiesRequest? parties,
        Guid tenantId,
        QuotationWithholdingContext withholdings,
        MunicipalContext municipal,
        bool contextChanged,
        IReadOnlyCollection<string> cleared,
        MemberId updatedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (quotation.Billing is null || parties?.BillingWithholdingTaxRateIds is not { } ids)
        {
            return BillingSelection.None;
        }

        // Los mismos de antes: la foto se queda como se congeló, sin volver al catálogo (Review Focus 1: un porcentaje
        // municipal editado sobrevive al reenvío del mismo selector).
        var current = quotation.BillingWithholdings.Select(withholding => withholding.Rate).ToList();
        if (WithholdingSelection.IsUnchanged(ids, current, quotation.BillingAccount?.Country))
        {
            return BillingSelection.None;
        }

        // A18 (plan P14): sin cambio de contexto, reenviar el id municipal guardado conserva su fila tal cual, aunque la
        // empresa haya reemplazado su tasa o cambie otro selector del mismo cuerpo.
        var scope = MunicipalSelectorScope.For(municipal, ignoreUnmatched: contextChanged) with
        {
            ReResolved = contextChanged ? [.. current.Where(IsMunicipal)] : [],
            Stored = contextChanged ? [] : current,
        };

        // La fecha sólo hace falta para resolver ids: vaciar no consulta ni el catálogo ni el calendario.
        IReadOnlyList<WithholdingRate> rates = ids.Count == 0 && scope.ReResolved.Count == 0
            ? []
            : await WithholdingSelection.ResolveAsync(
                withholdings.Lookup,
                tenantId,
                ids,
                quotation.BillingAccount?.Country,
                await withholdings.DocumentDate(cancellationToken),
                scope,
                "quotation",
                cancellationToken,
                cleared.ToHashSet(StringComparer.Ordinal));
        quotation.SetBillingWithholdings(rates, updatedBy, now);

        var after = quotation.BillingWithholdings.Select(withholding => withholding.Rate).ToList();
        var currentIds = current.Select(rate => rate.TaxRateId).ToHashSet();
        var afterIds = after.Select(rate => rate.TaxRateId).ToHashSet();
        return new BillingSelection(
            // R11: con facturación propia toda municipal que entra por el selector es una aplicación a mano.
            [.. after.Where(rate => IsMunicipal(rate) && !currentIds.Contains(rate.TaxRateId))],
            // «Sin retención» con el selector ya sin el id: la fila editada la quitó el selector y no la marca, pero la
            // elección del asesor es la misma y el porcentaje perdido se anota igual (spec §7.6).
            [.. current.Where(rate =>
                rate.Overridden && IsMunicipal(rate) && cleared.Contains(rate.Type) && !afterIds.Contains(rate.TaxRateId))]);
    }

    private static bool IsMunicipal(WithholdingRate rate) => TaxCountries.IsMunicipal(rate.Country, rate.Type);
}

/// <summary>
/// Con qué resuelve <see cref="QuotationEditsApplication"/> los selectores de retención y el contexto municipal: el puerto
/// al catálogo, Geography para comprobar un municipio explícito y la fecha del documento para la UVT. La fecha es perezosa
/// porque leerla cuesta el calendario del tenant, y sólo hace falta cuando llegan ids o hay municipio que resolver; al
/// crear ya se conoce (el hoy del tenant).
/// </summary>
internal sealed record QuotationWithholdingContext(
    IQuotationWithholdingLookup Lookup,
    IQuotationGeographyLookup Geography,
    Func<CancellationToken, Task<DateOnly>> DocumentDate)
{
    /// <summary>La de una cotización ya guardada: su <c>CreatedAt</c> en la zona del tenant.</summary>
    public static QuotationWithholdingContext For(
        IQuotationWithholdingLookup lookup,
        IQuotationGeographyLookup geography,
        ITenantClock tenantClock,
        Guid tenantId,
        Quotation quotation) =>
        new(lookup, geography, async cancellationToken => DocumentDates.LocalDate(
            await tenantClock.GetAsync(tenantId, cancellationToken), quotation.CreatedAt));

    /// <summary>La fecha, sólo si la empresa es de un país con tipos municipales: si no, no hay nada que consultar y el
    /// calendario no se lee.</summary>
    public async Task<DateOnly> DateForAsync(QuotationBillingAccount? billingAccount, CancellationToken cancellationToken) =>
        TaxCountries.MunicipalTypes(billingAccount?.Country).Count == 0
            ? default
            : await DocumentDate(cancellationToken);
}
