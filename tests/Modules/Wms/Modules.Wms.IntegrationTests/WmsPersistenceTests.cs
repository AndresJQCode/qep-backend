using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Wms.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;
using Modules.Wms.Infrastructure.Persistence;
using Npgsql;
using static Modules.Wms.IntegrationTests.WmsApiHarness;

namespace Modules.Wms.IntegrationTests;

/// <summary>
/// La persistencia de Bodegas contra PostgreSQL real (spec WMS 2026-10-31 §11.2): la migración <c>InitialWms</c>, el provisioner del
/// Self y las seis unidades, el recorder atómico con <c>changed_fields</c> como objeto y la traducción por nombre de índice de
/// <c>WmsUnitOfWork</c>.
/// </summary>
public sealed class WmsPersistenceTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<string>> QueryStringsAsync(QepApiFactory factory, string sql)
    {
        var connectionString = factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var values = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static Warehouse NewWarehouse(string code) =>
        Warehouse.Create(WarehouseId.New(), Tenant, code, "Bodega " + code, null, DateTimeOffset.UtcNow);

    [Fact]
    public async Task TheMigrationCreatesTheEightTablesWithTheirUniqueIndexes()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        // Las tablas del ledger (AddInventoryLedger) las cubre InventoryLedgerMigrationTests y las de tareas (AddWarehouseTasks),
        // WarehouseTaskPersistenceTests; aquí sólo las ocho de InitialWms.
        var tables = await QueryStringsAsync(
            factory,
            """
            SELECT tablename FROM pg_tables
             WHERE schemaname = 'wms' AND tablename <> '__ef_migrations_history'
               AND tablename NOT LIKE 'stock_movements%'
               AND tablename NOT IN ('stock_balances', 'lots', 'serial_units', 'reservations', 'operation_receipts', 'balance_checks',
                                     'import_previews', 'adjustments', 'adjustment_lines', 'balance_change_queue', 'tasks', 'task_lines',
                                     'task_events')
             ORDER BY tablename
            """);
        var indexes = await QueryStringsAsync(factory, "SELECT indexname FROM pg_indexes WHERE schemaname = 'wms'");

        Assert.Equal(
            ["identifiers", "locations", "member_scopes", "owners", "units_of_measure", "uom_conversions", "variant_profiles", "warehouses"],
            tables);
        foreach (var expected in new[]
        {
            "IX_warehouses_tenant_code", "IX_locations_warehouse_code", "IX_locations_warehouse_virtual_role", "IX_owners_tenant_self",
            "IX_uom_conversions_variant_code", "IX_identifiers_tenant_value", "PK_units_of_measure", "IX_member_scopes_tenant_member",
        })
        {
            Assert.Contains(expected, indexes);
        }

        // Las FKs internas son RESTRICT ('r'), no CASCADE: spec §11.2.
        Assert.Equal(
            "r",
            Assert.Single(await QueryStringsAsync(
                factory, "SELECT confdeltype::text FROM pg_constraint WHERE conname = 'FK_uom_conversions_profile'")));

        // member_scopes nace vacía: nadie la escribe en este corte (S18).
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<WmsDbContext>().MemberScopes.ToListAsync(Ct));
    }

    [Fact]
    public async Task TheProvisionerCreatesOneSelfOwnerAndSixUnitsAndIsIdempotent()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await using var scope = factory.Services.CreateAsyncScope();
        var provisioner = scope.ServiceProvider.GetRequiredService<IWmsProvisioner>();

        await provisioner.EnsureAsync(Tenant, Ct);
        await provisioner.EnsureAsync(Tenant, Ct);

        var db = scope.ServiceProvider.GetRequiredService<WmsDbContext>();
        var owners = await db.Owners.AsNoTracking().Where(owner => owner.TenantId == Tenant).ToListAsync(Ct);
        var uoms = await db.UnitsOfMeasure.AsNoTracking().Where(uom => uom.TenantId == Tenant).ToListAsync(Ct);
        Assert.Equal(OwnerKind.Self, Assert.Single(owners).Kind);
        Assert.Equal("Propio", owners[0].Name);
        Assert.Equal(6, uoms.Count);
        Assert.All(uoms, uom => Assert.True(uom.IsSystem));
    }

    [Fact]
    public async Task GetSelfAsyncThrowsWhenTheTenantWasNeverProvisioned()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await using var scope = factory.Services.CreateAsyncScope();

        // El repositorio no escribe (S5): quien llama tuvo que provisionar antes.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<IOwnerRepository>().GetSelfAsync(Tenant, Ct));
    }

    [Fact]
    public async Task TheRecorderWritesTheChangedFieldsAsAJsonObject()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var resourceId = Guid.CreateVersion7().ToString();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IWmsAuditRecorder>().Record(
                Tenant,
                Guid.CreateVersion7(),
                WmsActorType.Human,
                WmsAuditActions.WarehouseCreated,
                WmsAuditActions.Resources.Warehouse,
                resourceId,
                new Dictionary<string, string?> { ["code"] = "BOG" },
                DateTimeOffset.UtcNow);
            await scope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>().SaveChangesAsync(Ct);
        }

        // S23: un objeto, no el arreglo "[]" por defecto.
        var values = await QueryStringsAsync(
            factory,
            $"SELECT changed_fields->>'code' || '|' || jsonb_typeof(changed_fields) FROM audit.entries WHERE resource_id = '{resourceId}'");
        Assert.Equal("BOG|object", Assert.Single(values));
        Assert.Equal(1, await CountAuditEntriesAsync(factory, WmsAuditActions.WarehouseCreated, resourceId));
    }

    [Fact]
    public async Task DeletingAParentLocationIsTranslatedToHasChildren()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IWarehouseRepository>().Add(NewWarehouse("BOG"));
            await scope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using var removing = factory.Services.CreateAsyncScope();
        var db = removing.ServiceProvider.GetRequiredService<WmsDbContext>();
        var receiving = await db.Locations.SingleAsync(location => location.Code == LocationCodes.ReceivingZone, Ct);
        db.Locations.Remove(receiving);

        // REC-01 sigue vivo: borrar el padre de una FK RESTRICT da 23001, no 23503.
        var exception = await Assert.ThrowsAsync<WmsDomainException>(
            () => removing.ServiceProvider.GetRequiredService<IWmsUnitOfWork>().SaveChangesAsync(Ct));
        Assert.Equal(WmsErrorCodes.LocationHasChildren, exception.Code);
    }

    [Fact]
    public async Task ASecondSelfOwnerIsTranslatedToSelfExists()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWmsProvisioner>().EnsureAsync(Tenant, Ct);

        scope.ServiceProvider.GetRequiredService<IOwnerRepository>()
            .Add(Owner.CreateSelf(OwnerId.New(), Tenant, "Otro propio", DateTimeOffset.UtcNow));

        var exception = await Assert.ThrowsAsync<WmsDomainException>(
            () => scope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>().SaveChangesAsync(Ct));
        Assert.Equal(WmsErrorCodes.OwnerSelfExists, exception.Code);
    }

    [Fact]
    public async Task ADuplicateWarehouseCodeIsTranslatedToCodeTaken()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        await using (var first = factory.Services.CreateAsyncScope())
        {
            first.ServiceProvider.GetRequiredService<IWarehouseRepository>().Add(NewWarehouse("BOG"));
            await first.ServiceProvider.GetRequiredService<IWmsUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using var second = factory.Services.CreateAsyncScope();
        second.ServiceProvider.GetRequiredService<IWarehouseRepository>().Add(NewWarehouse("BOG"));

        var exception = await Assert.ThrowsAsync<WmsDomainException>(
            () => second.ServiceProvider.GetRequiredService<IWmsUnitOfWork>().SaveChangesAsync(Ct));
        Assert.Equal(WmsErrorCodes.WarehouseCodeTaken, exception.Code);
    }

    [Fact]
    public async Task AStaleVersionIsTranslatedToConcurrencyConflict()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var warehouse = NewWarehouse("BOG");
        await using (var seed = factory.Services.CreateAsyncScope())
        {
            seed.ServiceProvider.GetRequiredService<IWarehouseRepository>().Add(warehouse);
            await seed.ServiceProvider.GetRequiredService<IWmsUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using var stale = factory.Services.CreateAsyncScope();
        var staleCopy = await stale.ServiceProvider.GetRequiredService<IWarehouseRepository>().GetAsync(Tenant, warehouse.Id, Ct);
        Assert.NotNull(staleCopy);

        await using (var winner = factory.Services.CreateAsyncScope())
        {
            var copy = await winner.ServiceProvider.GetRequiredService<IWarehouseRepository>().GetAsync(Tenant, warehouse.Id, Ct);
            copy!.Update("Primero", null, true, DateTimeOffset.UtcNow);
            await winner.ServiceProvider.GetRequiredService<IWmsUnitOfWork>().SaveChangesAsync(Ct);
        }

        staleCopy.Update("Segundo", null, true, DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<RequestConcurrencyException>(
            () => stale.ServiceProvider.GetRequiredService<IWmsUnitOfWork>().SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task UpdatingATrackedProfileKeepsChangesAndDropsConversionsInPlace()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var variantId = await CreateVariantAsync(factory, "SKU-2", null, [6, 12]);
        await using (var seed = factory.Services.CreateAsyncScope())
        {
            await seed.ServiceProvider.GetRequiredService<IVariantProfileRepository>().GetOrCreateDefaultAsync(Tenant, variantId, Ct);
            await seed.ServiceProvider.GetRequiredService<IWmsUnitOfWork>().SaveChangesAsync(Ct);
        }

        Guid keptId;
        await using (var edit = factory.Services.CreateAsyncScope())
        {
            var profile = (await edit.ServiceProvider.GetRequiredService<IVariantProfileRepository>().FindAsync(Tenant, variantId, Ct))!;
            Assert.Equal(2, profile.Conversions.Count);
            keptId = profile.Conversions.Single(c => c.Code == "EMPAQUE x6").Id;

            // Conserva «EMPAQUE x6» con otro factor, deja caer «EMPAQUE x12» y agrega «CAJA».
            profile.Update(
                new ProfileChanges(
                    UomCode.Parse("UND"), 0, TrackingPolicy.None, false, null, null, null, null, null, null, null,
                    [("EMPAQUE x6", 7m, "BAR-6"), ("CAJA", 24m, null)]),
                hasStock: false,
                DateTimeOffset.UtcNow);
            await edit.ServiceProvider.GetRequiredService<IWmsUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var stored = (await read.ServiceProvider.GetRequiredService<IVariantProfileRepository>().FindAsync(Tenant, variantId, Ct))!;
        Assert.Equal(2, stored.Conversions.Count);
        var kept = stored.Conversions.Single(c => c.Code == "EMPAQUE x6");
        Assert.Equal(keptId, kept.Id);
        Assert.Equal(7m, kept.Factor);
        Assert.Equal("BAR-6", kept.Barcode);
        Assert.Contains(stored.Conversions, c => c.Code == "CAJA" && c.Factor == 24m);
        Assert.DoesNotContain(stored.Conversions, c => c.Code == "EMPAQUE x12");
    }

    [Fact]
    public async Task GetOrCreateDefaultAsyncCreatesTheProfileTheConversionsAndTheIdentifiers()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var variantId = await CreateVariantAsync(factory, "SKU-1", "7701234567890", [12]);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var profile = await scope.ServiceProvider.GetRequiredService<IVariantProfileRepository>()
                .GetOrCreateDefaultAsync(Tenant, variantId, Ct);
            await scope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>().SaveChangesAsync(Ct);
            Assert.Equal(variantId, profile.VariantId);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var stored = await read.ServiceProvider.GetRequiredService<IVariantProfileRepository>().FindAsync(Tenant, variantId, Ct);
        Assert.NotNull(stored);
        Assert.Equal("UND", stored.BaseUom.Value);
        Assert.Equal(0, stored.BaseUomPrecision);
        Assert.Equal(TrackingPolicy.None, stored.Tracking);
        var conversion = Assert.Single(stored.Conversions);
        Assert.Equal("EMPAQUE x12", conversion.Code);
        Assert.Equal(12m, conversion.Factor);

        var identifiers = await read.ServiceProvider.GetRequiredService<IIdentifierRepository>().ListByTargetAsync(Tenant, variantId, Ct);
        Assert.Equal(2, identifiers.Count);
        var gtin = Assert.Single(identifiers, identifier => identifier.Symbology == IdentifierSymbology.Gtin);
        Assert.Equal("7701234567890", gtin.Value);
        Assert.True(gtin.IsPrimary);
        Assert.Contains(identifiers, identifier => identifier.Symbology == IdentifierSymbology.Internal && identifier.Value == "SKU-1");

        // La auditoría del perfil nace como System (sin persona detrás) y en la misma transacción que el perfil.
        var actors = await QueryStringsAsync(
            factory,
            $"SELECT actor_type FROM audit.entries WHERE action = '{WmsAuditActions.ProfileCreated}' AND resource_id = '{variantId}'");
        Assert.Equal("System", Assert.Single(actors));
    }

    [Fact]
    public async Task TwoConcurrentDefaultCreationsOfTheSameProfileLoseWithAConcurrencyConflictNotAServerError()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var variantId = await CreateVariantAsync(factory, "SKU-1", "7701234567890", [12]);

        // Las dos miran antes de que cualquiera guarde: ninguna ve la fila y las dos arman el perfil por defecto.
        await using var first = factory.Services.CreateAsyncScope();
        await using var second = factory.Services.CreateAsyncScope();
        await first.ServiceProvider.GetRequiredService<IVariantProfileRepository>().GetOrCreateDefaultAsync(Tenant, variantId, Ct);
        await second.ServiceProvider.GetRequiredService<IVariantProfileRepository>().GetOrCreateDefaultAsync(Tenant, variantId, Ct);

        await first.ServiceProvider.GetRequiredService<IWmsUnitOfWork>().SaveChangesAsync(Ct);

        // La perdedora no pasa a 500: PK_variant_profiles se traduce a 412, que es lo que le dice a quien llama «recarga, ya existe».
        await Assert.ThrowsAsync<RequestConcurrencyException>(
            () => second.ServiceProvider.GetRequiredService<IWmsUnitOfWork>().SaveChangesAsync(Ct));

        await using var read = factory.Services.CreateAsyncScope();
        var stored = await read.ServiceProvider.GetRequiredService<IVariantProfileRepository>().FindAsync(Tenant, variantId, Ct);
        Assert.NotNull(stored);
        Assert.Equal(1, stored.Version);
    }
}
