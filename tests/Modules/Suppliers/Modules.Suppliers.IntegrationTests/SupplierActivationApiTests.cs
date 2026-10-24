using System.Net;
using System.Net.Http.Json;
using Modules.Suppliers.Application;
using Npgsql;
using static Modules.Suppliers.IntegrationTests.SuppliersApiHarness;

namespace Modules.Suppliers.IntegrationTests;

public sealed class SupplierActivationApiTests
{
    [Fact]
    public async Task DeactivateThenActivateRoundTripsAndAuditsBothActions()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var created = await CreateSupplierAsync(client);

        var deactivated = await PostAsync(client, created.Id, "deactivate");
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.False((await ReadAsync(deactivated)).IsActive);

        var activated = await PostAsync(client, created.Id, "activate");
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        Assert.True((await ReadAsync(activated)).IsActive);

        Assert.Equal(1, await AuditCountAsync(
            database.GetConnectionString(), SupplierAuditActions.Deactivated, created.Id));
        Assert.Equal(1, await AuditCountAsync(
            database.GetConnectionString(), SupplierAuditActions.Activated, created.Id));
    }

    [Fact]
    public async Task DeactivateTwiceIs422AlreadyInactive()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var created = await CreateSupplierAsync(client);
        (await PostAsync(client, created.Id, "deactivate")).EnsureSuccessStatusCode();

        var response = await PostAsync(client, created.Id, "deactivate");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("suppliers.supplier.already_inactive", await BodyAsync(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActivateAnActiveSupplierIs422AlreadyActive()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateManager(factory);
        var created = await CreateSupplierAsync(client);

        var response = await PostAsync(client, created.Id, "activate");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("suppliers.supplier.already_active", await BodyAsync(response), StringComparison.Ordinal);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid id, string verb) =>
        client.PostAsync(
            $"{SuppliersUrl()}/{id}/{verb}", content: null, TestContext.Current.CancellationToken);

    private static async Task<SupplierResponse> ReadAsync(HttpResponseMessage response)
    {
        var supplier = await response.Content.ReadFromJsonAsync<SupplierResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(supplier);
        return supplier;
    }

    private static async Task<long> AuditCountAsync(string connectionString, string action, Guid id)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM platform.outbox_messages
            WHERE event_name = 'platform.audit.recorded.v1'
              AND payload->>'action' = @action
              AND payload->>'resourceId' = @id
            """,
            connection);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("id", id.ToString());
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
