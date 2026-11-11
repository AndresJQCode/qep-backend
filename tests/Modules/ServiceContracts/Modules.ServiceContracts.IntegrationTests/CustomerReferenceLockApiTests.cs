using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Infrastructure;
using Modules.ServiceContracts.Domain;
using Npgsql;
using static Modules.ServiceContracts.IntegrationTests.ServiceContractsApiHarness;

namespace Modules.ServiceContracts.IntegrationTests;

/// <summary>
/// Borrar clientes (P1/P2): quien escribe una referencia a un cliente toma el candado compartido y comprueba que el
/// cliente exista dentro de su transacción. Aquí el borrado se simula con el candado exclusivo tomado a mano: la
/// escritura tiene que esperar, y al despertar ver el cliente ya borrado.
/// </summary>
public sealed class CustomerReferenceLockApiTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);
    private static readonly TimeSpan NoAnswerWindow = TimeSpan.FromMilliseconds(500);

    private static async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> HoldExclusiveAsync(
        string connectionString, Guid tenantId, Guid customerId)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var key = CustomerReferenceLock.KeyFor(tenantId, customerId);
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", connection, transaction);
        command.Parameters.AddWithValue("key", key);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return (connection, transaction);
    }

    private static async Task DeleteCustomerAndCommitAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid customerId)
    {
        await using (var delete = new NpgsqlCommand(
            "DELETE FROM customers.customers WHERE id = @id", connection, transaction))
        {
            delete.Parameters.AddWithValue("id", customerId);
            await delete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await transaction.CommitAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> CountAsync(string connectionString, string sql, Guid customerId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", customerId);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<string> CodeOfAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return problem.GetProperty("code").GetString()!;
    }

    private static object ServiceBody(Guid customerId, Guid assigneeMemberId) => new
    {
        customerId,
        name = "Hosting",
        price = 150000m,
        currency = (string?)null,
        cycle = "Monthly",
        firstDueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5)).AddMonths(2)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        profile = new { channel = "WhatsApp", assigneeMemberId },
        description = (string?)null,
    };

    [Fact]
    public async Task CreatingAServiceWaitsForTheDeletionAndThenFails()
    {
        var (database, factory) = await OpenAsync();
        await using var _d = database;
        using var _f = factory;
        var connectionString = database.GetConnectionString();
        using var manager = CreateManager(factory);
        var customerId = await SeedCustomerAsync(factory, Tenant, "Panaderia La Esquina", "+573001234567", "pan@example.com");
        var admin = await SeedMemberAsync(factory, $"ana-{Guid.NewGuid():N}@example.com", "Ana Admin", "admin");
        // Calentamiento: la primera petición de una fábrica nueva tarda más que la ventana de espera, y sin él la prueba
        // pasaría aunque el candado no existiera (la escritura "esperaría" por arranque en frío, no por el candado).
        (await manager.GetAsync($"{ServiceContractsUrl()}/services", TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var (connection, transaction) = await HoldExclusiveAsync(connectionString, Tenant, customerId);
        await using var _c = connection;
        await using var _t = transaction;

        var post = manager.PostAsJsonAsync(
            $"{ServiceContractsUrl()}/services", ServiceBody(customerId, admin), TestContext.Current.CancellationToken);

        Assert.NotSame(post, await Task.WhenAny(post, Task.Delay(NoAnswerWindow, TestContext.Current.CancellationToken)));
        await DeleteCustomerAndCommitAsync(connection, transaction, customerId);

        var response = await post;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ServiceContractsErrorCodes.CustomerNotFound, await CodeOfAsync(response));
        Assert.Equal(0, await CountAsync(
            connectionString, "SELECT count(*) FROM billing.services WHERE customer_id = @id", customerId));
        Assert.Equal(0, await CountAsync(
            connectionString, "SELECT count(*) FROM billing.customer_profiles WHERE customer_id = @id", customerId));
    }

    // Final review R7: PUT /customers/{id}/profile crea un perfil sin servicio, y el perfil también es una referencia al cliente
    // (el borrado lo borra, §4.3). Crearlo mientras el borrado tiene el candado espera y, al despertar, ve el cliente borrado.
    [Fact]
    public async Task CreatingAProfileWaitsForTheDeletionAndThenFails()
    {
        var (database, factory) = await OpenAsync();
        await using var _d = database;
        using var _f = factory;
        var connectionString = database.GetConnectionString();
        using var manager = CreateManager(factory);
        var customerId = await SeedCustomerAsync(factory, Tenant, "Panaderia La Esquina", "+573001234567", "pan@example.com");
        var admin = await SeedMemberAsync(factory, $"ana-{Guid.NewGuid():N}@example.com", "Ana Admin", "admin");
        // Calentamiento, como en la prueba del servicio: sin él la espera podría ser arranque en frío y no el candado.
        (await manager.GetAsync($"{ServiceContractsUrl()}/services", TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var (connection, transaction) = await HoldExclusiveAsync(connectionString, Tenant, customerId);
        await using var _c = connection;
        await using var _t = transaction;

        var put = manager.PutAsJsonAsync(
            $"{ServiceContractsUrl()}/customers/{customerId}/profile",
            new { channel = "WhatsApp", assigneeMemberId = admin },
            TestContext.Current.CancellationToken);

        Assert.NotSame(put, await Task.WhenAny(put, Task.Delay(NoAnswerWindow, TestContext.Current.CancellationToken)));
        await DeleteCustomerAndCommitAsync(connection, transaction, customerId);

        var response = await put;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ServiceContractsErrorCodes.CustomerNotFound, await CodeOfAsync(response));
        Assert.Equal(0, await CountAsync(
            connectionString, "SELECT count(*) FROM billing.customer_profiles WHERE customer_id = @id", customerId));
    }

    [Fact]
    public async Task WithoutADeletionNothingChanges()
    {
        var (database, factory) = await OpenAsync();
        await using var _d = database;
        using var _f = factory;
        using var manager = CreateManager(factory);
        var customerId = await SeedCustomerAsync(factory, Tenant, "Panaderia La Esquina", "+573001234567", "pan@example.com");
        var admin = await SeedMemberAsync(factory, $"ana-{Guid.NewGuid():N}@example.com", "Ana Admin", "admin");

        var response = await manager.PostAsJsonAsync(
            $"{ServiceContractsUrl()}/services", ServiceBody(customerId, admin), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
