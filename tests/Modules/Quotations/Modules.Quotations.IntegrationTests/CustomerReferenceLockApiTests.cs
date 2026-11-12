using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Infrastructure;
using Modules.Quotations.Application;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Borrar clientes (P1/P2): quien escribe una referencia a un cliente toma el candado compartido y comprueba que el
/// cliente exista dentro de su transacción. Aquí el borrado se simula con el candado exclusivo tomado a mano: la
/// escritura tiene que esperar, y al despertar ver el cliente ya borrado.
/// </summary>
public sealed class CustomerReferenceLockApiTests
{
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

    // Calentamiento: la primera petición a un módulo en una fábrica nueva arma su modelo de EF y tarda más que la ventana de
    // espera. Sin esto la escritura podría "esperar" por arranque en frío y la prueba pasaría aunque el candado no existiera.
    private static async Task WarmUpAsync(HttpClient client, string url) =>
        (await client.GetAsync(url, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

    private static async Task<string> CodeOfAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return problem.GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task CreatingAQuotationWaitsForTheDeletionAndThenFails()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        await WarmUpAsync(client, QuotationsUrl(tenantId));
        var (connection, transaction) = await HoldExclusiveAsync(connectionString, tenantId, customerId);
        await using var _c = connection;
        await using var _t = transaction;

        var post = client.PostAsJsonAsync(
            QuotationsUrl(tenantId),
            new CreateQuotationRequest(customerId, TodayInBogota().AddDays(30), null, null, null, null),
            TestContext.Current.CancellationToken);

        Assert.NotSame(post, await Task.WhenAny(post, Task.Delay(NoAnswerWindow, TestContext.Current.CancellationToken)));
        await DeleteCustomerAndCommitAsync(connection, transaction, customerId);

        var response = await post;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("quotation.quotation.client_not_found", await CodeOfAsync(response));
        Assert.Equal(0, await CountAsync(
            connectionString, "SELECT count(*) FROM quotations.quotations WHERE client_id = @id", customerId));
    }

    [Fact]
    public async Task CreatingAManualOrderWaitsForTheDeletionAndThenFails()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var request = new CreateOrderRequest(
            customerId, "Transferencia", "Pedido de mostrador", false, null, null,
            new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency),
            [new OrderEditItemRequest(productId, 3m)], []);
        await WarmUpAsync(client, $"/api/v1/tenants/{tenantId}/orders");
        var (connection, transaction) = await HoldExclusiveAsync(connectionString, tenantId, customerId);
        await using var _c = connection;
        await using var _t = transaction;

        var post = client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/orders", request, TestContext.Current.CancellationToken);

        Assert.NotSame(post, await Task.WhenAny(post, Task.Delay(NoAnswerWindow, TestContext.Current.CancellationToken)));
        await DeleteCustomerAndCommitAsync(connection, transaction, customerId);

        var response = await post;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("order.order.client_not_found", await CodeOfAsync(response));
        Assert.Equal(0, await CountAsync(
            connectionString, "SELECT count(*) FROM quotations.orders WHERE client_id = @id", customerId));
    }

    [Fact]
    public async Task ChangingTheQuotationClientToADeletedCustomerFails()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var originalId = await CreateActiveCustomerAsync(client, tenantId);
        var targetId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, originalId);
        var (connection, transaction) = await HoldExclusiveAsync(connectionString, tenantId, targetId);
        await using var _c = connection;
        await using var _t = transaction;

        var put = client.PutAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/client",
            new ChangeQuotationClientRequest(targetId),
            TestContext.Current.CancellationToken);

        Assert.NotSame(put, await Task.WhenAny(put, Task.Delay(NoAnswerWindow, TestContext.Current.CancellationToken)));
        await DeleteCustomerAndCommitAsync(connection, transaction, targetId);

        var response = await put;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("quotation.quotation.client_not_found", await CodeOfAsync(response));
        Assert.Equal(0, await CountAsync(
            connectionString, "SELECT count(*) FROM quotations.quotations WHERE client_id = @id", targetId));
    }

    [Fact]
    public async Task WithoutADeletionNothingChanges()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var customerId = await CreateActiveCustomerAsync(client, tenantId);

        var response = await client.PostAsJsonAsync(
            QuotationsUrl(tenantId),
            new CreateQuotationRequest(customerId, TodayInBogota().AddDays(30), null, null, null, null),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
