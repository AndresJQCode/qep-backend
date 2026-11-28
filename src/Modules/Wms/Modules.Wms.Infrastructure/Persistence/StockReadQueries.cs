using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;

namespace Modules.Wms.Infrastructure.Persistence;

/// <summary>
/// Las lecturas de saldos e historia, en SQL crudo sobre <c>wms.v_stock_rows</c>, <c>wms.v_stock_summary</c> y
/// <c>wms.stock_movements</c> (RF-021, RF-022). Las fórmulas de <c>available</c>, <c>pickable</c>, <c>received</c> y <c>blocked</c> viven
/// en las vistas y no se repiten acá. Un filtro opcional que no viene se omite del texto: PostgreSQL no tipa un parámetro <c>NULL</c>
/// suelto (<c>could not determine data type of parameter</c>), así que nunca se manda como nulo.
/// </summary>
internal sealed class StockReadQueries(WmsDbContext dbContext) : IStockReadQueries, IMovementReader
{
    private sealed record TotalsRow(int Total, decimal OnHand, decimal Reserved, decimal Available, decimal Pickable);

    public async Task<StockSearchResult> SearchAsync(StockFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var where = new SqlWhere();
        where.Add("r.tenant_id = {0}", filter.TenantId);
        where.AddIf(filter.WarehouseId, "r.warehouse_id = {0}");
        where.AddIf(filter.OwnerId, "r.owner_id = {0}");
        where.AddIf(filter.VariantId, "r.variant_id = {0}");
        where.AddIf(filter.LocationId, "r.location_id = {0}");
        where.AddIf(filter.LotId, "r.lot_id = {0}");
        if (filter.Status is { } status)
        {
            where.Add("r.status = {0}", status.ToString());
        }

        if (filter.OnlyAvailable)
        {
            where.Add("r.available > 0");
        }

        if (!filter.IncludeVirtual)
        {
            where.Add("NOT r.is_virtual");
        }

        if (filter.Search is not null)
        {
            // Sólo código de ubicación y de lote en este corte. Los comodines del texto del usuario se escapan: «A_1» no es «A cualquier 1».
            where.Add("(r.location_code ILIKE {0} ESCAPE '\\' OR lot.code ILIKE {0} ESCAPE '\\')", "%" + EscapeLike(filter.Search) + "%");
        }

        const string from = """
            FROM wms.v_stock_rows r
            JOIN wms.warehouses w ON w.id = r.warehouse_id
            JOIN wms.owners o ON o.id = r.owner_id
            LEFT JOIN wms.lots lot ON lot.id = r.lot_id
            """;

        // totals siempre excluye virtuales, aunque includeVirtual las muestre en la lista: el -10 de ADJUSTMENT no puede restarle al 10 del bin.
        var totalsRow = (await dbContext.Database.SqlQueryRaw<TotalsRow>(
            """
            SELECT count(*)::int AS "Total",
                   coalesce(sum(CASE WHEN NOT r.is_virtual THEN r.on_hand ELSE 0 END), 0) AS "OnHand",
                   coalesce(sum(CASE WHEN NOT r.is_virtual THEN r.reserved ELSE 0 END), 0) AS "Reserved",
                   coalesce(sum(r.available), 0) AS "Available",
                   coalesce(sum(r.pickable), 0) AS "Pickable"
            @FROM@
            WHERE @WHERE@
            """.Replace("@FROM@", from, StringComparison.Ordinal).Replace("@WHERE@", where.Text, StringComparison.Ordinal),
            where.Arguments.ToArray()).ToListAsync(cancellationToken)).Single();

        // Inventario básico §5.4: la reserva de demanda no tiene ubicación, lote ni estado, así que cuenta cuando el filtro no los pide, y
        // sólo para el bodega, dueño y variante del filtro. Con esto el total de la lista y el resumen dicen lo mismo. Las filas por
        // ubicación no la muestran.
        var demandReserved = 0m;
        if (filter.LocationId is null && filter.LotId is null && filter.Search is null && (filter.Status is null or StockStatus.Available))
        {
            var demand = new SqlWhere();
            demand.Add("d.tenant_id = {0}", filter.TenantId);
            demand.Add("d.status = 'Open' AND d.location_id IS NULL");
            demand.AddIf(filter.WarehouseId, "d.warehouse_id = {0}");
            demand.AddIf(filter.OwnerId, "d.owner_id = {0}");
            demand.AddIf(filter.VariantId, "d.variant_id = {0}");
            demandReserved = (await dbContext.Database.SqlQueryRaw<decimal>(
                """
                SELECT coalesce(sum(d.quantity), 0) AS "Value"
                FROM wms.reservations d
                WHERE @WHERE@
                """.Replace("@WHERE@", demand.Text, StringComparison.Ordinal),
                demand.Arguments.ToArray()).ToListAsync(cancellationToken)).Single();
        }

        var page = new SqlWhere(where);
        var limit = page.Parameter(filter.Limit);
        var offset = page.Parameter(filter.Offset);
        var rows = await dbContext.Database.SqlQueryRaw<StockBalanceRow>(
            """
            SELECT r.owner_id AS "OwnerId", o.name AS "OwnerName", r.warehouse_id AS "WarehouseId", w.code AS "WarehouseCode",
                   r.location_id AS "LocationId", r.location_code AS "LocationCode", r.location_path AS "LocationPath",
                   r.variant_id AS "VariantId", r.lot_id AS "LotId", lot.code AS "LotCode", lot.expiration_date AS "ExpirationDate",
                   r.status AS "Status", r.on_hand AS "OnHand", r.reserved AS "Reserved", r.available AS "Available",
                   r.pickable AS "Pickable", r.updated_at AS "UpdatedAt"
            @FROM@
            WHERE @WHERE@
            ORDER BY w.code, r.location_code, r.variant_id, lot.code NULLS FIRST, r.status, r.id
            LIMIT @LIMIT@ OFFSET @OFFSET@
            """.Replace("@FROM@", from, StringComparison.Ordinal)
                .Replace("@WHERE@", page.Text, StringComparison.Ordinal)
                .Replace("@LIMIT@", limit, StringComparison.Ordinal)
                .Replace("@OFFSET@", offset, StringComparison.Ordinal),
            page.Arguments.ToArray()).ToListAsync(cancellationToken);

        return new StockSearchResult(rows, totalsRow.Total, new StockTotalsDto(
            totalsRow.OnHand, totalsRow.Reserved + demandReserved, totalsRow.Available - demandReserved, totalsRow.Pickable));
    }

    // El cast a uuid tipa el parámetro cuando el lote es nulo (PostgreSQL no tipa un NULL suelto).
    public async Task<decimal> FindOnHandAsync(Guid tenantId, StockKey key, CancellationToken cancellationToken)
    {
        var status = key.Status.ToString();
        var lot = key.LotId;
        var rows = await dbContext.Database.SqlQuery<decimal>(
            $"""
            SELECT COALESCE(SUM(on_hand), 0) AS "Value"
            FROM wms.stock_balances
            WHERE tenant_id = {tenantId} AND owner_id = {key.OwnerId} AND location_id = {key.LocationId}
              AND variant_id = {key.VariantId} AND lot_id IS NOT DISTINCT FROM {lot}::uuid AND status = {status}
              AND handling_unit_id IS NULL
            """).ToListAsync(cancellationToken);
        return rows.Single();
    }

    public async Task LockBalancesAsync(Guid tenantId, IReadOnlyCollection<StockKey> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("LockBalancesAsync requires the transaction from BeginTransactionAsync.");
        }

        var ordered = keys.Distinct().Order(StockKeyComparer.Instance).ToList();
        foreach (var key in ordered)
        {
            var status = key.Status.ToString();
            var lot = key.LotId;
            await dbContext.Database.SqlQuery<int>(
                $"""
                SELECT 1 AS "Value"
                FROM wms.stock_balances
                WHERE tenant_id = {tenantId} AND owner_id = {key.OwnerId} AND location_id = {key.LocationId}
                  AND variant_id = {key.VariantId} AND lot_id IS NOT DISTINCT FROM {lot}::uuid AND status = {status}
                  AND handling_unit_id IS NULL
                FOR UPDATE
                """).ToListAsync(cancellationToken);
        }
    }

    private sealed record BalanceKeyRow(Guid VariantId, Guid LocationId, Guid? LotId);

    public async Task<IReadOnlySet<(Guid VariantId, Guid LocationId, Guid? LotId)>> FindNonZeroBalanceKeysAsync(
        Guid tenantId,
        Guid ownerId,
        IReadOnlyCollection<Guid> variantIds,
        IReadOnlyCollection<Guid> locationIds,
        CancellationToken cancellationToken)
    {
        var variants = variantIds.ToArray();
        var locations = locationIds.ToArray();
        var rows = await dbContext.Database.SqlQuery<BalanceKeyRow>(
            $"""
            SELECT DISTINCT variant_id AS "VariantId", location_id AS "LocationId", lot_id AS "LotId"
            FROM wms.stock_balances
            WHERE tenant_id = {tenantId} AND owner_id = {ownerId}
              AND variant_id = ANY({variants}) AND location_id = ANY({locations}) AND on_hand <> 0
            """).ToListAsync(cancellationToken);

        return rows.Select(row => (row.VariantId, row.LocationId, row.LotId)).ToHashSet();
    }

    public async Task<StockSummaryRow> SummarizeAsync(
        Guid tenantId, Guid? warehouseId, Guid? ownerId, Guid? variantId, CancellationToken cancellationToken)
    {
        var where = new SqlWhere();
        where.Add("s.tenant_id = {0}", tenantId);
        where.AddIf(warehouseId, "s.warehouse_id = {0}");
        where.AddIf(ownerId, "s.owner_id = {0}");
        where.AddIf(variantId, "s.variant_id = {0}");

        // Un agregado sin GROUP BY siempre devuelve una fila: sin saldos, ceros.
        return (await dbContext.Database.SqlQueryRaw<StockSummaryRow>(
            """
            SELECT coalesce(sum(s.on_hand), 0) AS "OnHand", coalesce(sum(s.received), 0) AS "Received",
                   coalesce(sum(s.available), 0) AS "Available", coalesce(sum(s.pickable), 0) AS "Pickable",
                   coalesce(sum(s.reserved), 0) AS "Reserved", coalesce(sum(s.blocked), 0) AS "Blocked",
                   coalesce(sum(s.in_transit), 0) AS "InTransit"
            FROM wms.v_stock_summary s
            WHERE @WHERE@
            """.Replace("@WHERE@", where.Text, StringComparison.Ordinal),
            where.Arguments.ToArray()).ToListAsync(cancellationToken)).Single();
    }

    public async Task<MovementSearchResult> ListMovementsAsync(MovementFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var where = new SqlWhere();
        where.Add("m.tenant_id = {0}", filter.TenantId);
        where.AddIf(filter.VariantId, "m.variant_id = {0}");
        where.AddIf(filter.LocationId, "(m.from_location_id = {0} OR m.to_location_id = {0})");
        where.AddIf(filter.LotId, "m.lot_id = {0}");
        where.AddIf(filter.DocumentId, "m.document_id = {0}");
        if (filter.DocumentType is not null)
        {
            where.Add("m.document_type = {0}", filter.DocumentType);
        }

        if (filter.Serial is not null)
        {
            where.Add(
                "m.serial_unit_id IN (SELECT su2.id FROM wms.serial_units su2 WHERE su2.tenant_id = m.tenant_id AND su2.serial = {0})",
                filter.Serial);
        }

        // Npgsql sólo escribe un timestamptz con offset 0.
        if (filter.From is { } from)
        {
            where.Add("m.occurred_at >= {0}", from.ToUniversalTime());
        }

        if (filter.To is { } to)
        {
            where.Add("m.occurred_at <= {0}", to.ToUniversalTime());
        }

        if (filter.After is { } after)
        {
            // Comparación de fila sobre la PK (occurred_at, id) que recorre IX_stock_movements_tenant_occurred: sin OFFSET, así que
            // la página mil cuesta lo mismo que la primera.
            var occurredAt = where.Parameter(after.OccurredAt.ToUniversalTime());
            var id = where.Parameter(after.Id);
            where.Add($"(m.occurred_at, m.id) < ({occurredAt}, {id})");
        }

        var limit = where.Parameter(filter.Limit + 1);
        var rows = await dbContext.Database.SqlQueryRaw<MovementRow>(
            """
            SELECT m.id AS "Id", m.occurred_at AS "OccurredAt", m.recorded_at AS "RecordedAt", m.reason AS "Reason",
                   m.quantity AS "Quantity", m.captured_uom AS "CapturedUom", m.captured_qty AS "CapturedQty",
                   m.conversion_factor AS "ConversionFactor",
                   lf.code AS "FromLocationCode", m.from_status AS "FromStatus", lt.code AS "ToLocationCode", m.to_status AS "ToStatus",
                   m.variant_id AS "VariantId", lot.code AS "LotCode", su.serial AS "Serial",
                   m.document_type AS "DocumentType", m.document_id AS "DocumentId", m.batch_id AS "BatchId",
                   m.reverses_movement_id AS "ReversesMovementId", m.actor_id AS "ActorId", m.actor_type AS "ActorType",
                   m.device_id AS "DeviceId"
            FROM wms.stock_movements m
            JOIN wms.locations lf ON lf.id = m.from_location_id
            JOIN wms.locations lt ON lt.id = m.to_location_id
            LEFT JOIN wms.lots lot ON lot.id = m.lot_id
            LEFT JOIN wms.serial_units su ON su.id = m.serial_unit_id
            WHERE @WHERE@
            ORDER BY m.occurred_at DESC, m.id DESC
            LIMIT @LIMIT@
            """.Replace("@WHERE@", where.Text, StringComparison.Ordinal).Replace("@LIMIT@", limit, StringComparison.Ordinal),
            where.Arguments.ToArray()).ToListAsync(cancellationToken);

        // La fila de más sólo dice que hay otra página; nunca se devuelve.
        return rows.Count > filter.Limit
            ? new MovementSearchResult(rows.Take(filter.Limit).ToList(), HasMore: true)
            : new MovementSearchResult(rows, HasMore: false);
    }

    // El ledger sigue siendo el único escritor: acá sólo se lee, sin rastreo. La PK (id, occurred_at) da índice por prefijo al id.
    public Task<StockMovement?> GetAsync(Guid tenantId, Guid movementId, CancellationToken cancellationToken) =>
        dbContext.Set<StockMovement>()
            .AsNoTracking()
            .FirstOrDefaultAsync(movement => movement.TenantId == tenantId && movement.Id == movementId, cancellationToken);

    // EXISTS sobre IX_stock_movements_reverses: no baja una fila ni recorre particiones enteras.
    public Task<bool> IsReversedAsync(Guid tenantId, Guid movementId, CancellationToken cancellationToken) =>
        dbContext.Set<StockMovement>()
            .AsNoTracking()
            .AnyAsync(movement => movement.TenantId == tenantId && movement.ReversesMovementId == movementId, cancellationToken);

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>Arma un <c>WHERE</c> con parámetros posicionales <c>{n}</c> de <c>SqlQueryRaw</c>. Sólo los marcadores se arman a mano;
    /// todo valor viaja como parámetro. En <see cref="Add(string, object)"/> y <see cref="AddIf"/> el marcador <c>{0}</c> es el valor
    /// que se pasa, y se renumera al índice real.</summary>
    private sealed class SqlWhere
    {
        private readonly List<string> _conditions = [];
        private readonly List<object> _arguments = [];

        public SqlWhere()
        {
        }

        public SqlWhere(SqlWhere source)
        {
            _conditions.AddRange(source._conditions);
            _arguments.AddRange(source._arguments);
        }

        public string Text => string.Join(" AND ", _conditions);

        public IReadOnlyList<object> Arguments => _arguments;

        /// <summary>Agrega un valor a los parámetros y devuelve su marcador <c>{n}</c>, para armar un texto con varios.</summary>
        public string Parameter(object value)
        {
            _arguments.Add(value);
            return string.Create(CultureInfo.InvariantCulture, $"{{{_arguments.Count - 1}}}");
        }

        public void Add(string condition) => _conditions.Add(condition);

        public void Add(string condition, object value)
        {
            var marker = Parameter(value);
            _conditions.Add(condition.Replace("{0}", marker, StringComparison.Ordinal));
        }

        public void AddIf(Guid? value, string condition)
        {
            if (value is { } present)
            {
                Add(condition, present);
            }
        }
    }
}
