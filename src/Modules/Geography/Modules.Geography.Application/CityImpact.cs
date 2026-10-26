using Modules.Geography.Domain;

namespace Modules.Geography.Application;

/// <summary>Los grupos de impacto por tenant, comunes a confirmar (SC-11) y corregir (SC-12).</summary>
internal static class ImpactGroups
{
    /// <summary>Un grupo desde el tenant de cada elemento: <c>count: 0, byTenant: []</c> si no hay ninguno. Por tenant, el que
    /// más tiene primero y luego por nombre.</summary>
    public static ImpactCountDto Count(IEnumerable<Guid> tenantIds, IReadOnlyDictionary<Guid, string> tenantNames)
    {
        var byTenant = tenantIds
            .GroupBy(tenantId => tenantId)
            .Select(group => new ImpactTenantCountDto(
                group.Key, UnresolvedCityViews.TenantName(tenantNames, group.Key), group.Count()))
            .OrderByDescending(tenant => tenant.Count)
            .ThenBy(tenant => tenant.Name, StringComparer.Ordinal)
            .ToList();
        return new ImpactCountDto(byTenant.Sum(tenant => tenant.Count), byTenant);
    }

    /// <summary>La lista completa, sin tope, por tenant y número de pedido.</summary>
    public static ImpactOrderListDto Orders(IEnumerable<ImpactOrderDto> orders)
    {
        var list = orders
            .OrderBy(order => order.TenantName, StringComparer.Ordinal)
            .ThenBy(order => order.OrderNumber, StringComparer.Ordinal)
            .ToList();
        return new ImpactOrderListDto(list.Count, list);
    }
}

/// <summary>
/// Puro: el impacto de confirmar un texto como sinónimo (spec 2026-10-26 §4.4), desde las ocurrencias de la fila y lo que
/// <see cref="ICityCorrectionInsights"/> leyó de sus pedidos y clientes. Un pedido o cliente que ya no existe no cuenta en ningún
/// grupo. Las ocurrencias se cuentan por pedido y por cliente, sin repetir.
/// </summary>
public static class ResolveImpactCalculator
{
    private const string PendingOrderStatus = "Pending";

    public static ResolveImpactDto Compute(
        CityRefDto city,
        IReadOnlyList<UnresolvedCityOccurrence> occurrences,
        IReadOnlyList<OrderInsight> orders,
        IReadOnlyList<CustomerInsight> customers,
        IReadOnlyDictionary<Guid, string> tenantNames)
    {
        ArgumentNullException.ThrowIfNull(occurrences);
        var ordersByKey = orders.ToDictionary(order => (order.TenantId, order.OrderId));
        var customersByKey = customers.ToDictionary(customer => (customer.TenantId, customer.CustomerId));

        var toFix = new List<Guid>();
        var missingData = new List<Guid>();
        var withShipping = new List<Guid>();
        var notPending = new List<ImpactOrderDto>();
        foreach (var group in occurrences.GroupBy(occurrence => (occurrence.TenantId, occurrence.OrderId)))
        {
            // Un pedido que ya no existe, o uno de recoger en tienda (nunca recibe parte de envío, P10), no está en ningún grupo.
            if (!ordersByKey.TryGetValue(group.Key, out var order) || order.IsStorePickup)
            {
                continue;
            }

            if (!string.Equals(order.Status, PendingOrderStatus, StringComparison.Ordinal))
            {
                notPending.Add(new ImpactOrderDto(
                    order.TenantId, UnresolvedCityViews.TenantName(tenantNames, order.TenantId), order.OrderId, order.OrderNumber,
                    order.Status));
            }
            else if (order.HasShippingParty)
            {
                // Ya tiene parte de envío: no se toca (§4.4).
                withShipping.Add(order.TenantId);
            }
            else if (group.Any(occurrence => occurrence.PartyCityOnly))
            {
                // Sin parte y con todo lo demás: confirmar le crea la parte. Nunca un pedido de recoger en tienda (P10).
                toFix.Add(order.TenantId);
            }
            else
            {
                missingData.Add(order.TenantId);
            }
        }

        var toComplete = new List<Guid>();
        var stayIncomplete = new List<Guid>();
        var withCustomer = occurrences
            .Where(occurrence => occurrence.CustomerId is not null)
            .GroupBy(occurrence => (occurrence.TenantId, CustomerId: occurrence.CustomerId!.Value));
        foreach (var group in withCustomer)
        {
            // Uno ya completo (alguien lo completó) no está en ningún grupo.
            if (!customersByKey.TryGetValue(group.Key, out var customer) || customer.IsComplete)
            {
                continue;
            }

            if (!group.Any(occurrence => occurrence.CustomerCityOnly))
            {
                // La importación lo creó incompleto y le falta algo más que la ciudad: confirmar no lo completa.
                stayIncomplete.Add(customer.TenantId);
            }
            else if (!customer.IsMerged && customer.IsActive && customer.Version == 1)
            {
                // Desvío S7: sólo el que sigue como lo creó la importación (versión 1), activo y sin fusionar, se completa.
                toComplete.Add(customer.TenantId);
            }

            // Sólo le faltaba la ciudad pero ya no es elegible (fusionado, inactivo o editado): no le falta "algo más", así que
            // no está en ningún grupo.
        }

        return new ResolveImpactDto(
            city,
            ImpactGroups.Count(toFix, tenantNames),
            ImpactGroups.Count(missingData, tenantNames),
            ImpactGroups.Count(withShipping, tenantNames),
            ImpactGroups.Orders(notPending),
            ImpactGroups.Count(toComplete, tenantNames),
            ImpactGroups.Count(stayIncomplete, tenantNames));
    }
}

/// <summary>
/// Puro: el impacto de corregir un sinónimo a otra ciudad (spec 2026-10-26 §4.6), desde las huellas que dejó —de los dos orígenes,
/// O8— y las ocurrencias de la fila que lo confirmó. Sólo un pedido <c>Pending</c> se recorrige: uno que sigue como se escribió
/// cambia (<c>ordersToChange</c>) y uno que alguien editó se respeta (<c>ordersEdited</c>); uno que ya no está <c>Pending</c> se
/// lista con su estado, completo, para avisar al tenant. Un pedido que ya no existe no cuenta en ningún grupo. Un cliente sigue
/// como se escribió mientras su versión sea la de la huella; uno que ya no existe cuenta como editado (la lectura no distingue
/// «editado» de «ya no está», y en los dos casos no se toca).
/// </summary>
public static class CorrectImpactCalculator
{
    private const string PendingOrderStatus = "Pending";
    private const string OrderTarget = "Order";
    private const string CustomerTarget = "Customer";

    public static CorrectImpactDto Compute(
        CityRefDto from,
        CityRefDto to,
        IReadOnlyList<CorrectionInsight> corrections,
        IReadOnlyList<UnresolvedCityOccurrence> occurrences,
        IReadOnlyDictionary<Guid, string> tenantNames)
    {
        ArgumentNullException.ThrowIfNull(corrections);
        ArgumentNullException.ThrowIfNull(occurrences);
        var ordersToChange = new List<Guid>();
        var ordersEdited = new List<Guid>();
        var notPending = new List<ImpactOrderDto>();
        var customersToChange = new List<Guid>();
        var customersEdited = new List<Guid>();
        foreach (var correction in corrections)
        {
            if (string.Equals(correction.Target, OrderTarget, StringComparison.Ordinal))
            {
                if (correction.OrderStatus is not { } status)
                {
                    continue;
                }

                if (!string.Equals(status, PendingOrderStatus, StringComparison.Ordinal))
                {
                    notPending.Add(new ImpactOrderDto(
                        correction.TenantId,
                        UnresolvedCityViews.TenantName(tenantNames, correction.TenantId),
                        correction.TargetId,
                        correction.OrderNumber ?? correction.TargetId.ToString(),
                        status));
                }
                else
                {
                    (correction.StillAsWritten ? ordersToChange : ordersEdited).Add(correction.TenantId);
                }
            }
            else
            {
                (correction.StillAsWritten ? customersToChange : customersEdited).Add(correction.TenantId);
            }
        }

        // Una ocurrencia está corregida si su pedido o su cliente tiene huella; si ninguno la tiene, el sinónimo no la alcanzó
        // (el trabajo falló, se saltó o sigue pendiente) y sigue como está.
        var footprints = corrections
            .Select(correction => (correction.TenantId, correction.Target, correction.TargetId))
            .ToHashSet();
        var uncorrected = occurrences
            .Where(occurrence => !footprints.Contains((occurrence.TenantId, OrderTarget, occurrence.OrderId))
                && (occurrence.CustomerId is not { } customerId
                    || !footprints.Contains((occurrence.TenantId, CustomerTarget, customerId))))
            .Select(occurrence => occurrence.TenantId);

        return new CorrectImpactDto(
            from,
            to,
            ImpactGroups.Count(ordersToChange, tenantNames),
            ImpactGroups.Count(ordersEdited, tenantNames),
            ImpactGroups.Orders(notPending),
            ImpactGroups.Count(customersToChange, tenantNames),
            ImpactGroups.Count(customersEdited, tenantNames),
            ImpactGroups.Count(uncorrected, tenantNames));
    }
}
