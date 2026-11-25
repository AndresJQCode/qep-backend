using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Siigo.Application;
using Modules.Siigo.Infrastructure.Batches;
using static Modules.Siigo.IntegrationTests.SiigoApiHarness;
using static Modules.Siigo.IntegrationTests.SiigoInvoicingHarness;

namespace Modules.Siigo.IntegrationTests;

/// <summary>Spec 2026-11-21: la tanda de facturación con Siigo, de punta a punta con el host entero, el doble <see cref="FakeSiigoApi"/> y el
/// worker corrido a mano (<c>DrainAsync</c>): el del host no corre solo durante una prueba. Ninguna prueba llama a Siigo.</summary>
public sealed class SiigoInvoiceBatchApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string BatchesRoute(InvoicingWorld world) => $"/api/v1/tenants/{world.TenantId}/siigo/invoice-batches";

    private static Task<HttpResponseMessage> PostBatchAsync(InvoicingWorld world, params Guid[] orderIds) =>
        world.Client.PostAsJsonAsync(BatchesRoute(world), new { orderIds }, Ct);

    private static async Task<JsonElement> GetBatchAsync(InvoicingWorld world, Guid batchId) =>
        await ReadAsync(await world.Client.GetAsync($"{BatchesRoute(world)}/{batchId}", Ct));

    private static async Task DrainAsync(InvoicingWorld world)
    {
        var worker = world.Factory.Services.GetServices<IHostedService>().OfType<SiigoInvoiceBatchWorker>().Single();
        await worker.DrainAsync(Ct);
    }

    private static JsonElement Item(JsonElement batch, int position) => batch.GetProperty("items")[position];

    [Fact]
    public async Task ABatchIsAcceptedInProgressAndTheWorkerInvoicesEachOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);

        var response = await PostBatchAsync(world, world.OrderId);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var batchId = created.GetProperty("id").GetGuid();
        Assert.Equal(("InProgress", 1), (created.GetProperty("status").GetString(), created.GetProperty("total").GetInt32()));
        Assert.Equal("Pending", Item(created, 0).GetProperty("status").GetString());
        Assert.Empty(InvoiceCalls(world.Siigo));

        await DrainAsync(world);

        var batch = await GetBatchAsync(world, batchId);
        Assert.Equal(("Finished", 1, 0, 0), (batch.GetProperty("status").GetString(), batch.GetProperty("invoiced").GetInt32(), batch.GetProperty("skipped").GetInt32(), batch.GetProperty("failed").GetInt32()));
        Assert.Equal(("Invoiced", "FV-1-123"), (Item(batch, 0).GetProperty("status").GetString(), Item(batch, 0).GetProperty("invoiceNumber").GetString()));
        Assert.Single(InvoiceCalls(world.Siigo));
        Assert.True((await GetOrderAsync(world)).GetProperty("invoiced").GetBoolean());
    }

    [Fact]
    public async Task AnOrderThatIsNotThereIsSkippedAndTheOthersAreStillInvoiced()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var created = await ReadAsync(await PostBatchAsync(world, Guid.NewGuid(), world.OrderId));

        await DrainAsync(world);

        var batch = await GetBatchAsync(world, created.GetProperty("id").GetGuid());
        Assert.Equal("Finished", batch.GetProperty("status").GetString());
        Assert.Equal(("Skipped", "order.order.not_found"), (Item(batch, 0).GetProperty("status").GetString(), Item(batch, 0).GetProperty("errorCode").GetString()));
        Assert.Equal("Invoiced", Item(batch, 1).GetProperty("status").GetString());
        Assert.Single(InvoiceCalls(world.Siigo));
    }

    [Fact]
    public async Task AnOrderThatIsNotReadyIsSkippedWithWhatItLacksAndSiigoIsNeverAskedToCreateIt()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database, advisorLinked: false);
        var created = await ReadAsync(await PostBatchAsync(world, world.OrderId));

        await DrainAsync(world);

        var batch = await GetBatchAsync(world, created.GetProperty("id").GetGuid());
        var item = Item(batch, 0);
        Assert.Equal(("Skipped", "siigo.invoice.not_ready"), (item.GetProperty("status").GetString(), item.GetProperty("errorCode").GetString()));
        Assert.Equal(["seller_unlinked"], item.GetProperty("missing").EnumerateArray().Select(code => code.GetString()));
        Assert.Empty(InvoiceCalls(world.Siigo));
        Assert.Equal("Finished", batch.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ASiigoRejectionFailsOnlyThatOrderWithItsCode()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.OnRequest("/v1/invoices", _ => FakeSiigoApi.Respond(HttpStatusCode.BadRequest, """{"Errors":[{"Code":"invalid_item","Message":"CLIENTE-SENTINEL"}]}"""));
        var created = await ReadAsync(await PostBatchAsync(world, world.OrderId));

        await DrainAsync(world);

        var text = await world.Client.GetStringAsync($"{BatchesRoute(world)}/{created.GetProperty("id").GetGuid()}", Ct);
        using var batch = JsonDocument.Parse(text);
        Assert.Equal(("Failed", "siigo.invoice.rejected"), (Item(batch.RootElement, 0).GetProperty("status").GetString(), Item(batch.RootElement, 0).GetProperty("errorCode").GetString()));
        Assert.DoesNotContain("SENTINEL", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DrainingAgainNeverInvoicesAnOrderTwice()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await ReadAsync(await PostBatchAsync(world, world.OrderId));

        await DrainAsync(world);
        await DrainAsync(world);

        Assert.Single(InvoiceCalls(world.Siigo));
    }

    [Fact]
    public async Task ASiigoOutageFailsTheOrderAtOnceAndNothingIsScheduledForLater()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.OnRequest("/v1/invoices", _ => FakeSiigoApi.Respond(HttpStatusCode.ServiceUnavailable, "{}"));
        var created = await ReadAsync(await PostBatchAsync(world, world.OrderId));

        await DrainAsync(world);
        await DrainAsync(world);

        var batch = await GetBatchAsync(world, created.GetProperty("id").GetGuid());
        Assert.Equal(("Finished", "Failed"), (batch.GetProperty("status").GetString(), Item(batch, 0).GetProperty("status").GetString()));
        Assert.Equal("siigo.invoice.unavailable", Item(batch, 0).GetProperty("errorCode").GetString());
        Assert.Single(InvoiceCalls(world.Siigo));
    }

    // Un proceso que murió a mitad del envío no se reanuda más tarde: el usuario pudo haber creado la factura en Siigo entre tanto.
    [Fact]
    public async Task AnOrderWhoseSendWasCutShortIsFailedWithoutCallingSiigoAgain()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var created = await ReadAsync(await PostBatchAsync(world, world.OrderId));
        await ExecuteAsync(
            world.ConnectionString,
            "UPDATE siigo.invoice_batch_items SET status = 'Sending', attempts = 1, lease_until = now() - interval '1 minute'");

        await DrainAsync(world);

        var batch = await GetBatchAsync(world, created.GetProperty("id").GetGuid());
        Assert.Equal("Failed", Item(batch, 0).GetProperty("status").GetString());
        Assert.Empty(InvoiceCalls(world.Siigo));
    }

    [Fact]
    public async Task ARequesterWhoLostThePermissionSeesTheirOrdersFailAndNothingIsInvoiced()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var created = await ReadAsync(await PostBatchAsync(world, world.OrderId));
        await ExecuteAsync(world.ConnectionString, "UPDATE siigo.invoice_batches SET requested_by = gen_random_uuid()");

        await DrainAsync(world);

        var batch = await GetBatchAsync(world, created.GetProperty("id").GetGuid());
        Assert.Equal(("Failed", "siigo.invoice_batch.forbidden"), (Item(batch, 0).GetProperty("status").GetString(), Item(batch, 0).GetProperty("errorCode").GetString()));
        Assert.Empty(InvoiceCalls(world.Siigo));
    }

    [Fact]
    public async Task ABadBodyIs422WithItsFieldAndNothingIsCreated()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var repeated = Guid.NewGuid();
        var tooMany = Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToArray();

        var responses = new[]
        {
            await PostBatchAsync(world),
            await PostBatchAsync(world, repeated, repeated),
            await PostBatchAsync(world, Guid.Empty),
            await PostBatchAsync(world, tooMany),
            await world.Client.PostAsJsonAsync(BatchesRoute(world), new { }, Ct),
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode));
        using var json = JsonDocument.Parse(await responses[0].Content.ReadAsStringAsync(Ct));
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty("orderIds", out _));
        Assert.Equal(0, await ScalarAsync<long>(world.ConnectionString, "SELECT count(*) FROM siigo.invoice_batches"));
    }

    [Fact]
    public async Task WithoutThePermissionOrWithTheModuleOffTheBatchRoutesAre403()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var created = await ReadAsync(await PostBatchAsync(world, world.OrderId));
        using var other = CreateClient(world.Factory, world.Tenant.OwnerUserId, world.TenantId, SiigoPermissions.SettingsManage);

        var withoutPermission = new[]
        {
            await other.PostAsJsonAsync(BatchesRoute(world), new { orderIds = new[] { world.OrderId } }, Ct),
            await other.GetAsync($"{BatchesRoute(world)}/{created.GetProperty("id").GetGuid()}", Ct),
        };
        await ExecuteAsync(world.ConnectionString, "UPDATE tenancy.tenant_modules SET status = 'inactive' WHERE module_key = 'siigo'");
        var moduleOff = await PostBatchAsync(world, world.OrderId);

        Assert.All(withoutPermission, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
        Assert.Equal(HttpStatusCode.Forbidden, moduleOff.StatusCode);
        Assert.Equal(1, await ScalarAsync<long>(world.ConnectionString, "SELECT count(*) FROM siigo.invoice_batches"));
    }

    [Fact]
    public async Task ABatchThatDoesNotExistIs404()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);

        var response = await world.Client.GetAsync($"{BatchesRoute(world)}/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
