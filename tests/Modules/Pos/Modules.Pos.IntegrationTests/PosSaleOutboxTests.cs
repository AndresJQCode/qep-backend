using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using static Modules.Pos.IntegrationTests.PosApiHarness;

namespace Modules.Pos.IntegrationTests;

/// <summary>Crear y anular una venta escriben <c>pos.sale.created.v1</c> y <c>pos.sale.voided.v1</c> en el mismo guardado.</summary>
public sealed class PosSaleOutboxTests
{
    private static async Task<(string Payload, string CorrelationId)> OutboxRowAsync(
        Testcontainers.PostgreSql.PostgreSqlContainer database, string eventName, Guid saleId)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT payload::text, correlation_id FROM platform.outbox_messages WHERE event_name = @event AND payload->>'saleId' = @sale",
            connection);
        command.Parameters.AddWithValue("event", eventName);
        command.Parameters.AddWithValue("sale", saleId.ToString());
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken), $"No hay fila de {eventName}.");
        var row = (reader.GetString(0), reader.GetString(1));
        Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken), $"Hay más de una fila de {eventName}.");
        return row;
    }

    [Fact]
    public async Task CreateAndVoidWriteTheirEventsInTheSameTransaction()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var session = await world.OpenSessionAsync(world.Admin);
        var id = Guid.CreateVersion7();

        var created = await world.Admin.PostAsJsonAsync(
            $"{world.Url}/sales",
            PosWorld.SaleBody(id, session.Id, world.WorkedExampleLines(), PosWorld.SplitPayments()),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var voided = await world.Admin.PostAsJsonAsync(
            $"{world.Url}/sales/{id}/void", new { reason = "Cliente se arrepintió" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);

        var (createdPayload, createdCorrelation) = await OutboxRowAsync(database, "pos.sale.created.v1", id);
        using var createdJson = JsonDocument.Parse(createdPayload);
        var root = createdJson.RootElement;
        Assert.Equal(world.Tenant.TenantId, root.GetProperty("tenantId").GetGuid());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(
            [world.Shampoo, world.Avena, world.Jabon],
            root.GetProperty("lines").EnumerateArray().Select(line => line.GetProperty("variantId").GetGuid()).ToArray());
        Assert.Equal(
            [2m, 1.5m, 3m],
            root.GetProperty("lines").EnumerateArray().Select(line => line.GetProperty("quantity").GetDecimal()).ToArray());
        Assert.Equal(id.ToString("D"), createdCorrelation);

        var (voidedPayload, voidedCorrelation) = await OutboxRowAsync(database, "pos.sale.voided.v1", id);
        using var voidedJson = JsonDocument.Parse(voidedPayload);
        Assert.Equal(2, voidedJson.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(id.ToString("D"), voidedCorrelation);
    }
}
