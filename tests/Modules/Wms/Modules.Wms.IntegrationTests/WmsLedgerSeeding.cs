using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Application;
using Modules.Wms.Domain.Inventory;
using Testcontainers.PostgreSql;
using static Modules.Wms.IntegrationTests.WmsApiHarness;

namespace Modules.Wms.IntegrationTests;

internal sealed record LedgerSetup(
    Guid TenantId,
    HttpClient Manager,
    Guid WarehouseId,
    Guid OwnerId,
    Guid Supplier,
    Guid Adjustment,
    Guid Transit,
    Guid Receiving,
    Guid BinA,
    Guid VariantId);

/// <summary>Lo que necesita toda prueba que escribe al ledger: una bodega con sus virtuales, un bin, una variante y el dueño Self. El
/// saldo se siembra posteando, no por SQL, salvo donde el brief dice lo contrario.</summary>
internal static class WmsLedgerSeeding
{
    public static async Task<LedgerSetup> ArrangeAsync(PostgreSqlContainer database, QepApiFactory factory)
    {
        var manager = CreateManager(factory);
        await SeedCallerMembershipAsync(factory);
        var warehouse = await CreateWarehouseAsync(manager, "BOG");
        var locations = await ListLocationsAsync(manager, warehouse.Id);
        var bin = await CreateBinAsync(manager, warehouse.Id, "A", "A-01-02");
        var variantId = await CreateVariantAsync(factory, "V1", null, [12]);

        // Listar dueños provisiona el Self si nadie lo hizo todavía.
        var owners = await manager.GetAsync($"{WmsUrl()}/owners", TestContext.Current.CancellationToken);
        owners.EnsureSuccessStatusCode();
        var tenantId = Guid.Parse(TenantId);
        var ownerId = await ScalarAsync<Guid>(
            database, "SELECT id FROM wms.owners WHERE tenant_id = @t AND kind = 'Self'", ("t", tenantId));

        Guid LocationCoded(string code) => locations.Single(location => location.Code == code).Id;

        return new LedgerSetup(
            tenantId,
            manager,
            warehouse.Id,
            ownerId,
            LocationCoded("SUPPLIER"),
            LocationCoded("ADJUSTMENT"),
            LocationCoded("TRANSIT"),
            LocationCoded("REC-01"),
            bin.Id,
            variantId);
    }

    public static async Task<LedgerPosting> PostAsync(QepApiFactory factory, LedgerBatch batch)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>();
        var ledger = scope.ServiceProvider.GetRequiredService<IInventoryLedger>();

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        var posting = await ledger.PostAsync(batch, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return posting;
    }

    public static LedgerEntry Entry(
        LedgerSetup s,
        Guid from,
        StockStatus fromStatus,
        Guid to,
        StockStatus toStatus,
        decimal qty,
        MovementReason reason = MovementReason.Adjustment,
        Guid? lot = null,
        Guid? variant = null,
        DateTimeOffset? occurredAt = null,
        Guid? serial = null)
    {
        var variantId = variant ?? s.VariantId;
        return new LedgerEntry(
            new StockKey(s.OwnerId, from, variantId, lot, fromStatus),
            new StockKey(s.OwnerId, to, variantId, lot, toStatus),
            qty,
            null,
            reason,
            null,
            occurredAt ?? DateTimeOffset.UtcNow,
            serialUnitId: serial);
    }

    public static LedgerBatch Batch(LedgerSetup s, params LedgerEntry[] entries) =>
        new(
            s.TenantId,
            Guid.CreateVersion7(),
            Guid.NewGuid().ToString(),
            new LedgerActor(Guid.Parse(SubjectId), LedgerActorType.Human, null),
            entries);

    /// <summary>Deja <paramref name="qty"/> disponibles en la ubicación, traídos del proveedor.</summary>
    public static Task<LedgerPosting> StockAsync(QepApiFactory factory, LedgerSetup s, Guid location, decimal qty, Guid? lot = null, Guid? variant = null) =>
        PostAsync(factory, Batch(s, Entry(s, s.Supplier, StockStatus.Available, location, StockStatus.Available, qty, lot: lot, variant: variant)));

    public static async Task<decimal> OnHandAsync(
        PostgreSqlContainer database, Guid locationId, Guid variantId, StockStatus status, Guid? lotId = null) =>
        await ScalarAsync<decimal>(
            database,
            """
            SELECT coalesce(sum(on_hand), 0)
            FROM wms.stock_balances
            WHERE location_id = @location AND variant_id = @variant AND status = @status AND lot_id IS NOT DISTINCT FROM @lot::uuid
            """,
            ("location", locationId),
            ("variant", variantId),
            ("status", status.ToString()),
            ("lot", lotId));

    /// <summary>El saldo de una clave exacta, unidad logística incluida (<c>null</c> es suelto, y no suma lo que está en unidades).</summary>
    public static async Task<decimal> OnHandAsync(PostgreSqlContainer database, StockKey key) =>
        await ScalarAsync<decimal>(
            database,
            """
            SELECT coalesce(sum(on_hand), 0)
            FROM wms.stock_balances
            WHERE owner_id = @owner AND location_id = @location AND variant_id = @variant AND status = @status
              AND lot_id IS NOT DISTINCT FROM @lot::uuid AND handling_unit_id IS NOT DISTINCT FROM @unit::uuid
            """,
            ("owner", key.OwnerId),
            ("location", key.LocationId),
            ("variant", key.VariantId),
            ("status", key.Status.ToString()),
            ("lot", key.LotId),
            ("unit", key.HandlingUnitId));

    /// <summary>Suma <paramref name="delta"/> al saldo de la clave por SQL, como lo haría el upsert del ledger. Sirve para sembrar saldos
    /// dentro de una unidad logística, que el ledger de WMS-D todavía no escribe (D-12). Primero el UPDATE y sólo si no hay fila el
    /// INSERT, como el débito del ledger: un <c>ON CONFLICT</c> con delta negativo chocaría con el CHECK antes de ver el conflicto.</summary>
    public static Task SeedBalanceAsync(PostgreSqlContainer database, LedgerSetup s, StockKey key, decimal delta) =>
        ExecuteAsync(
            database,
            """
            WITH updated AS (
                UPDATE wms.stock_balances
                SET on_hand = on_hand + @delta, version = version + 1
                WHERE tenant_id = @tenant AND owner_id = @owner AND location_id = @location AND variant_id = @variant
                  AND lot_id IS NOT DISTINCT FROM @lot::uuid AND handling_unit_id IS NOT DISTINCT FROM @unit::uuid AND status = @status
                RETURNING 1)
            INSERT INTO wms.stock_balances
                (tenant_id, owner_id, warehouse_id, location_id, variant_id, lot_id, handling_unit_id, status, on_hand, reserved,
                 allows_negative, version, updated_at)
            SELECT @tenant, @owner, l.warehouse_id, l.id, @variant, @lot::uuid, @unit::uuid, @status, @delta, 0,
                   l.role IN ('Supplier', 'Customer', 'Adjustment'), 1, now()
            FROM wms.locations l
            WHERE l.id = @location AND NOT EXISTS (SELECT 1 FROM updated)
            """,
            ("tenant", s.TenantId),
            ("owner", key.OwnerId),
            ("variant", key.VariantId),
            ("lot", key.LotId),
            ("unit", key.HandlingUnitId),
            ("status", key.Status.ToString()),
            ("delta", delta),
            ("location", key.LocationId));

    /// <summary>Un movimiento con unidad logística sembrado por SQL, con sus dos saldos. Mientras no exista el agregado
    /// <c>HandlingUnit</c> el ledger rechaza unidades fuera de un reverso, así que es la única forma de tener uno.</summary>
    public static async Task<Guid> SeedUnitMovementAsync(
        PostgreSqlContainer database, LedgerSetup s, StockKey from, StockKey to, decimal qty, MovementReason reason = MovementReason.Putaway)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            database,
            """
            INSERT INTO wms.stock_movements
                (id, tenant_id, owner_id, variant_id, lot_id, from_location_id, from_status, from_handling_unit_id,
                 to_location_id, to_status, to_handling_unit_id, quantity, reason, batch_id, idempotency_key, actor_id, actor_type,
                 occurred_at, recorded_at)
            VALUES
                (@id, @tenant, @owner, @variant, @lot::uuid, @fromLocation, @fromStatus, @fromUnit::uuid,
                 @toLocation, @toStatus, @toUnit::uuid, @qty, @reason, @batch, @key, @actor, 'Human', now(), now())
            """,
            ("id", id),
            ("tenant", s.TenantId),
            ("owner", from.OwnerId),
            ("variant", from.VariantId),
            ("lot", from.LotId),
            ("fromLocation", from.LocationId),
            ("fromStatus", from.Status.ToString()),
            ("fromUnit", from.HandlingUnitId),
            ("toLocation", to.LocationId),
            ("toStatus", to.Status.ToString()),
            ("toUnit", to.HandlingUnitId),
            ("qty", qty),
            ("reason", reason.ToString()),
            ("batch", Guid.CreateVersion7()),
            ("key", Guid.NewGuid().ToString()),
            ("actor", Guid.Parse(SubjectId)));
        await SeedBalanceAsync(database, s, from, -qty);
        await SeedBalanceAsync(database, s, to, qty);
        return id;
    }

    /// <summary>Por el <c>PUT</c> de WMS-B, con la versión 0 de un perfil todavía sin guardar.</summary>
    public static async Task SetTrackingAsync(HttpClient manager, Guid variantId, string tracking, bool tracksExpiry = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{WmsUrl()}/variant-profiles/{variantId}")
        {
            Content = JsonContent.Create(new
            {
                baseUom = "UND",
                baseUomPrecision = 0,
                tracking,
                tracksExpiry,
                weightKg = 1.5m,
                volumeM3 = (decimal?)null,
                lengthCm = (decimal?)null,
                widthCm = (decimal?)null,
                heightCm = (decimal?)null,
                storageClass = "FRIO",
                displayUom = (string?)null,
                conversions = Array.Empty<object>(),
                version = 0L,
            }),
        };
        request.Headers.Add("X-Qep-Client", "web");
        var response = await manager.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public static async Task<Guid> CreateLotAsync(QepApiFactory factory, LedgerSetup s, Guid variantId, string code)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var lot = Lot.Create(s.TenantId, s.OwnerId, variantId, code, null, null, false, DateTimeOffset.UtcNow);
        scope.ServiceProvider.GetRequiredService<ILotRepository>().Add(lot);
        await scope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>().SaveChangesAsync(ct);
        return lot.Id.Value;
    }

    /// <summary>El perfil por defecto de otra petición, con su identificador, tal como lo dejaría al commitear primero.</summary>
    public static Task InsertCompetingProfileAsync(PostgreSqlContainer database, Guid variantId, string identifier) =>
        ExecuteAsync(
            database,
            """
            INSERT INTO wms.variant_profiles
                (tenant_id, variant_id, product_id, base_uom, base_uom_precision, tracking, tracks_expiry, version, created_at, updated_at)
            VALUES (@tenant, @variant, @product, 'UND', 0, 'None', false, 0, now(), now());
            INSERT INTO wms.identifiers (id, tenant_id, kind, symbology, value, target_id, is_primary, created_at)
            VALUES (@id, @tenant, 'Variant', 'Internal', @value, @variant, false, now());
            """,
            ("tenant", Guid.Parse(TenantId)),
            ("variant", variantId),
            ("product", Guid.CreateVersion7()),
            ("id", Guid.CreateVersion7()),
            ("value", identifier));
}
