using System.Net.Http.Json;
using Modules.Identity.Domain;
using Modules.Identity.Infrastructure.Persistence;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Modules.Wms.Application;
using Npgsql;
using Testcontainers.PostgreSql;
using Microsoft.Extensions.DependencyInjection;

namespace Modules.Wms.IntegrationTests;

/// <summary>Los helpers de inventario del plan WMS-D: bodegas, ubicaciones y SQL crudo sobre el ledger. Parte de
/// <see cref="WmsApiHarness"/>, que es <c>partial</c> para esto.</summary>
internal static partial class WmsApiHarness
{
    public static async Task<WarehouseDto> CreateWarehouseAsync(HttpClient manager, string code = "BOG", string tenantId = TenantId)
    {
        var response = await manager.PostAsJsonAsync(
            $"{WmsUrl(tenantId)}/warehouses", new { code, name = code }, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var warehouse = await response.Content.ReadFromJsonAsync<WarehouseDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(warehouse);
        return warehouse;
    }

    public static async Task<IReadOnlyList<LocationDto>> ListLocationsAsync(HttpClient client, Guid warehouseId, string tenantId = TenantId)
    {
        var locations = await client.GetFromJsonAsync<LocationsDto>(
            $"{WmsUrl(tenantId)}/warehouses/{warehouseId}/locations", TestContext.Current.CancellationToken);
        Assert.NotNull(locations);
        return locations.Items;
    }

    /// <summary>Crea la zona (<c>Zone</c>/<c>Storage</c>) si no existe y el bin dentro de ella: <c>Bin</c>/<c>Picking</c> si
    /// <paramref name="allowsPicking"/>, si no <c>Bin</c>/<c>Storage</c>.</summary>
    public static async Task<LocationDto> CreateBinAsync(
        HttpClient manager,
        Guid warehouseId,
        string zoneCode,
        string binCode,
        bool allowsPicking = true,
        string tenantId = TenantId)
    {
        var url = $"{WmsUrl(tenantId)}/warehouses/{warehouseId}/locations";
        var zone = (await ListLocationsAsync(manager, warehouseId, tenantId)).FirstOrDefault(value => value.Code == zoneCode);
        if (zone is null)
        {
            var zoneResponse = await manager.PostAsJsonAsync(
                url,
                new LocationRequest(null, zoneCode, "Zone", "Storage", false, false, null, null, null, 0, null),
                TestContext.Current.CancellationToken);
            zoneResponse.EnsureSuccessStatusCode();
            zone = await zoneResponse.Content.ReadFromJsonAsync<LocationDto>(TestContext.Current.CancellationToken);
            Assert.NotNull(zone);
        }

        var binResponse = await manager.PostAsJsonAsync(
            url,
            new LocationRequest(zone.Id, binCode, "Bin", allowsPicking ? "Picking" : "Storage", true, allowsPicking, null, null, null, 0, null),
            TestContext.Current.CancellationToken);
        binResponse.EnsureSuccessStatusCode();
        var bin = await binResponse.Content.ReadFromJsonAsync<LocationDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(bin);
        return bin;
    }

    public static async Task<T> ScalarAsync<T>(PostgreSqlContainer database, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = Command(connection, sql, parameters);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is null or DBNull ? default! : (T)value;
    }

    public static async Task<int> ExecuteAsync(PostgreSqlContainer database, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = Command(connection, sql, parameters);
        return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    /// <summary>Inserta una fila del ledger con todos los <c>NOT NULL</c> y devuelve su <c>id</c>. Para las pruebas del trigger y de
    /// las particiones, que no pasan por el ledger.</summary>
    public static async Task<Guid> InsertRawMovementAsync(PostgreSqlContainer database, DateTimeOffset occurredAt)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            database,
            """
            INSERT INTO wms.stock_movements
                (id, tenant_id, owner_id, variant_id, from_location_id, from_status, to_location_id, to_status, quantity, reason,
                 batch_id, idempotency_key, actor_id, actor_type, occurred_at, recorded_at)
            VALUES
                (@id, @tenant, @owner, @variant, @from, 'Available', @to, 'Available', 1, 'Adjustment',
                 @batch, @key, @actor, 'System', @occurredAt, now())
            """,
            ("id", id),
            ("tenant", Guid.Parse(TenantId)),
            ("owner", Guid.CreateVersion7()),
            ("variant", Guid.CreateVersion7()),
            ("from", Guid.CreateVersion7()),
            ("to", Guid.CreateVersion7()),
            ("batch", Guid.CreateVersion7()),
            ("key", id.ToString()),
            ("actor", Guid.CreateVersion7()),
            ("occurredAt", occurredAt));
        return id;
    }

    /// <summary>La membresía activa de quien llama: copia de <c>LoansApiHarness.SeedCallerMembershipAsync</c>, que WMS-B no trajo.</summary>
    public static async Task<Guid> SeedCallerMembershipAsync(QepApiFactory factory, string tenantId = TenantId, string subjectId = SubjectId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;

        var user = User.CreateInvited(UserId.Parse(subjectId), $"caller-{tenantId[^2..]}@example.com", now);
        identity.Users.Add(user);
        var membership = Membership.CreateActive(
            MembershipId.New(), user.Id.Value, new Modules.Tenancy.Domain.TenantId(Guid.Parse(tenantId)), ["admin"], "test", now);
        membership.UpdateProfile("Caller", null, now);
        tenancy.Memberships.Add(membership);
        await identity.SaveChangesAsync(ct);
        await tenancy.SaveChangesAsync(ct);
        return membership.Id.Value;
    }
}
