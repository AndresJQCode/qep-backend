using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;
using Npgsql;

namespace Modules.Wms.Infrastructure.Persistence;

/// <summary>
/// El único escritor de movimientos y saldos. Valida el lote entero antes de escribir nada, escribe los saldos por SQL en el orden de
/// <see cref="StockKeyComparer"/> y deja los movimientos rastreados: quien llama confirma con <c>IWmsUnitOfWork.SaveChangesAsync</c>,
/// así saldos, movimientos, recibo y auditoría commitean o se revierten juntos.
/// </summary>
internal sealed class InventoryLedger(
    WmsDbContext dbContext,
    IWmsUnitOfWork unitOfWork,
    IWarehouseRepository warehouses,
    IVariantProfileRepository profiles,
    ILotRepository lots,
    IBalanceChangeQueue balanceChanges,
    IServiceScopeFactory scopeFactory,
    WmsMetrics metrics,
    IClock clock) : IInventoryLedger
{
    private const string NotEnoughStockMessage = "No hay suficiente saldo en la ubicación de origen.";
    private const string ReservedStockMessage = "Parte de ese saldo está reservado por otra demanda.";

    public async Task<LedgerPosting> PostAsync(LedgerBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("InventoryLedger.PostAsync requires the transaction from BeginTransactionAsync.");
        }

        EnsureNoHandlingUnits(batch);

        var tenantId = batch.TenantId;
        var locations = await LoadLocationsAsync(batch, cancellationToken);
        var variants = await LoadVariantRulesAsync(batch, cancellationToken);
        var now = clock.UtcNow;
        foreach (var entry in batch.Entries)
        {
            LedgerEntryRules.Validate(
                entry, variants[entry.From.VariantId], locations[entry.From.LocationId], locations[entry.To.LocationId], now);
        }

        var lotsById = await LoadLotsAsync(batch, cancellationToken);
        var serialUnits = await LoadSerialUnitsAsync(batch, cancellationToken);

        // Un lote recién creado en esta transacción sigue sólo en el rastreador, y la FK del saldo lo busca en la base.
        if (dbContext.ChangeTracker.Entries<Lot>().Any(entry => entry.State == EntityState.Added))
        {
            await unitOfWork.FlushAsync(cancellationToken);
        }

        foreach (var (key, delta) in LedgerDeltas.Net(batch.Entries))
        {
            await ApplyDeltaAsync(tenantId, key, delta, locations[key.LocationId], lotsById, cancellationToken);
        }

        var movements = batch.Entries.Select(entry => ToMovement(batch, entry, now)).ToArray();
        dbContext.Set<StockMovement>().AddRange(movements);
        foreach (var entry in batch.Entries.Where(entry => entry.SerialUnitId is not null))
        {
            serialUnits[entry.SerialUnitId!.Value].MoveTo(entry.To.LocationId, entry.To.Status);
        }

        // Después de todos los saldos: la cola es el último bloqueo que toma un lote, y siempre en el mismo orden (S26).
        var changed = batch.Entries
            .SelectMany(entry => new[] { entry.From, entry.To })
            .Select(key => new BalanceChangeKey(tenantId, key.OwnerId, locations[key.LocationId].WarehouseId, key.VariantId))
            .ToHashSet();
        await balanceChanges.EnqueueAsync(changed, cancellationToken);

        foreach (var reason in batch.Entries.GroupBy(entry => entry.Reason))
        {
            metrics.RecordMovements(reason.Key, reason.Count());
        }

        return new LedgerPosting(batch.BatchId, movements.Select(movement => movement.Id).ToArray());
    }

    // D-12, temporal: la unidad logística ya es dimensión de la clave, pero sin el agregado HandlingUnit nadie verifica «una unidad, una
    // ubicación» ni bloquea la fila de la unidad (spec §6.3). Ningún endpoint de WMS-D puede mandar una, así que no es un código de
    // negocio. Se exceptúa el reverso: copia las unidades del movimiento guardado, que ya aceptó el ledger (o sembró una prueba), y sin
    // ellas compensaría el saldo suelto. Lo retira el corte que crea las unidades.
    private static void EnsureNoHandlingUnits(LedgerBatch batch)
    {
        if (batch.Entries.Any(entry => entry.ReversesMovementId is null
            && (entry.From.HandlingUnitId is not null || entry.To.HandlingUnitId is not null)))
        {
            throw new InvalidOperationException("Handling units are not supported until the HandlingUnit aggregate exists.");
        }
    }

    private async Task<Dictionary<Guid, LedgerLocationInfo>> LoadLocationsAsync(LedgerBatch batch, CancellationToken ct)
    {
        var infos = new Dictionary<Guid, LedgerLocationInfo>();
        foreach (var id in batch.Entries.SelectMany(entry => new[] { entry.From.LocationId, entry.To.LocationId }).Distinct())
        {
            var location = await warehouses.GetLocationAsync(batch.TenantId, new LocationId(id), ct)
                ?? throw new ResourceNotFoundException(WmsErrorCodes.LocationNotFound, $"Location '{id}' was not found.");
            infos[id] = new LedgerLocationInfo(
                id,
                location.WarehouseId.Value,
                location.Code,
                location.AllowsNegative,
                location.Status == LocationStatus.Blocked,
                location.Status == LocationStatus.Counting);
        }

        return infos;
    }

    private async Task<Dictionary<Guid, LedgerVariantRules>> LoadVariantRulesAsync(LedgerBatch batch, CancellationToken ct)
    {
        var rules = new Dictionary<Guid, LedgerVariantRules>();
        foreach (var variantId in batch.Entries.Select(entry => entry.From.VariantId).Distinct())
        {
            var profile = await profiles.FindAsync(batch.TenantId, variantId, ct)
                ?? await CreateDefaultProfileAsync(batch.TenantId, variantId, ct);

            // TryAdd y no ToDictionary: el índice distingue mayúsculas y este diccionario no, y dos códigos que difieren sólo en eso no
            // pueden convertir cualquier movimiento de la variante en un 500. El dominio ya los rechaza; esto es por si entran por SQL.
            var factors = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var conversion in profile.Conversions)
            {
                factors.TryAdd(conversion.Code, conversion.Factor);
            }

            rules[variantId] = new LedgerVariantRules(profile.BaseUom.Value, profile.BaseUomPrecision, profile.Tracking, factors);
        }

        return rules;
    }

    // En un scope y una transacción propios, como IWmsProvisioner: el perfil por defecto no depende del movimiento, y guardarlo aparte
    // es lo que permite recargar. Dentro de la transacción del lote, perder la carrera (412) sólo se vería al confirmar, con los saldos
    // ya escritos y nada que reintentar. Aquí la perdedora relee y encuentra el perfil de la otra petición, que ya commiteó.
    // Invariante: quien llama no puede tener sin confirmar una escritura de perfil o identificador antes de PostAsync. Este scope
    // esperaría esa fila mientras quien llama espera a este scope, y PostgreSQL no ve ese ciclo: termina en el timeout del comando.
    private async Task<VariantLogisticsProfile> CreateDefaultProfileAsync(Guid tenantId, Guid variantId, CancellationToken ct)
    {
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            try
            {
                await scope.ServiceProvider.GetRequiredService<IVariantProfileRepository>().GetOrCreateDefaultAsync(tenantId, variantId, ct);
                await scope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>().SaveChangesAsync(ct);
            }
            catch (RequestConcurrencyException)
            {
                // Otra petición lo creó primero: se relee abajo.
            }
        }

        return await profiles.FindAsync(tenantId, variantId, ct)
            ?? throw new InvalidOperationException($"The default profile of variant '{variantId}' was saved but cannot be read.");
    }

    private async Task<IReadOnlyDictionary<Guid, Lot>> LoadLotsAsync(LedgerBatch batch, CancellationToken ct)
    {
        var ids = batch.Entries.Where(entry => entry.From.LotId is not null).Select(entry => entry.From.LotId!.Value).ToHashSet();
        var found = await lots.GetManyAsync(batch.TenantId, ids, ct);

        // El lote tiene que ser de esa variante y ese dueño: su FK sólo prueba que existe, y un saldo con el lote de otra variante
        // quedaría escrito sin error.
        foreach (var entry in batch.Entries.Where(entry => entry.From.LotId is not null))
        {
            if (!found.TryGetValue(entry.From.LotId!.Value, out var lot)
                || lot.VariantId != entry.From.VariantId
                || lot.OwnerId != entry.From.OwnerId)
            {
                throw new ResourceNotFoundException(WmsErrorCodes.LotNotFound, $"Lot '{entry.From.LotId}' was not found.");
            }
        }

        return found;
    }

    private async Task<Dictionary<Guid, SerialUnit>> LoadSerialUnitsAsync(LedgerBatch batch, CancellationToken ct)
    {
        var ids = batch.Entries.Where(entry => entry.SerialUnitId is not null).Select(entry => entry.SerialUnitId!.Value).ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var units = await dbContext.Set<SerialUnit>()
            .Where(unit => unit.TenantId == batch.TenantId && ids.Contains(unit.Id))
            .ToDictionaryAsync(unit => unit.Id, ct);
        if (ids.Any(id => !units.ContainsKey(id)))
        {
            throw new WmsDomainException(WmsErrorCodes.SerialRequired, "La serie del movimiento no existe.");
        }

        return units;
    }

    private async Task ApplyDeltaAsync(
        Guid tenantId,
        StockKey key,
        decimal delta,
        LedgerLocationInfo location,
        IReadOnlyDictionary<Guid, Lot> lotsById,
        CancellationToken ct)
    {
        try
        {
            if (delta < 0 && !location.AllowsNegative)
            {
                await DebitAsync(tenantId, key, delta, location, lotsById, ct);
            }
            else
            {
                await UpsertAsync(tenantId, key, delta, location, ct);
            }
        }
        catch (PostgresException exception) when (CheckViolation.Is(
            exception, WmsDbContext.StockBalanceNonNegativeCheck, WmsDbContext.StockBalanceReservedCheck))
        {
            throw TranslateCheck(exception);
        }
    }

    // El UPDATE toma el mismo bloqueo de fila que el upsert y evita chocar con el CHECK: el CHECK aborta la transacción y después ya no
    // se podría leer lo disponible que la pantalla necesita.
    private async Task DebitAsync(
        Guid tenantId,
        StockKey key,
        decimal delta,
        LedgerLocationInfo location,
        IReadOnlyDictionary<Guid, Lot> lotsById,
        CancellationToken ct)
    {
        var status = key.Status.ToString();
        var updated = await dbContext.Database.SqlQuery<BalanceRow>(
            $"""
            UPDATE wms.stock_balances
            SET on_hand = on_hand + {delta}, version = version + 1, updated_at = now()
            WHERE tenant_id = {tenantId} AND owner_id = {key.OwnerId} AND location_id = {key.LocationId} AND variant_id = {key.VariantId}
              AND lot_id IS NOT DISTINCT FROM {key.LotId}::uuid AND handling_unit_id IS NOT DISTINCT FROM {key.HandlingUnitId}::uuid
              AND status = {status}
              AND on_hand + {delta} >= reserved
            RETURNING on_hand AS "OnHand", reserved AS "Reserved"
            """)
            .ToListAsync(ct);
        if (updated.Count > 0)
        {
            return;
        }

        var current = await dbContext.Database.SqlQuery<BalanceRow>(
            $"""
            SELECT on_hand AS "OnHand", reserved AS "Reserved"
            FROM wms.stock_balances
            WHERE tenant_id = {tenantId} AND owner_id = {key.OwnerId} AND location_id = {key.LocationId} AND variant_id = {key.VariantId}
              AND lot_id IS NOT DISTINCT FROM {key.LotId}::uuid AND handling_unit_id IS NOT DISTINCT FROM {key.HandlingUnitId}::uuid
              AND status = {status}
            """)
            .ToListAsync(ct);
        var (onHand, reserved) = current.Count > 0 ? (current[0].OnHand, current[0].Reserved) : (0m, 0m);

        if (onHand + delta < 0)
        {
            var lotCode = key.LotId is { } lotId ? lotsById[lotId].Code : null;
            throw new StockInsufficientException(location.Code, key.VariantId, lotCode, onHand - reserved);
        }

        throw new WmsDomainException(WmsErrorCodes.StockReserved, ReservedStockMessage);
    }

    private async Task UpsertAsync(Guid tenantId, StockKey key, decimal delta, LedgerLocationInfo location, CancellationToken ct) =>
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO wms.stock_balances
                (tenant_id, owner_id, warehouse_id, location_id, variant_id, lot_id, handling_unit_id, status, on_hand, reserved, allows_negative,
                 version, updated_at)
            VALUES
                ({tenantId}, {key.OwnerId}, {location.WarehouseId}, {key.LocationId}, {key.VariantId}, {key.LotId}::uuid,
                 {key.HandlingUnitId}::uuid, {key.Status.ToString()}, {delta}, 0, {location.AllowsNegative}, 1, now())
            ON CONFLICT (tenant_id, owner_id, location_id, variant_id, lot_id, handling_unit_id, status)
            DO UPDATE SET on_hand = wms.stock_balances.on_hand + EXCLUDED.on_hand,
                          version = wms.stock_balances.version + 1,
                          updated_at = now()
            RETURNING on_hand, reserved
            """,
            ct);

    private static StockMovement ToMovement(LedgerBatch batch, LedgerEntry entry, DateTimeOffset recordedAt) =>
        StockMovement.Record(
            Guid.CreateVersion7(),
            batch.TenantId,
            entry.From,
            entry.To,
            entry.Quantity,
            entry.Captured?.Uom,
            entry.Captured?.Quantity,
            entry.Captured?.Factor,
            entry.Reason,
            entry.Document?.Type,
            entry.Document?.Id,
            entry.Document?.LineId,
            batch.BatchId,
            batch.IdempotencyKey,
            batch.Actor.Id,
            batch.Actor.Type,
            batch.Actor.DeviceId,
            entry.OccurredAt,
            recordedAt,
            entry.ReversesMovementId,
            entry.ReversesOccurredAt,
            entry.OverrideReason,
            entry.SerialUnitId);

    private static WmsDomainException TranslateCheck(PostgresException exception) =>
        CheckViolation.Is(exception, WmsDbContext.StockBalanceNonNegativeCheck)
            ? new WmsDomainException(WmsErrorCodes.StockInsufficient, NotEnoughStockMessage, exception)
            : new WmsDomainException(WmsErrorCodes.StockReserved, ReservedStockMessage, exception);

    // Fuera de la clase no existe: es la forma de una fila para SqlQuery, por eso internal y no private (EF la instancia).
    internal sealed record BalanceRow(decimal OnHand, decimal Reserved);
}
