using BuildingBlocks.Domain.Taxes;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Un comprobante nuevo del borrador (spec 2026-09-17). <paramref name="FileId"/> es null
/// en el cálculo previo: los archivos se suben recién al guardar. <paramref name="PaidOn"/> es la
/// fecha del soporte (a pedido, 2026-10-08), ver <see cref="OrderPaymentProofRequest.PaidOn"/>.</summary>
public sealed record OrderEditProofAddition(Guid? FileId, decimal Amount, DateOnly? PaidOn = null);

/// <summary>Los comprobantes del borrador. <see cref="Update"/> sólo lleva los que cambian; los no
/// mencionados se conservan.</summary>
public sealed record OrderEditProofs(
    IReadOnlyList<OrderEditProofAddition> Add,
    IReadOnlyList<OrderPaymentProofUpdateRequest> Update,
    IReadOnlyList<Guid> RemoveIds)
{
    public static readonly OrderEditProofs None = new([], [], []);
}

/// <summary>El estado deseado completo de «Editar pedido» (spec 2026-09-17, decisión 2): mismo
/// cuerpo para el guardado y el cálculo previo, así que las reglas se escriben una vez.</summary>
public interface IOrderEdits
{
    IReadOnlyList<OrderItemAddition> Items { get; }

    OrderEditProofs Proofs { get; }

    string? Notes { get; }

    /// <summary>
    /// El piso de escala global de la cotización del pedido, o <c>null</c> para quitarlo. Viaja
    /// con el resto de la edición y se persiste al guardar: cambiarlo en la pantalla recalcula
    /// contra la vista previa, igual que cambiar una cantidad.
    /// </summary>
    int? GlobalScaleFloor { get; }

    /// <summary>
    /// Cotización detal de la cotización del pedido. Mismo contrato que
    /// <c>IQuotationEdits.IsRetail</c>: viaja con la edición, se aplica antes que el piso y se
    /// persiste al guardar sólo si cambió.
    /// </summary>
    bool IsRetail { get; }

    /// <summary>
    /// El envío del pedido pendiente (spec 2026-10-17 §5.4). <b>Null es "no lo toques"</b>, como en la
    /// cotización: un frontend viejo no lo borra. Recoger o enviar lo cambia <see cref="Delivery"/>, que se aplica antes.
    /// </summary>
    ShippingRequest? Shipping { get; }

    /// <summary>Recoger o enviar y a dónde (spec 2026-11-24 §10). <b>Null es «no la toques»</b>, como
    /// <see cref="Shipping"/>.</summary>
    OrderDeliveryEditRequest? Delivery { get; }

    /// <summary>
    /// A quién se le factura (spec 2026-10-26). <b>Null es «no lo toques»</b>, como <see cref="Shipping"/>:
    /// un frontend viejo no borra la facturación del pedido.
    /// </summary>
    OrderBillingEditRequest? Billing { get; }

    /// <summary>
    /// Los selectores de retención del pedido (spec impuestos por tipo D21). <b>Null es «no los toques»</b>; vacía los
    /// quita. Se resuelven contra el país de la empresa que queda después de <see cref="BillingAccount"/>.
    /// </summary>
    IReadOnlyList<Guid>? WithholdingTaxRateIds { get; }

    /// <summary>La empresa y la cuenta de cobro (D22). <b>Null es «no la toques»</b>.</summary>
    QuotationBillingAccountRequest? BillingAccount { get; }

    /// <summary>
    /// The billing company chosen before its bank account (spec 2026-11-16 withholdings by company). Only the preview
    /// carries it: absent <see cref="BillingAccount"/> then means «this company, account pending» instead of «keep the
    /// saved account». Ignored when <see cref="BillingAccount"/> is present. Null by default: the save never accepts it.
    /// </summary>
    Guid? BillingCompanyId => null;

    /// <summary>El municipio de la operación (spec ReteICA por empresa §9.4, plan P1): ausente, <c>null</c> (el de la
    /// empresa) o un id.</summary>
    OperationCityChoice OperationCity { get; }

    /// <summary>Los porcentajes de las retenciones municipales (§7.7); null conserva todos.</summary>
    IReadOnlyList<WithholdingPercentageRequest>? WithholdingPercentages { get; }

    /// <summary>Los tipos municipales con «Sin retención» (owner 2026-11-13); null o vacío = sin marca.</summary>
    IReadOnlyList<string>? ClearedWithholdingTypes { get; }

    /// <summary>La fecha del pedido (spec 2026-11-16 §4). <b>Null es «no la toques»</b>, como <see cref="Shipping"/>.</summary>
    DateOnly? OrderDate { get; }

    /// <summary>Los regalos del pedido (spec 2026-11-23). <b>Null es «no los toques»</b>, como <see cref="Shipping"/>: un
    /// frontend viejo no los borra. Presente —también vacía— es la lista entera deseada, por variante.</summary>
    IReadOnlyList<OrderItemAddition>? Gifts { get; }
}

/// <summary>Aplica el bloque <c>billing</c> de la edición al pedido. Lo comparten el guardado y el cálculo
/// previo para que la retención y el excedente de IVA de la vista previa sean los que el guardado dejaría.</summary>
internal static class OrderBillingEdits
{
    /// <summary>Devuelve <c>null</c> si no hay bloque o nada cambió; si cambió, el texto del historial.
    /// <paramref name="strict"/> aplica la regla NIT y las exigencias de fila completa, que sólo el guardado
    /// exige: el cálculo previo se hace mientras la persona teclea.</summary>
    public static string? Apply(
        Order order,
        OrderBillingEditRequest? billing,
        bool strict,
        MemberId updatedBy,
        DateTimeOffset now)
    {
        if (billing is null)
        {
            return null;
        }

        var party = billing.Party.ToDomain();
        if (strict)
        {
            BillingPartyFiscalRules.EnsureConsistent(party);
        }

        var before = OrderChangeSummary.BillingLabel(order);
        var changed = order.SetBilling(
            party, billing.BillsToFinalConsumer, billing.BillingUsesBusinessName,
            billing.BillingVatSurplus, updatedBy, now,
            enforceCompleteness: strict);
        return changed ? OrderChangeSummary.BillingChanged(before, OrderChangeSummary.BillingLabel(order)) : null;
    }
}

/// <summary>La entrega de un pedido en un momento dado: lo que el historial compara antes y después.</summary>
internal sealed record OrderDeliveryState(bool IsStorePickup, QuotationPartyDetails? Shipping)
{
    public static OrderDeliveryState Of(Order order) => new(order.IsStorePickup, order.Shipping?.ToDetails());
}

/// <summary>Aplica el bloque <c>delivery</c> de la edición al pedido (spec 2026-11-24 §10). Lo comparten el guardado y el
/// cálculo previo, y los dos lo hacen antes de la facturación, las retenciones, el envío y el estado de pago: el envío se
/// resuelve contra la recogida que queda.</summary>
internal static class OrderDeliveryEdits
{
    /// <summary>Devuelve <c>false</c> si no hay bloque o nada cambió. <paramref name="strict"/> exige la parte de envío
    /// completa, como el guardado de la facturación; el cálculo previo no. Las reglas de Shopify (§11.1.4) valen en los dos.
    /// </summary>
    public static async Task<bool> ApplyAsync(
        Order order,
        OrderDeliveryEditRequest? delivery,
        bool strict,
        IQuotationCustomerLookup customerLookup,
        IQuotationGeographyLookup geographyLookup,
        IExternalOrderDeliveryCheck externalCheck,
        Guid tenantId,
        MemberId updatedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (delivery is null)
        {
            return false;
        }

        var shipping = delivery.Shipping.ToDomain();
        // Con recogida la parte la rechaza el agregado, con su propio código: su región no se consulta.
        if (!delivery.IsStorePickup)
        {
            await PartyRegionRules.EnsureAsync(geographyLookup, shipping, "Delivery.Shipping", cancellationToken);
            // De recoger a enviar con los datos del cliente: sin domicilio no hay a dónde mandar.
            if (shipping is null && order.IsStorePickup)
            {
                var customer = await customerLookup.FindAsync(tenantId, order.ClientId, cancellationToken);
                if (string.IsNullOrWhiteSpace(customer?.Address))
                {
                    throw new QuotationsDomainException(
                        OrderDeliveryErrors.AddressRequired, "The customer has no address to ship the order to.");
                }
            }
        }

        var previousParty = order.Shipping?.ToDetails();
        var changed = order.SetDelivery(delivery.IsStorePickup, shipping, updatedBy, now, enforceCompleteness: strict);
        // Después del agregado, que ya validó y normalizó la parte: lo que se compara y se pregunta es lo que quedaría guardado.
        // Quien lanza acá no ha escrito nada. De recoger a enviar con los datos del cliente la parte propia no cambia (no la
        // hay), pero la entrega sí: también se pregunta, o Shopify se quedaría con la dirección anterior. Repetir esa entrega
        // sin cambiarla no cuenta.
        if (OwnShippingPartyChanged(order, previousParty)
            || (changed && !order.IsStorePickup && order.Shipping is null))
        {
            await EnsureShopifyCanReceiveAsync(order, externalCheck, tenantId, cancellationToken);
        }

        return changed;
    }

    /// <summary>Si, enviando, la parte de envío propia dejó de ser <paramref name="previous"/>: se agregó, cambió o se quitó
    /// para volver a los datos del cliente. Pasar a recoger no cuenta: no hay dirección que mandar.</summary>
    public static bool OwnShippingPartyChanged(Order order, QuotationPartyDetails? previous) =>
        !order.IsStorePickup && order.Shipping?.ToDetails() != previous;

    // Spec 2026-11-24 §11.1.4: sólo en un pedido de Shopify, para que el envío a la tienda nunca falle por un dato que falta.
    private static async Task EnsureShopifyCanReceiveAsync(
        Order order,
        IExternalOrderDeliveryCheck externalCheck,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (order.Source != OrderSource.Shopify)
        {
            return;
        }

        // Con los datos del cliente no hay parte propia que mandar: no hace falta preguntar.
        var code = order.Shipping?.ToDetails() is { } party
            ? await externalCheck.CheckAsync(
                tenantId, order.Id.Value, party.Country ?? QuotationPartyDetails.ColombiaCountryCode, party.RegionId,
                cancellationToken)
            : OrderDeliveryErrors.ShopifyPartyRequired;
        if (code is not null)
        {
            throw new QuotationsDomainException(code, "Shopify cannot receive this delivery address.");
        }
    }

    /// <summary>El texto del historial: la ciudad es la de DIVIPOLA en Colombia y la escrita afuera.</summary>
    public static async Task<string> DescribeChangeAsync(
        IQuotationGeographyLookup geographyLookup,
        OrderDeliveryState before,
        OrderDeliveryState after,
        CancellationToken cancellationToken)
    {
        var cityIds = new[] { before.Shipping, after.Shipping }
            .Where(party => party is { IsColombian: true, CityId: not null })
            .Select(party => party!.CityId!.Value)
            .Distinct()
            .ToArray();
        IReadOnlyDictionary<Guid, string> cityNames = cityIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await geographyLookup.FindCityNamesAsync(cityIds, cancellationToken);
        return OrderChangeSummary.DeliveryChanged(Text(before, cityNames), Text(after, cityNames));
    }

    private static OrderDeliveryText Text(OrderDeliveryState state, IReadOnlyDictionary<Guid, string> cityNames)
    {
        if (state.IsStorePickup)
        {
            return OrderDeliveryText.StorePickup;
        }

        if (state.Shipping is not { } party)
        {
            return OrderDeliveryText.CustomerAddress;
        }

        var city = !party.IsColombian
            ? party.CityName
            : party.CityId is { } cityId ? cityNames.GetValueOrDefault(cityId) : null;
        return OrderDeliveryText.To(party.Address, city);
    }
}

/// <summary>Lo que cambió la empresa, el municipio y las retenciones de la edición, para el historial. El guardado lo
/// redacta (<see cref="OrderMunicipalHistory"/>); el cálculo previo lo descarta.</summary>
/// <param name="BillingAccountSummary">El texto del cambio de cuenta, o null si no cambió.</param>
internal sealed record OrderWithholdingEditsOutcome(string? BillingAccountSummary)
{
    /// <summary>Las etiquetas de las tasas antes y después de los selectores y de «Sin retención»; null si no cambiaron.</summary>
    public (string Before, string After)? WithholdingsLabels { get; init; }

    /// <summary>La tasa municipal elegida a mano para un municipio donde el cliente no es agente (R11), para el sufijo del
    /// resumen; null si no hubo.</summary>
    public WithholdingRate? NotAgentManualRate { get; init; }

    /// <summary>La re-resolución por cambio de empresa o de municipio (spec ReteICA por empresa §7.6).</summary>
    public MunicipalReResolution Municipal { get; init; } = MunicipalReResolution.None;

    /// <summary>Las filas con porcentaje editado que quitó «Sin retención» (plan P12).</summary>
    public IReadOnlyList<WithholdingRate> ClearedOverrides { get; init; } = [];

    /// <summary>Los porcentajes que cambió el cuerpo (§7.7).</summary>
    public IReadOnlyList<WithholdingPercentageChange> PercentageChanges { get; init; } = [];

    /// <summary>Se completó en silencio el municipio de la empresa de una cuenta anterior (A12): no es un cambio para el
    /// historial, pero el guardado tiene que persistirlo.</summary>
    public bool BillingCompanyCityFilled { get; init; }

    /// <summary>Si algo de esto cambió el pedido.</summary>
    public bool Changed =>
        BillingAccountSummary is not null
        || WithholdingsLabels is not null
        || Municipal.Triggered
        || ClearedOverrides.Count > 0
        || PercentageChanges.Count > 0
        || BillingCompanyCityFilled;
}

/// <summary>Con qué resuelve <see cref="OrderWithholdingEdits"/> la cuenta, el municipio y los selectores: la empresa, el
/// catálogo, Geography (un municipio explícito) y el calendario del tenant (la fecha del pedido para la UVT).</summary>
internal sealed record OrderWithholdingEditsContext(
    IQuotationCompanyLookup CompanyLookup,
    IQuotationWithholdingLookup WithholdingLookup,
    IQuotationGeographyLookup GeographyLookup,
    ITenantClock TenantClock,
    Guid TenantId,
    ShippingResolver Shipping);

/// <summary>
/// Aplica la empresa (D22), el municipio de la operación, los selectores de retención (D21), «Sin retención» y los
/// porcentajes de la edición a un pedido editable (spec impuestos por tipo §7.5, §7.7; spec ReteICA por empresa §7.6, §7.7).
/// Lo comparten el guardado y el cálculo previo, para que las retenciones de la vista previa sean las que el guardado
/// dejaría. Orden: cuenta → municipio → re-resolución → selectores → «Sin retención» → porcentajes. Los selectores van
/// contra el país de la empresa que quedó (D23): una tasa de otro país es 422 aunque llegue en la misma edición que cambia
/// la empresa. Nunca escribe en el cliente.
/// </summary>
internal static class OrderWithholdingEdits
{
    public static async Task<OrderWithholdingEditsOutcome> ApplyAsync(
        Order order,
        IOrderEdits edits,
        OrderWithholdingEditsContext context,
        MemberId updatedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Con cuerpo, el resolver siempre devuelve la cuenta validada contra la empresa (o lanza).
        // Una empresa sin cuenta (spec 2026-11-17, R3) sale en la moneda del propio pedido: un pedido nunca cambia de
        // moneda (plan P10), y la cuenta sin banco no puede tropezar con esa regla.
        var account = await QuotationBillingAccountResolver.ResolveAsync(
            context.CompanyLookup, context.TenantId, edits.BillingAccount, edits.BillingCompanyId, order.Currency,
            cancellationToken);
        var target = account ?? order.BillingAccount;
        // La UVT es la del año del pedido (spec §7.3): la misma fecha con la que nació. Se lee una vez y sólo si hace falta.
        DateOnly? documentDate = null;
        async Task<DateOnly> DocumentDateAsync() => documentDate ??= DocumentDates.LocalDate(
            await context.TenantClock.GetAsync(context.TenantId, cancellationToken), order.CreatedAt);

        // «Sin retención» se valida siempre y antes de tocar el pedido (plan P12).
        string[] cleared = [.. (edits.ClearedWithholdingTypes ?? []).Distinct(StringComparer.Ordinal)];
        if (cleared.Length > 0)
        {
            var selectorTypes = edits.WithholdingTaxRateIds is { Count: > 0 } selectorIds
                ? await WithholdingSelection.MunicipalTypesOfAsync(
                    context.WithholdingLookup, context.TenantId, selectorIds, await DocumentDateAsync(), cancellationToken)
                : [];
            MunicipalSelectionMarker.Validate(cleared, target?.Country, selectorTypes, edits.WithholdingPercentages, "order");
        }

        // El municipio resultante se calcula antes de consultar (plan P4); el contexto también define qué id municipal
        // aceptan los selectores aunque nada cambie (A4).
        var municipal = await MunicipalContexts.ResolveAsync(
            context.WithholdingLookup,
            context.GeographyLookup,
            context.TenantId,
            order.ClientId,
            target,
            order.OperationCityFor(account, edits.OperationCity),
            explicitCity: edits.OperationCity.Kind == OperationCityChoiceKind.Explicit,
            TaxCountries.MunicipalTypes(target?.Country).Count == 0 ? default : await DocumentDateAsync(),
            "order",
            cancellationToken);

        string? accountSummary = null;
        var accountBefore = OrderChangeSummary.BillingAccountLabel(order.BillingAccount);
        var companyCityBefore = order.BillingAccount?.CityId;
        // Spec 2026-11-17 (R2): pasar a una empresa con IVA vuelve a gravar el envío cobrado con la tasa del tenant, que se
        // lee antes de tocar el pedido (puede faltar: order.order.freight_tax_rate_missing).
        int? freightTaxPercentageWithVat = order.NeedsFreightTaxRateFor(account)
            ? await context.Shipping.FreightTaxPercentageAsync(context.TenantId, "order.order", cancellationToken)
            : null;
        var applied = order.ApplyBillingContext(account, municipal, updatedBy, now, freightTaxPercentageWithVat);
        if (applied.AccountChanged)
        {
            accountSummary = OrderChangeSummary.BillingAccountChanged(
                accountBefore, OrderChangeSummary.BillingAccountLabel(order.BillingAccount)!);
        }

        var reResolution = applied.Municipal;
        var labelsBefore = OrderChangeSummary.WithholdingsLabel(order);
        var current = OrderedRates(order);

        // Los mismos de antes (también los heredados de la cotización): la foto se queda como se congeló, sin volver al
        // catálogo ni recalcular la UVT (Review Focus 1: un porcentaje editado sobrevive al reenvío del mismo selector).
        if (edits.WithholdingTaxRateIds is { } ids
            && !WithholdingSelection.IsUnchanged(ids, current, order.BillingAccount?.Country))
        {
            // A11 (plan P6): tras un cambio de empresa o de municipio el id elegido contra lo anterior se ignora y queda la
            // fila de la re-resolución; sin cambio, un id municipal que no es la tasa de la empresa es 422, salvo el de la fila
            // guardada, que se conserva tal cual (A18, plan P14).
            var scope = MunicipalSelectorScope.For(municipal, ignoreUnmatched: reResolution.Triggered) with
            {
                ReResolved = reResolution.Triggered ? [.. current.Where(IsMunicipal)] : [],
                Stored = reResolution.Triggered ? [] : current,
            };
            IReadOnlyList<WithholdingRate> rates = ids.Count == 0 && scope.ReResolved.Count == 0
                ? []
                : await WithholdingSelection.ResolveAsync(
                    context.WithholdingLookup,
                    context.TenantId,
                    ids,
                    order.BillingAccount?.Country,
                    await DocumentDateAsync(),
                    scope,
                    "order",
                    cancellationToken,
                    cleared.ToHashSet(StringComparer.Ordinal));
            order.SetWithholdings(rates, updatedBy, now);
        }

        // «Sin retención»: quita lo que haya quedado de cada tipo marcado. Si el selector ya lo había quitado, el
        // porcentaje editado perdido se anota igual (spec §7.6).
        var afterSelectors = OrderedRates(order);
        IReadOnlyList<WithholdingRate> clearedOverrides = cleared.Length == 0
            ? []
            :
            [
                .. current.Where(rate => rate.Overridden && IsMunicipal(rate) && cleared.Contains(rate.Type)
                    && afterSelectors.All(after => after.TaxRateId != rate.TaxRateId)),
                .. order.ClearMunicipalWithholdings(cleared, updatedBy, now),
            ];

        var labelsAfter = OrderChangeSummary.WithholdingsLabel(order);
        var currentIds = current.Select(rate => rate.TaxRateId).ToHashSet();
        // R11: una municipal nueva del selector para un municipio donde el cliente no es agente es una aplicación a mano.
        var notAgentManual = OrderedRates(order).FirstOrDefault(rate =>
            IsMunicipal(rate)
            && !currentIds.Contains(rate.TaxRateId)
            && municipal.Offers.Any(offer => offer.Type == rate.Type && !offer.CustomerIsAgent));

        var percentageChanges = ApplyPercentages(order, edits, reResolution.Triggered, updatedBy, now);

        return new OrderWithholdingEditsOutcome(accountSummary)
        {
            WithholdingsLabels = labelsBefore == labelsAfter ? null : (labelsBefore, labelsAfter),
            NotAgentManualRate = notAgentManual,
            Municipal = reResolution,
            ClearedOverrides = clearedOverrides,
            PercentageChanges = percentageChanges,
            BillingCompanyCityFilled = !applied.AccountChanged && order.BillingAccount?.CityId != companyCityBefore,
        };
    }

    // Spec §7.7: al final. Con cambio de empresa o de municipio en el mismo cuerpo (A11), un porcentaje municipal sin fila
    // de su tipo se ignora en silencio; sin cambio, la regla es la de siempre (not_editable).
    private static List<WithholdingPercentageChange> ApplyPercentages(
        Order order, IOrderEdits edits, bool contextChanged, MemberId updatedBy, DateTimeOffset now)
    {
        var changes = new List<WithholdingPercentageChange>();
        foreach (var entry in edits.WithholdingPercentages ?? [])
        {
            if (contextChanged
                && TaxCountries.IsMunicipal(order.BillingAccount?.Country, entry.Type)
                && !order.Withholdings.Any(row => string.Equals(row.Type, entry.Type, StringComparison.Ordinal)))
            {
                continue;
            }

            if (order.SetWithholdingPercentage(entry.Type, entry.Percentage, updatedBy, now) is { } change)
            {
                changes.Add(change);
            }
        }

        return changes;
    }

    private static List<WithholdingRate> OrderedRates(Order order) =>
        [.. order.Withholdings.OrderBy(withholding => withholding.Position).Select(withholding => withholding.Rate)];

    private static bool IsMunicipal(WithholdingRate rate) => TaxCountries.IsMunicipal(rate.Country, rate.Type);
}

/// <summary>
/// El historial de la retención municipal de un pedido (spec ReteICA por empresa §7.8), redactado sólo por el guardado: el
/// cambio de municipio y el de selectores van como <c>PartiesChanged</c>; cada tarifa descartada, quitada o editada, como
/// <c>WithholdingRateEdited</c>.
/// </summary>
internal static class OrderMunicipalHistory
{
    public const string RateEditedAuditAction = "quotation.order.withholding_rate_edited";

    public sealed record Texts(string? OperationCitySummary, string? WithholdingsSummary, IReadOnlyList<string> RateEdited);

    public static async Task<Texts> DescribeAsync(
        IQuotationGeographyLookup geography, OrderWithholdingEditsOutcome outcome, HistoryFormat format,
        CancellationToken cancellationToken)
    {
        var municipal = outcome.Municipal;
        // Sólo «de un municipio a otro»: el spec no tiene texto para la primera vez que se fija (ver
        // QuotationChangeSummary.OperationCityChanged).
        var cityChanged = municipal.Triggered
            && municipal.OperationCityBefore is not null
            && municipal.OperationCityAfter is not null
            && municipal.OperationCityBefore != municipal.OperationCityAfter;
        Guid[] cityIds =
        [
            .. new[]
                {
                    cityChanged ? municipal.OperationCityBefore : null,
                    cityChanged ? municipal.OperationCityAfter : null,
                    outcome.NotAgentManualRate?.CityId,
                }
                .OfType<Guid>()
                .Distinct(),
        ];
        IReadOnlyDictionary<Guid, string> names = cityIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await geography.FindCityNamesAsync(cityIds, cancellationToken);

        string? citySummary = null;
        if (cityChanged
            && names.TryGetValue(municipal.OperationCityBefore!.Value, out var from)
            && names.TryGetValue(municipal.OperationCityAfter!.Value, out var to))
        {
            citySummary = QuotationChangeSummary.OperationCityChanged(from, to);
        }

        string? withholdingsSummary = null;
        if (outcome.WithholdingsLabels is { } labels)
        {
            (string Type, string City)? notAgent = outcome.NotAgentManualRate is { } manual
                ? (manual.Type, manual.CityId is { } cityId && names.TryGetValue(cityId, out var city) ? city : manual.Name)
                : null;
            withholdingsSummary = OrderChangeSummary.WithholdingsChanged(labels.Before, labels.After, notAgent);
        }

        // Lo descartado y lo quitado primero, después lo editado en el mismo cuerpo (spec §7.7).
        IReadOnlyList<string> rateEdited =
        [
            .. municipal.DiscardedOverrides.Select(rate =>
                QuotationChangeSummary.WithholdingOverrideDiscarded(rate.Type, rate.Percentage, format)),
            .. municipal.RemovedNotAgent.Select(rate => OrderChangeSummary.WithholdingRemovedNotAgent(rate.Type)),
            .. outcome.ClearedOverrides.Select(rate =>
                QuotationChangeSummary.WithholdingOverrideDiscarded(rate.Type, rate.Percentage, format)),
            .. outcome.PercentageChanges.Select(change => change.Restored
                ? QuotationChangeSummary.WithholdingRateRestored(change.Name, change.To, format)
                : QuotationChangeSummary.WithholdingRateEdited(change.Name, change.From, change.To, format)),
        ];
        return new Texts(citySummary, withholdingsSummary, rateEdited);
    }
}

/// <summary>Corre el validador de la edición diciéndole si el pedido es de proyecto, que es lo único que sus reglas
/// necesitan y no sale del cuerpo.</summary>
internal static class OrderEditsValidation
{
    public const string ProjectOrderKey = "ProjectOrder";

    /// <summary>Las dos reglas que cambian en un pedido de proyecto (§12-C): admite la lista de líneas vacía e ignora el
    /// envío. Sin ninguno de los dos casos en el cuerpo, el tipo del pedido no cambia nada y no hace falta leerlo antes de
    /// validar.</summary>
    public static bool NeedsTheOrder(IOrderEdits edits) =>
        edits.Items.Count == 0 || !ShippingRequestValidator.Accepts(edits.Shipping);

    /// <param name="order">El pedido, si ya se leyó; ver <see cref="NeedsTheOrder"/>.</param>
    public static async Task ValidateAsync<TEdits>(
        IValidator<TEdits> validator, TEdits edits, Order? order, CancellationToken cancellationToken)
        where TEdits : IOrderEdits
    {
        var context = ValidationContext<TEdits>.CreateWithOptions(edits, options => options.ThrowOnFailures());
        if (order?.Kind == QuotationKind.Project)
        {
            context.RootContextData[ProjectOrderKey] = true;
        }

        await validator.ValidateAsync(context, cancellationToken);
    }
}

/// <summary>
/// Las reglas de la sección «Validación» del spec 2026-09-17. Abstracta a propósito: el escaneo de
/// validadores del composition root sólo registra las dos concretas. <paramref name="requireFileIds"/>
/// separa el guardado del cálculo previo, que todavía no tiene archivos (el <c>newFileId</c> sí se
/// valida en los dos: si llega, no puede ser <see cref="Guid.Empty"/>). Se ejecuta con
/// <see cref="OrderEditsValidation.ValidateAsync"/>, que dice si el pedido es de proyecto.
/// </summary>
public abstract class OrderEditsValidator<TEdits> : AbstractValidator<TEdits>
    where TEdits : IOrderEdits
{
    protected OrderEditsValidator(bool requireFileIds, bool requireBillingIdentification)
    {
        AddBillingRules(requireBillingIdentification);

        // Un pedido de proyecto se edita sin líneas (spec 2026-11-26 §12-C): su línea no cambia.
        RuleFor(edits => edits.Items)
            .Must((_, items, context) =>
                items.Count > 0 || context.RootContextData.ContainsKey(OrderEditsValidation.ProjectOrderKey))
            .WithMessage("At least one product is required.");
        RuleForEach(edits => edits.Items).ChildRules(item =>
        {
            item.RuleFor(line => line.VariantId).NotEmpty();
            item.RuleFor(line => line.Quantity).GreaterThan(0m);
            // D-4: IVA incluido y nunca negativo; que el pedido sea mayorista lo decide el dominio con su código.
            item.RuleFor(line => line.UnitPrice).GreaterThanOrEqualTo(0m).When(line => line.UnitPrice is not null);
        });
        RuleFor(edits => edits.Items)
            .Must(items => !HasDuplicates(items.Select(line => line.VariantId)))
            .WithMessage("La misma variante no puede aparecer más de una vez.");
        RuleForEach(edits => edits.Gifts).ChildRules(gift =>
        {
            gift.RuleFor(line => line.VariantId).NotEmpty();
            gift.RuleFor(line => line.Quantity).GreaterThan(0m);
        }).When(edits => edits.Gifts is not null);
        RuleFor(edits => edits.Gifts)
            .Must(gifts => !HasDuplicates(gifts!.Select(line => line.VariantId)))
            .WithMessage("La misma variante no puede ir más de una vez como regalo.")
            .When(edits => edits.Gifts is not null);

        RuleForEach(edits => edits.Proofs.Add).ChildRules(addition =>
        {
            addition.RuleFor(value => value.Amount).GreaterThan(0m);
            if (requireFileIds)
            {
                addition.RuleFor(value => value.FileId).NotNull().NotEqual(Guid.Empty);
            }
        });
        RuleForEach(edits => edits.Proofs.Update).SetValidator(new OrderPaymentProofUpdateRequestValidator());
        RuleFor(edits => edits.Proofs.Update)
            .Must(updates => !HasDuplicates(updates.Select(update => update.ProofId)))
            .WithMessage("The same payment proof cannot be updated more than once in a request.");
        RuleFor(edits => edits.Proofs)
            .Must(proofs => !proofs.Update.Select(update => update.ProofId).Intersect(proofs.RemoveIds).Any())
            .WithMessage("A payment proof cannot be updated and removed in the same request.");
        // Mismo motivo que AddOrderPaymentProofsValidator (revisión final, I2): un archivo, un adjunto.
        RuleFor(edits => edits.Proofs)
            .Must(proofs => !HasDuplicates(proofs.Add
                .Where(addition => addition.FileId is not null)
                .Select(addition => addition.FileId!.Value)
                .Concat(proofs.Update
                    .Where(update => update.NewFileId is not null)
                    .Select(update => update.NewFileId!.Value))))
            .WithMessage("The same file cannot be attached more than once in a request.");

        RuleFor(edits => edits.Shipping!)
            .SetValidator(new ShippingRequestValidator())
            .When((edits, context) =>
                edits.Shipping is not null
                && !context.RootContextData.ContainsKey(OrderEditsValidation.ProjectOrderKey));

        // Las notas no tienen tope (plan 2026-10-15, P2): heredan las de la cotización.
    }

    // Las mismas reglas de número y tipo de documento que QuotationPartiesRequestValidator, con la clave que
    // arma FluentValidation para este comando (Billing.Party.*). El guardado las exige; el cálculo previo sólo
    // rechaza lo mal escrito, porque la persona todavía está tecleando.
    private void AddBillingRules(bool requireBillingIdentification)
    {
        RuleFor(edits => edits.Billing!.Party!.IdentificationNumber)
            .NotEmpty()
            .WithMessage("The identification number is required when billing to someone else.")
            .When(edits => requireBillingIdentification && HasNamedOwnParty(edits));

        RuleFor(edits => edits.Billing!.Party!.IdentificationType)
            .NotEmpty()
            .WithMessage("The identification type is required when billing to someone else.")
            .When(edits => requireBillingIdentification && HasNamedOwnParty(edits));

        RuleFor(edits => edits.Billing!.Party!.IdentificationType)
            .Must(type => PartyFiscalTypesParser.SupportedIdentificationTypes
                .Contains(type!.Trim(), StringComparer.OrdinalIgnoreCase))
            .WithMessage($"The identification type must be one of {string.Join(", ", PartyFiscalTypesParser.SupportedIdentificationTypes)}.")
            .When(edits => !string.IsNullOrWhiteSpace(edits.Billing?.Party?.IdentificationType));

        // Aparte de la regla anterior para que un país mal escrito se informe sólo en el país y un código
        // desconocido sólo como código desconocido: esta corre cuando el código es conocido y el país tiene forma de ISO.
        RuleFor(edits => edits.Billing!.Party!.IdentificationType)
            .Must((edits, type) => PartyFiscalTypesParser.ParseIdentificationType(type)!.Value
                .IsAllowedFor(edits.Billing!.Party!.Country))
            .WithMessage("The identification type is not allowed for the country.")
            .When(edits => PartyDocumentRules.IsKnownTypeWithAValidCountry(edits.Billing?.Party?.IdentificationType, edits.Billing?.Party?.Country));

        RuleFor(edits => edits.Billing!.Party!.PersonType)
            .Must(type => PartyFiscalTypesParser.SupportedPersonTypes
                .Contains(type!.Trim(), StringComparer.OrdinalIgnoreCase))
            .WithMessage("The person type must be one of Natural or Juridica.")
            .When(edits => !string.IsNullOrWhiteSpace(edits.Billing?.Party?.PersonType));

        RuleFor(edits => edits.Billing!.Party!.IdentificationNumber)
            .Must(number => number!.Trim().Length <= QuotationPartyDetails.IdentificationNumberMaxLength)
            .WithMessage(
                $"The identification number cannot exceed {QuotationPartyDetails.IdentificationNumberMaxLength} characters.")
            .When(edits => edits.Billing?.Party?.IdentificationNumber is not null);
    }

    // Con consumidor final una parte propia es otro error, con su código de dominio
    // (order.billing.final_consumer_conflict): un 422 de campo lo taparía.
    private static bool HasNamedOwnParty(TEdits edits) =>
        edits.Billing is { BillsToFinalConsumer: false, Party: { } party }
        && !string.IsNullOrWhiteSpace(party.Name);

    private static bool HasDuplicates<TValue>(IEnumerable<TValue> values)
    {
        var seen = new HashSet<TValue>();
        return values.Any(value => !seen.Add(value));
    }
}
