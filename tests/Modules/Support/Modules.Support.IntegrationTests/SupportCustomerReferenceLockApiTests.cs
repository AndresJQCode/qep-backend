using System.Net;
using BuildingBlocks.Infrastructure;
using Npgsql;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportTicketScenario;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Borrar clientes (P1/P2, copia de <c>Loans.CustomerReferenceLockApiTests</c>): quien escribe una referencia a un cliente toma el
/// candado compartido y comprueba que el cliente exista dentro de su transacción. Acá el borrado se simula con el candado exclusivo
/// tomado a mano: el alta tiene que esperar y, al despertar, ver el cliente ya borrado (Review Focus 5). Host propio por prueba:
/// la prueba mide una espera y no puede compartir la base con otras corridas.
/// </summary>
public sealed class SupportCustomerReferenceLockApiTests
{
    private static readonly TimeSpan NoAnswerWindow = TimeSpan.FromMilliseconds(500);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> HoldExclusiveAsync(
        string connectionString, Guid tenantId, Guid customerId)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        var transaction = await connection.BeginTransactionAsync(Ct);
        var key = CustomerReferenceLock.KeyFor(tenantId, customerId);
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))", connection, transaction);
        command.Parameters.AddWithValue("key", key);
        await command.ExecuteNonQueryAsync(Ct);
        return (connection, transaction);
    }

    private static async Task DeleteCustomerAndCommitAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid customerId)
    {
        await using (var delete = new NpgsqlCommand("DELETE FROM customers.customers WHERE id = @id", connection, transaction))
        {
            delete.Parameters.AddWithValue("id", customerId);
            await delete.ExecuteNonQueryAsync(Ct);
        }

        await transaction.CommitAsync(Ct);
    }

    private static async Task<long> CountAsync(string connectionString, string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task CreatingATicketWaitsForTheCustomerDeletionAndThenFails()
    {
        var database = await StartDatabaseAsync();
        await using var _d = database;
        using var factory = new QepApiFactory(database.GetConnectionString());
        var connectionString = database.GetConnectionString();
        var scenario = await StartAsync(factory);
        var customerId = await SeedCustomerAsync(factory, scenario.TenantId, "Novasteel SAS");
        // Calentamiento: la primera petición de una fábrica nueva tarda más que la ventana de espera, y sin él la prueba pasaría
        // aunque el candado no existiera (el alta «esperaría» por arranque en frío, no por el candado). Además siembra los tipos.
        var typeId = await scenario.TypeIdAsync("Queja");
        var (connection, transaction) = await HoldExclusiveAsync(connectionString, scenario.TenantId, customerId);
        await using var _c = connection;
        await using var _t = transaction;

        var post = scenario.PostAsync(Body(typeId, customerId: customerId));

        Assert.NotSame(post, await Task.WhenAny(post, Task.Delay(NoAnswerWindow, Ct)));
        await DeleteCustomerAndCommitAsync(connection, transaction, customerId);

        var response = await post;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.ticket.customer_not_found", await CodeOfAsync(response));
        Assert.Equal(0, await CountAsync(connectionString, "SELECT count(*) FROM support.tickets WHERE customer_id = @id", customerId));
        // El radicado volvió con la transacción: el siguiente alta es SOP-000001.
        var next = await scenario.CreateAsync(Body(typeId));
        Assert.Equal("SOP-000001", next.GetProperty("number").GetString());
    }

    [Fact]
    public async Task WithoutADeletionNothingChanges()
    {
        var database = await StartDatabaseAsync();
        await using var _d = database;
        using var factory = new QepApiFactory(database.GetConnectionString());
        var scenario = await StartAsync(factory);
        var customerId = await SeedCustomerAsync(factory, scenario.TenantId, "Novasteel SAS");

        var response = await scenario.PostAsync(Body(await scenario.TypeIdAsync("Queja"), customerId: customerId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await CountAsync(
            database.GetConnectionString(), "SELECT count(*) FROM support.tickets WHERE customer_id = @id", customerId));
    }
}
