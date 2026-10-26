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
            if (!ordersByKey.TryGetValue(group.Key, out var order))
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

            // Desvío S7: sólo el que sigue como lo creó la importación (versión 1), activo y sin fusionar.
            var completes = !customer.IsMerged
                && customer.IsActive
                && customer.Version == 1
                && group.Any(occurrence => occurrence.CustomerCityOnly);
            (completes ? toComplete : stayIncomplete).Add(customer.TenantId);
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
