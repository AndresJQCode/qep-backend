using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Shopify.Domain;
using Modules.Tenancy.Application;

namespace Modules.Shopify.Application;

/// <summary>Qué pasa con un grupo (spec 2026-11-14 §3). Viaja por nombre.</summary>
public enum ShopifyEditGroupOutcome
{
    /// <summary>Cambió en Shopify y el pedido sigue como QEP lo escribió: se aplica (o se aplicó).</summary>
    Apply,

    /// <summary>No se aplica: el asesor lo tocó (<c>qep_changed</c>) o no se puede (<c>city_unresolved</c>, …). Gana QEP.</summary>
    Conflict,

    /// <summary>Sólo se muestra (el comprador): nunca toca el pedido ni el cliente (plan D17).</summary>
    Informative,
}

/// <summary>Un grupo del cambio con su decisión y sus filas, tal como se guarda en <c>order_edits.groups</c> y se pinta.</summary>
public sealed record ShopifyEditGroup(
    ShopifyEditGroupKind Kind, ShopifyEditGroupOutcome Outcome, string? Reason, IReadOnlyList<ShopifyEditRow> Rows);

/// <summary>
/// El plan de un cambio. <see cref="Groups"/> no trae los grupos eco (<see cref="Echoes"/>: el valor nuevo de Shopify ya es el del
/// pedido). <see cref="BlockReason"/> bloquea el cambio entero. <see cref="Draft"/> son los grupos que tocan el pedido; es
/// <c>null</c> si no hay ninguno que aplicar o si el cambio está bloqueado.
/// </summary>
public sealed record ShopifyEditPlan(
    IReadOnlyList<ShopifyEditGroup> Groups,
    IReadOnlyList<ShopifyEditGroupKind> Echoes,
    string? BlockReason,
    ShopifyOrderEditDraft? Draft)
{
    public bool IsEmpty => Groups.Count == 0;
}

/// <summary>
/// Spec 2026-11-14 §3 y §4: decide, grupo por grupo del diff entre la versión aplicada (<c>applied</c>) y la nueva
/// (<c>incoming</c>), si se aplica, es conflicto o es eco, comparando el pedido de QEP de <b>ahora</b> (<c>order</c>) con la
/// proyección (lo que QEP escribió a partir de la versión aplicada) y con el candidato (lo que saldría de la versión nueva):
/// <list type="bullet">
/// <item>candidato = pedido → eco: no es cambio.</item>
/// <item>pedido ≠ proyección → conflicto <c>qep_changed</c>: el asesor lo tocó y gana QEP.</item>
/// <item>si no → se aplica.</item>
/// </list>
/// La ciudad, el documento, las líneas y el envío se resuelven con las mismas reglas de la importación. Los precios son los de
/// Shopify (<see cref="ShopifyPricing"/>), sin descuentos de catálogo. El cambio entero se bloquea, en este orden, si el pedido no se
/// edita, si su pago no está entre los que la tienda admite, o si una línea no empareja (nunca se aplica a medias, plan D5).
/// </summary>
public sealed class ShopifyEditPlanner(
    ShopifyLineResolver lines,
    ShopifyShippingResolver shipping,
    ShopifyCustomerResolver customers,
    IShopifyCustomerDirectory customerDirectory,
    IShopifyUnresolvedCityReporter unresolvedCities,
    IShopifyGeographyLookup geography,
    IClock clock)
{
    public async Task<ShopifyEditPlan> PlanAsync(
        ShopifyStore store,
        ShopifyOrderMirror row,
        ShopifyOrderSnapshot applied,
        ShopifyOrderProjection projection,
        ShopifyOrderSnapshot incoming,
        ShopifyOrderState order,
        string? qepAttributesJson,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(applied);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(order);
        var groups = new List<ShopifyEditGroup>();
        var echoes = new List<ShopifyEditGroupKind>();
        string? unmatched = null;
        IReadOnlyList<ShopifyOrderLineDraft>? lineDraft = null;
        ShopifyShippingDraft? shippingDraft = null;
        ShopifyPartyDraft? partyDraft = null;
        var replaceNotes = false;
        string? notes = null;

        foreach (var diff in ShopifyOrderDiff.Compute(applied, incoming, store))
        {
            ShopifyEditGroup? group = null;
            switch (diff.Kind)
            {
                case ShopifyEditGroupKind.Buyer:
                    group = await BuyerAsync(store, incoming, order, diff, cancellationToken);
                    break;
                case ShopifyEditGroupKind.Attributes:
                    var currentQep = qepAttributesJson is null ? projection.Attributes : ReadAttributes(qepAttributesJson);
                    group = SameAttributes(incoming.Attributes, currentQep)
                        ? null
                        : !SameAttributes(currentQep, projection.Attributes)
                            ? Conflict(diff, ShopifyEditReasons.QepChanged)
                            : Apply(diff);
                    break;
                case ShopifyEditGroupKind.Note:
                    var note = Blank(incoming.Note);
                    group = string.Equals(note, order.BuyerNotes, StringComparison.Ordinal)
                        ? null
                        : !string.Equals(order.BuyerNotes, projection.BuyerNotes, StringComparison.Ordinal)
                            ? Conflict(diff, ShopifyEditReasons.QepChanged)
                            : Apply(diff);
                    if (group?.Outcome == ShopifyEditGroupOutcome.Apply)
                    {
                        (replaceNotes, notes) = (true, note);
                    }

                    break;
                case ShopifyEditGroupKind.ShippingAddress:
                    (group, partyDraft) = await AddressAsync(row, applied, incoming, order, projection, diff, cancellationToken);
                    break;
                case ShopifyEditGroupKind.Shipping:
                    (group, shippingDraft) = await ShippingAsync(row, incoming, order, projection, diff, cancellationToken);
                    break;
                case ShopifyEditGroupKind.Lines:
                    (group, lineDraft, unmatched) = await LinesAsync(row, incoming, order, projection, diff, cancellationToken);
                    break;
            }

            if (group is null)
            {
                echoes.Add(diff.Kind);
            }
            else
            {
                groups.Add(group);
            }
        }

        var blockReason = !order.IsEditable
            ? ShopifyEditReasons.OrderNotEditable
            : !store.EditPaymentStatuses.Contains(order.PaymentStatus, StringComparer.Ordinal)
                ? ShopifyEditReasons.PaymentStatusNotAllowed
                : unmatched;
        var touchesOrder = lineDraft is not null || shippingDraft is not null || partyDraft is not null || replaceNotes;
        var draft = groups.Count == 0 || blockReason is not null || !touchesOrder
            ? null
            : new ShopifyOrderEditDraft(
                row.TenantId, order.OrderId, order.Version, lineDraft, shippingDraft, partyDraft, replaceNotes, notes, clock.UtcNow);
        return new ShopifyEditPlan(groups, echoes, blockReason, draft);
    }

    /// <summary>Los atributos que el asesor corrigió (<c>shopify.orders.qep_attributes</c>), como objeto de clave y valor.</summary>
    public static IReadOnlyDictionary<string, string> ReadAttributes(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];

    public static bool SameAttributes(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count
        && a.All(pair => b.TryGetValue(pair.Key, out var value) && string.Equals(pair.Value, value, StringComparison.Ordinal));

    // Plan D17: el comprador vive en el cliente de QEP, que es maestro; el grupo sólo se muestra. Si el documento nuevo es de otro
    // cliente, es conflicto: el pedido no cambia de cliente.
    private async Task<ShopifyEditGroup> BuyerAsync(
        ShopifyStore store, ShopifyOrderSnapshot incoming, ShopifyOrderState order, ShopifyGroupDiff diff, CancellationToken cancellationToken)
    {
        if (ShopifyCustomerResolver.DocumentOf(store, incoming) is { } document
            && await customerDirectory.FindByDocumentAsync(store.TenantId, document.Type, document.Number, cancellationToken) is { } owner
            && owner.Id != order.ClientId)
        {
            return Conflict(diff, ShopifyEditReasons.CustomerChanged);
        }

        return new ShopifyEditGroup(diff.Kind, ShopifyEditGroupOutcome.Informative, null, diff.Rows);
    }

    private async Task<(ShopifyEditGroup? Group, ShopifyPartyDraft? Draft)> AddressAsync(
        ShopifyOrderMirror row,
        ShopifyOrderSnapshot applied,
        ShopifyOrderSnapshot incoming,
        ShopifyOrderState order,
        ShopifyOrderProjection projection,
        ShopifyGroupDiff diff,
        CancellationToken cancellationToken)
    {
        // El dominio rechaza una parte de envío con recogida. El pedido puede ser de recogida desde la tienda o porque «Editar
        // pedido» lo pasó a recoger (spec 2026-11-24 §10): en los dos casos gana QEP.
        if (order.IsStorePickup)
        {
            return (Conflict(diff, ShopifyEditReasons.StorePickup), null);
        }

        // Una parte de fuera de Colombia: antes de comparar se mira si lo que llegó es lo que el envío de esa parte mandó. Si no
        // lo es, sigue la comparación de siempre contra el pedido y la proyección.
        if (order.ShippingParty is { } own
            && !ShopifyPushAddress.IsColombia(own.Country)
            && await ShopifyPushAddress.IsForeignEchoAsync(
                geography, own, order.ShippingCountry, applied.ShippingAddress, incoming.ShippingAddress, cancellationToken))
        {
            return (null, null);
        }

        if (await DestinationAsync(row, incoming, order, cancellationToken) is not { } destination)
        {
            return (Conflict(diff, ShopifyEditReasons.CityUnresolved), null);
        }

        // El correo no viene en la dirección: la parte que ya existe conserva el suyo (el del comprador puede no estar).
        var withEmail = order.ShippingParty is { Email: { } email }
            ? incoming with { Buyer = incoming.Buyer with { Email = email } }
            : incoming;
        if (ShopifyOrderImporter.ShippingPartyFields(withEmail) is not { } fields)
        {
            return (Conflict(diff, ShopifyEditReasons.PartyIncomplete), null);
        }

        // Un payload que no guardó el zip no dice que Shopify no tenga ninguno: vale el del pedido.
        var postalCode = incoming.ShippingAddress is { ZipUnrecorded: true }
            ? order.ShippingParty?.PostalCode
            : ShopifyOrderImporter.ShippingPostalCode(incoming);
        var candidate = new ShopifyShippingFingerprint(
            fields.Name, fields.Phone, fields.Email, fields.Address, destination.DepartmentId, destination.CityId, postalCode,
            destination.Country, destination.CityName, destination.RegionId);
        // Un zip que Shopify ya tenía en la versión aplicada no es una novedad: si el pedido no guarda código postal (importado
        // antes de que viajara, o mandado sin uno propio, con el zip que Shopify conservaba), no cuenta como diferencia. Sin esto,
        // el eco de ese envío saldría como qep_changed. Tampoco cuenta si la versión aplicada no guardó el zip: no se sabe si
        // cambió. Un zip que Shopify sí cambió cuenta siempre.
        var compared = order.ShippingParty is { PostalCode: null }
            && (applied.ShippingAddress is { ZipUnrecorded: true }
                || string.Equals(postalCode, ShopifyOrderImporter.ShippingPostalCode(applied), StringComparison.Ordinal))
                ? candidate with { PostalCode = null }
                : candidate;
        if (ShopifyOrderProjection.SameParty(compared, order.ShippingParty))
        {
            return (null, null);
        }

        return !ShopifyOrderProjection.SameParty(order.ShippingParty, projection.ShippingParty)
            ? (Conflict(diff, ShopifyEditReasons.QepChanged), null)
            : (Apply(diff), new ShopifyPartyDraft(
                fields.Name, fields.Phone, fields.Email, fields.Address, destination.DepartmentId, destination.CityId, postalCode,
                destination.Country, destination.CityName, destination.RegionId));
    }

    // A dónde va la dirección que llegó, con la regla de la importación: fuera de Colombia, ShopifyForeignAddress (sin región no
    // es conflicto); en Colombia, la ciudad DIVIPOLA. null es city_unresolved; sólo la de Colombia va a la bandeja de ciudades.
    private async Task<(Guid? DepartmentId, Guid? CityId, string? Country, string? CityName, Guid? RegionId)?> DestinationAsync(
        ShopifyOrderMirror row, ShopifyOrderSnapshot incoming, ShopifyOrderState order, CancellationToken cancellationToken)
    {
        if (ShopifyForeignAddress.IsForeign(incoming.ShippingAddress))
        {
            return await ShopifyForeignAddress.ResolveAsync(geography, incoming.ShippingAddress!, cancellationToken) is { } foreign
                ? (null, null, foreign.Country, foreign.CityName, foreign.RegionId)
                : null;
        }

        var resolution = await customers.ResolveCityAsync(incoming.ShippingAddress, cancellationToken);
        if (resolution.City is not { } city)
        {
            await ReportCityAsync(row, incoming, order, resolution, cancellationToken);
            return null;
        }

        return (city.DepartmentId, city.CityId, null, null, null);
    }

    // Como la importación (RecordShippingCityAsync): la ciudad de Colombia que no casó, con su departamento resuelto, va a la bandeja
    // de ciudades. Es lo único que le falta a la parte de envío; el cliente no se toca. La ocurrencia es idempotente por la fila.
    private async Task ReportCityAsync(
        ShopifyOrderMirror row,
        ShopifyOrderSnapshot incoming,
        ShopifyOrderState order,
        ShopifyCityResolution resolution,
        CancellationToken cancellationToken)
    {
        if (resolution is not { UnmatchedDepartmentId: { } departmentId, CityText: { } cityText } || incoming.ShippingAddress is not { } address)
        {
            return;
        }

        await unresolvedCities.ReportAsync(
            new ShopifyUnresolvedCityReport(
                row.TenantId,
                row.Id,
                order.OrderId,
                null,
                departmentId,
                cityText,
                ShopifyUnresolvedCitySample.Of(address),
                Blank(address.ProvinceCode),
                PartyCityOnly: true,
                CustomerCityOnly: false,
                clock.UtcNow),
            cancellationToken);
    }

    private async Task<(ShopifyEditGroup? Group, ShopifyShippingDraft? Draft)> ShippingAsync(
        ShopifyOrderMirror row,
        ShopifyOrderSnapshot incoming,
        ShopifyOrderState order,
        ShopifyOrderProjection projection,
        ShopifyGroupDiff diff,
        CancellationToken cancellationToken)
    {
        // Spec 2026-11-17 R7: con el IVA congelado del pedido, no con el de la empresa hoy.
        var result = await shipping.ResolveAsync(row.TenantId, incoming, order.Currency, order.ChargesVat, cancellationToken);
        if (result.WaitReason is { } reason)
        {
            return (Conflict(diff, reason), null);
        }

        var candidate = result.Draft;
        if (candidate.IsStorePickup != order.IsStorePickup)
        {
            return (Conflict(diff, ShopifyEditReasons.StorePickupChanged), null);
        }

        if (candidate == order.Shipping)
        {
            return (null, null);
        }

        return order.Shipping != projection.Shipping ? (Conflict(diff, ShopifyEditReasons.QepChanged), null) : (Apply(diff), candidate);
    }

    private async Task<(ShopifyEditGroup? Group, IReadOnlyList<ShopifyOrderLineDraft>? Draft, string? Unmatched)> LinesAsync(
        ShopifyOrderMirror row,
        ShopifyOrderSnapshot incoming,
        ShopifyOrderState order,
        ShopifyOrderProjection projection,
        ShopifyGroupDiff diff,
        CancellationToken cancellationToken)
    {
        // Plan D5: las líneas de QEP no guardan el id de la línea de Shopify, así que se vuelven a emparejar todas.
        var (resolved, unmatchedSkus, unmatchedTitles) = await lines.ResolveAsync(
            row.TenantId, row.ConnectionId, incoming, order.Currency, order.ChargesVat, cancellationToken);
        if (unmatchedSkus.Count > 0 || unmatchedTitles.Count > 0)
        {
            return (Apply(diff), null, ShopifyEditReasons.ProductUnmatched);
        }

        // Como se guardaría: el precio con la regla de la moneda (una integración se precia con la de siempre de su moneda).
        var candidate = resolved
            .Select(line => new ShopifyOrderLineState(
                line.VariantId, line.ProductId, line.Quantity, RoundAsStored(line.UnitPrice, order.Currency), line.DiscountPercentage,
                line.TaxPercentage))
            .ToArray();
        if (ShopifyOrderProjection.SameLines(candidate, order.Lines))
        {
            return (null, null, null);
        }

        return !ShopifyOrderProjection.SameLines(order.Lines, projection.Lines)
            ? (Conflict(diff, ShopifyEditReasons.QepChanged), null, null)
            : (Apply(diff), resolved, null);
    }

    private static decimal RoundAsStored(decimal price, string currency)
    {
        try
        {
            return PriceRoundingDefaults.For(currency).Apply(price);
        }
        catch (KeyNotFoundException)
        {
            // Una moneda que el catálogo no conoce no llega a un pedido; si llegara, se compara sin redondear.
            return price;
        }
    }

    private static ShopifyEditGroup Apply(ShopifyGroupDiff diff) => new(diff.Kind, ShopifyEditGroupOutcome.Apply, null, diff.Rows);

    private static ShopifyEditGroup Conflict(ShopifyGroupDiff diff, string reason) =>
        new(diff.Kind, ShopifyEditGroupOutcome.Conflict, reason, diff.Rows);

    private static string? Blank(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
