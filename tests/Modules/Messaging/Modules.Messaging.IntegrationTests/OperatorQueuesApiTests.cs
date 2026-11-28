using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>
/// Colas de la consola de plataforma (spec 2026-11-27) contra el esquema migrado: <c>QueueMetricsSource</c> lee por nombre las tablas
/// de cada cola, y esta clase amarra que el endpoint responde las ocho. El tenant operador es simulado, como en
/// <c>PlatformUsageApiTests</c>.
/// </summary>
public sealed class OperatorQueuesApiTests
{
    private static readonly Guid OperatorTenantId = Guid.Parse("01900000-0000-7000-8000-00000000c0de");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string QueuesUrl() => $"/api/v1/tenants/{OperatorTenantId}/operator/queues";

    [Fact]
    public async Task TheOperatorGetsAllTheEightQueuesInOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        using var client = CreateClient(factory, Guid.CreateVersion7(), OperatorTenantId, "operator.queues.read");

        var response = await client.GetAsync(QueuesUrl(), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var queues = body.GetProperty("queues").EnumerateArray().ToArray();
        Assert.Equal(
            ["webhook", "automation", "media", "campaign", "outbox", "shopify_webhook", "support_survey", "ai_model"],
            queues.Select(queue => queue.GetProperty("queue").GetString()));
        // Una base recién migrada no tiene nada encolado: cero, no null (null sólo es «no se pudo leer»).
        Assert.All(queues, queue => Assert.Equal(0, queue.GetProperty("backlog").GetInt64()));
        Assert.All(queues, queue => Assert.Equal(0, queue.GetProperty("retrying").GetInt64()));
        Assert.Equal(JsonValueKind.Null, queues.Single(queue => queue.GetProperty("queue").GetString() == "campaign").GetProperty("oldestAgeSeconds").ValueKind);
    }

    [Fact]
    public async Task WithoutThePermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), operatorTenantId: OperatorTenantId);
        using var client = CreateClient(factory, Guid.CreateVersion7(), OperatorTenantId, "operator.usage.read");

        var response = await client.GetAsync(QueuesUrl(), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
