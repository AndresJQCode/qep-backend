using Npgsql;
using static Modules.Wms.IntegrationTests.WmsApiHarness;
using static Modules.Wms.IntegrationTests.WmsLedgerSeeding;

namespace Modules.Wms.IntegrationTests;

/// <summary>La migración <c>AddBasicInventory</c> (spec inventario básico §5.3, §5.4 y §8): una principal por tenant, reservas de
/// demanda sin ubicación y sus índices parciales.</summary>
public sealed class BasicInventoryMigrationTests
{
    private const string InsertReservation = """
        INSERT INTO wms.reservations
            (id, tenant_id, owner_id, variant_id, warehouse_id, location_id, quantity, demand_type, demand_id, demand_line_id, status, created_at)
        VALUES
            (@id, @tenant, @owner, @variant, @warehouse, @location, 2, 'Order', @demand, @line, @status, now())
        """;

    [Fact]
    public async Task TheMigrationAllowsOnePrincipalPerTenant()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var manager = CreateManager(factory);
        var first = await CreateWarehouseAsync(manager, "BOG");
        var second = await CreateWarehouseAsync(manager, "MED");

        await ExecuteAsync(database, "UPDATE wms.warehouses SET is_principal = true WHERE id = @id", ("id", first.Id));
        var violation = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(database, "UPDATE wms.warehouses SET is_principal = true WHERE id = @id", ("id", second.Id)));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, violation.SqlState);
        Assert.Equal("IX_warehouses_tenant_principal", violation.ConstraintName);
        // Las que no son principal no cuentan: el índice es parcial.
        Assert.Equal(1L, await ScalarAsync<long>(
            database, "SELECT count(*) FROM wms.warehouses WHERE tenant_id = @t AND NOT is_principal", ("t", Guid.Parse(TenantId))));
        Assert.Equal(1L, await ScalarAsync<long>(
            database, "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'wms' AND table_name = 'warehouses' AND column_name = 'general_location_id'"));
    }

    [Fact]
    public async Task TwoOpenDemandReservationsForTheSameLineAreRejected()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var demand = Guid.CreateVersion7();
        var line = Guid.CreateVersion7();

        await InsertDemandAsync(database, s, demand, line, "Open");
        var violation = await Assert.ThrowsAsync<PostgresException>(() => InsertDemandAsync(database, s, demand, line, "Open"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, violation.SqlState);
        Assert.Equal("IX_reservations_open_demand", violation.ConstraintName);
        // Cerrada o consumida no estorba: sólo una abierta por línea; y otra línea del mismo pedido tampoco choca.
        await InsertDemandAsync(database, s, demand, line, "Released");
        await InsertDemandAsync(database, s, demand, line, "Consumed");
        await InsertDemandAsync(database, s, demand, Guid.CreateVersion7(), "Open");
    }

    [Fact]
    public async Task TwoAllocationsForTheSameLineInDifferentLocationsAreAllowed()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var s = await ArrangeAsync(database, factory);
        var demand = Guid.CreateVersion7();
        var line = Guid.CreateVersion7();

        // T5 asigna una misma línea desde dos ubicaciones: la ubicación vuelve a la reserva distinta de la de demanda.
        await InsertReservationAsync(database, s, s.BinA, demand, line, "Open");
        await InsertReservationAsync(database, s, s.Receiving, demand, line, "Open");
        // Y la de demanda (sin ubicación) convive con ellas.
        await InsertReservationAsync(database, s, null, demand, line, "Open");

        Assert.Equal(3L, await ScalarAsync<long>(
            database, "SELECT count(*) FROM wms.reservations WHERE demand_id = @d AND status = 'Open'", ("d", demand)));
        Assert.Equal(1L, await ScalarAsync<long>(
            database, "SELECT count(*) FROM wms.reservations WHERE demand_id = @d AND location_id IS NULL", ("d", demand)));
    }

    private static Task<int> InsertDemandAsync(
        Testcontainers.PostgreSql.PostgreSqlContainer database, LedgerSetup s, Guid demand, Guid line, string status) =>
        InsertReservationAsync(database, s, null, demand, line, status);

    private static Task<int> InsertReservationAsync(
        Testcontainers.PostgreSql.PostgreSqlContainer database, LedgerSetup s, Guid? location, Guid demand, Guid line, string status) =>
        ExecuteAsync(
            database,
            InsertReservation,
            ("id", Guid.CreateVersion7()),
            ("tenant", s.TenantId),
            ("owner", s.OwnerId),
            ("variant", s.VariantId),
            ("warehouse", s.WarehouseId),
            ("location", location),
            ("demand", demand),
            ("line", line),
            ("status", status));
}
