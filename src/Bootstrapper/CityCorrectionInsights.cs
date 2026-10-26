using Microsoft.EntityFrameworkCore;
using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Geography.Infrastructure.Persistence;

namespace Bootstrapper;

/// <summary>
/// Adapta Quotations, Customers, Shopify, Tenancy e Identity al puerto <see cref="ICityCorrectionInsights"/> de Geography, que no
/// referencia ninguno (spec 2026-10-26 D-C3b, <c>GeographyLayerTests</c>). Es SQL crudo sobre la conexión de
/// <see cref="GeographyDbContext"/> y conoce por nombre <c>quotations.orders</c>, <c>quotations.order_parties</c>,
/// <c>customers.customers</c>, <c>shopify.city_corrections</c>, <c>shopify.city_correction_jobs</c>, <c>tenancy.tenants</c> e
/// <c>identity.users</c>. Todo cruce con una tabla de tenant une por <c>(tenant_id, id)</c>: un id suelto leería el pedido o el
/// cliente de otro tenant (<c>order_parties</c> no tiene <c>tenant_id</c> y entra por su pedido, ya filtrado).
///
/// Renombrar una columna de esas tablas compila y deja los endpoints de impacto respondiendo 500 (<c>42703</c>). Lo amarra
/// <c>CityCorrectionInsightsTests</c> (Shopify.IntegrationTests), que resuelve este adaptador del contenedor y lo corre contra el
/// esquema migrado de todos los módulos. Un cambio en la huella de <c>city_corrections</c> o en los estados de
/// <c>city_correction_jobs</c> se cambia acá y en esas pruebas.
/// </summary>
internal sealed class CityCorrectionInsights(GeographyDbContext geography) : ICityCorrectionInsights
{
    public async Task<IReadOnlyList<OrderInsight>> ReadOrdersAsync(
        IReadOnlyCollection<TenantOrderKey> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            return [];
        }

        var distinct = keys.Distinct().ToArray();
        var tenantIds = distinct.Select(key => key.TenantId).ToArray();
        var orderIds = distinct.Select(key => key.OrderId).ToArray();
        var rows = await geography.Database.SqlQuery<OrderRow>($"""
            SELECT o.tenant_id AS "TenantId", o.id AS "OrderId", o.order_number AS "OrderNumber", o.status AS "Status",
                   EXISTS (
                       SELECT 1 FROM quotations.order_parties p WHERE p.order_id = o.id AND p.role = 'Shipping'
                   ) AS "HasShippingParty",
                   o.is_store_pickup AS "IsStorePickup"
            FROM unnest({tenantIds}, {orderIds}) AS k(tenant_id, order_id)
            JOIN quotations.orders o ON o.tenant_id = k.tenant_id AND o.id = k.order_id
            """).ToListAsync(cancellationToken);

        return rows
            .Select(row => new OrderInsight(row.TenantId, row.OrderId, row.OrderNumber, row.Status, row.HasShippingParty, row.IsStorePickup))
            .ToArray();
    }

    public async Task<IReadOnlyList<CustomerInsight>> ReadCustomersAsync(
        IReadOnlyCollection<TenantCustomerKey> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            return [];
        }

        var distinct = keys.Distinct().ToArray();
        var tenantIds = distinct.Select(key => key.TenantId).ToArray();
        var customerIds = distinct.Select(key => key.CustomerId).ToArray();
        var rows = await geography.Database.SqlQuery<CustomerRow>($"""
            SELECT c.tenant_id AS "TenantId", c.id AS "CustomerId", c.completeness = 'Complete' AS "IsComplete",
                   c.merged_into_id IS NOT NULL AS "IsMerged", c.is_active AS "IsActive", c.version::bigint AS "Version"
            FROM unnest({tenantIds}, {customerIds}) AS k(tenant_id, customer_id)
            JOIN customers.customers c ON c.tenant_id = k.tenant_id AND c.id = k.customer_id
            """).ToListAsync(cancellationToken);

        return rows
            .Select(row => new CustomerInsight(row.TenantId, row.CustomerId, row.IsComplete, row.IsMerged, row.IsActive, row.Version))
            .ToArray();
    }

    /// <summary>
    /// «Sigue como se escribió» (§4.6): un pedido conserva su parte <c>Shipping</c> campo por campo como la dejó la huella —nombre,
    /// teléfono, correo, dirección, departamento y ciudad, con <c>IS NOT DISTINCT FROM</c> porque un campo nulo en los dos lados es
    /// igual—; un cliente sigue en la versión de la huella. Un pedido o cliente que ya no existe (o sin parte) no sigue igual.
    /// </summary>
    public async Task<IReadOnlyList<CorrectionInsight>> ReadCorrectionsAsync(
        CitySynonymId synonymId, CancellationToken cancellationToken)
    {
        var synonym = synonymId.Value;
        var rows = await geography.Database.SqlQuery<CorrectionRow>($"""
            SELECT c.tenant_id AS "TenantId", c.target AS "Target", c.target_id AS "TargetId", c.city_id AS "CityId",
                   o.order_number AS "OrderNumber", o.status AS "OrderStatus",
                   CASE c.target
                       WHEN 'Order' THEN p.id IS NOT NULL
                           AND p.name IS NOT DISTINCT FROM c.party_name
                           AND p.phone IS NOT DISTINCT FROM c.party_phone
                           AND p.email IS NOT DISTINCT FROM c.party_email
                           AND p.address IS NOT DISTINCT FROM c.party_address
                           AND p.department_id IS NOT DISTINCT FROM c.department_id
                           AND p.city_id IS NOT DISTINCT FROM c.city_id
                       ELSE cu.id IS NOT NULL AND cu.version::bigint IS NOT DISTINCT FROM c.customer_version
                   END AS "StillAsWritten"
            FROM shopify.city_corrections c
            LEFT JOIN quotations.orders o ON c.target = 'Order' AND o.tenant_id = c.tenant_id AND o.id = c.target_id
            LEFT JOIN quotations.order_parties p ON p.order_id = o.id AND p.role = 'Shipping'
            LEFT JOIN customers.customers cu ON c.target = 'Customer' AND cu.tenant_id = c.tenant_id AND cu.id = c.target_id
            WHERE c.synonym_id = {synonym}
            ORDER BY c.applied_at, c.id
            """).ToListAsync(cancellationToken);

        return rows
            .Select(row => new CorrectionInsight(
                row.TenantId, row.Target, row.TargetId, row.CityId, row.OrderNumber, row.OrderStatus, row.StillAsWritten))
            .ToArray();
    }

    /// <summary>
    /// Las huellas cuentan los dos orígenes (O8). De los trabajos, sólo <c>Pending</c> y <c>Failed</c>: un <c>Applied</c> ya es una
    /// huella y un <c>Skipped</c> no se hizo. El <c>unnest</c> de los ids pedidos es lo que garantiza una fila por id, también en
    /// cero.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, SynonymCorrectionCounts>> CountCorrectionsAsync(
        IReadOnlyCollection<CitySynonymId> synonymIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(synonymIds);
        if (synonymIds.Count == 0)
        {
            return new Dictionary<Guid, SynonymCorrectionCounts>();
        }

        var ids = synonymIds.Select(id => id.Value).Distinct().ToArray();
        var rows = await geography.Database.SqlQuery<CountsRow>($"""
            SELECT s.id AS "SynonymId",
                   (SELECT count(*)::int FROM shopify.city_corrections c WHERE c.synonym_id = s.id AND c.target = 'Order') AS "Orders",
                   (SELECT count(*)::int FROM shopify.city_corrections c WHERE c.synonym_id = s.id AND c.target = 'Customer') AS "Customers",
                   (SELECT count(*)::int FROM shopify.city_correction_jobs j WHERE j.synonym_id = s.id AND j.status = 'Pending') AS "PendingJobs",
                   (SELECT count(*)::int FROM shopify.city_correction_jobs j WHERE j.synonym_id = s.id AND j.status = 'Failed') AS "FailedJobs"
            FROM unnest({ids}) AS s(id)
            """).ToListAsync(cancellationToken);

        return rows.ToDictionary(
            row => row.SynonymId,
            row => new SynonymCorrectionCounts(row.Orders, row.Customers, row.PendingJobs, row.FailedJobs));
    }

    public async Task<IReadOnlyDictionary<Guid, string>> ReadTenantNamesAsync(
        IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantIds);
        if (tenantIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var ids = tenantIds.Distinct().ToArray();
        var rows = await geography.Database.SqlQuery<NamedRow>($"""
            SELECT t.id AS "Id", t.display_name AS "Value" FROM tenancy.tenants t WHERE t.id = ANY({ids})
            """).ToListAsync(cancellationToken);

        return rows.ToDictionary(row => row.Id, row => row.Value);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> ReadUserEmailsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var ids = userIds.Distinct().ToArray();
        var rows = await geography.Database.SqlQuery<NamedRow>($"""
            SELECT u.id AS "Id", u.email AS "Value" FROM identity.users u WHERE u.id = ANY({ids})
            """).ToListAsync(cancellationToken);

        return rows.ToDictionary(row => row.Id, row => row.Value);
    }

    private sealed class OrderRow
    {
        public Guid TenantId { get; init; }

        public Guid OrderId { get; init; }

        public string OrderNumber { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;

        public bool HasShippingParty { get; init; }

        public bool IsStorePickup { get; init; }
    }

    private sealed class CustomerRow
    {
        public Guid TenantId { get; init; }

        public Guid CustomerId { get; init; }

        public bool IsComplete { get; init; }

        public bool IsMerged { get; init; }

        public bool IsActive { get; init; }

        public long Version { get; init; }
    }

    private sealed class CorrectionRow
    {
        public Guid TenantId { get; init; }

        public string Target { get; init; } = string.Empty;

        public Guid TargetId { get; init; }

        public Guid CityId { get; init; }

        public string? OrderNumber { get; init; }

        public string? OrderStatus { get; init; }

        public bool StillAsWritten { get; init; }
    }

    private sealed class CountsRow
    {
        public Guid SynonymId { get; init; }

        public int Orders { get; init; }

        public int Customers { get; init; }

        public int PendingJobs { get; init; }

        public int FailedJobs { get; init; }
    }

    private sealed class NamedRow
    {
        public Guid Id { get; init; }

        public string Value { get; init; } = string.Empty;
    }
}
