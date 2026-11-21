using Modules.Siigo.Application;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json;
using static Modules.Siigo.IntegrationTests.SiigoApiHarness;
using static Modules.Siigo.IntegrationTests.SiigoInvoicingHarness;

namespace Modules.Siigo.IntegrationTests;

/// <summary>Spec 2026-11-18 §5.4 a §5.6: generar, retomar, estampar y resolver la factura de Siigo de un pedido, de punta a punta con el
/// host entero y el doble <see cref="FakeSiigoApi"/>. Ninguna prueba llama a Siigo.</summary>
public sealed class SiigoInvoiceApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> PostAsync(InvoicingWorld world, string path = "", object? body = null) =>
        world.Client.PostAsJsonAsyncSafe(world.BaseRoute + path, body);

    private static Task<string?> ScalarTextAsync(InvoicingWorld world, string sql) =>
        ScalarAsync<string?>(world.ConnectionString, sql, ("id", world.OrderId));

    private static void AssertRejectedCode(HttpResponseMessage response, HttpStatusCode status, string code, string body)
    {
        Assert.Equal(status, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AReadyOrderIsCreatedAndMarkedInvoicedWithTheSiigoNumber()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);

        var response = await PostAsync(world);

        var rows = await ReadAsync(response);
        var row = rows[0];
        Assert.Equal(("Order", "Created", "FV-1-123"), (row.GetProperty("kind").GetString(), row.GetProperty("status").GetString(), row.GetProperty("number").GetString()));
        Assert.False(row.TryGetProperty("requestJson", out _));
        var order = await GetOrderAsync(world);
        Assert.True(order.GetProperty("invoiced").GetBoolean());
        Assert.Equal("siigo", order.GetProperty("invoiceProvider").GetString());
        Assert.Equal("FV-1-123", order.GetProperty("invoiceNumber").GetString());
        Assert.Single(InvoiceCalls(world.Siigo));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithDianOffStampSendIsFalseAndWithDianOnItIsTrue(bool dian)
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database, sendToDian: dian);

        await ReadAsync(await PostAsync(world));

        using var body = JsonDocument.Parse(InvoiceCalls(world.Siigo).Single().Body!);
        Assert.Equal(dian, body.RootElement.GetProperty("stamp").GetProperty("send").GetBoolean());
    }

    [Fact]
    public async Task AnUnreadyOrderIs422WithAllMissingAndNothingReachesSiigo()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database, linkProduct: false);

        var response = await PostAsync(world);

        var text = await response.Content.ReadAsStringAsync(Ct);
        AssertRejectedCode(response, HttpStatusCode.UnprocessableEntity, "siigo.invoice.not_ready", text);
        using var json = JsonDocument.Parse(text);
        var missing = json.RootElement.GetProperty("details").GetProperty("missing").EnumerateArray().Select(item => item.GetProperty("code").GetString()).ToList();
        Assert.Contains("variant_unlinked", missing);
        Assert.DoesNotContain(world.Siigo.Calls, call => call.Path is "/v1/invoices" or "/v1/customers");
        Assert.Equal(0, await ScalarAsync<long>(world.ConnectionString, "SELECT count(*) FROM siigo.invoices"));
    }

    [Fact]
    public async Task InvalidTotalPaymentsLeavesItRejectedWithTheExpectedTotalAndTheOrderUnmarked()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.OnRequest("/v1/invoices", _ => FakeSiigoApi.Respond(
            HttpStatusCode.BadRequest, """{"Errors":[{"Code":"invalid_total_payments","Message":"El total de pagos debe ser 288490.5"}]}"""));

        var row = (await ReadAsync(await PostAsync(world)))[0];

        Assert.Equal("Rejected", row.GetProperty("status").GetString());
        Assert.Equal("siigo.invoice.totals_mismatch", row.GetProperty("errorCode").GetString());
        Assert.Equal(288490.5m, row.GetProperty("expectedPaymentsTotal").GetDecimal());
        Assert.False((await GetOrderAsync(world)).GetProperty("invoiced").GetBoolean());

        // Siigo no la creó: un segundo POST arma otra fila, con otra clave.
        world.Siigo.OnRequest("/v1/invoices", _ => Created("FV-1-200", "id-200", 0m));
        var second = (await ReadAsync(await PostAsync(world)))[0];
        Assert.Equal("Created", second.GetProperty("status").GetString());
        Assert.Equal(2, await ScalarAsync<long>(world.ConnectionString, "SELECT count(*) FROM siigo.invoices"));
        var keys = InvoiceCalls(world.Siigo).Select(call => call.Header("Idempotency-Key")).Distinct().Count();
        Assert.Equal(2, keys);
    }

    [Fact]
    public async Task ATimeoutLeavesItPendingAndTheRetrySendsTheSameBytesAndKey()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.OnRequest("/v1/invoices", _ => FakeSiigoApi.Respond(HttpStatusCode.ServiceUnavailable, "{}"));

        var first = (await ReadAsync(await PostAsync(world)))[0];
        Assert.Equal("Pending", first.GetProperty("status").GetString());
        Assert.False((await GetOrderAsync(world)).GetProperty("invoiced").GetBoolean());

        world.Siigo.OnRequest("/v1/invoices", _ => Created("FV-1-123", "id-1", 0m));
        var retried = (await ReadAsync(await PostAsync(world)))[0];

        Assert.Equal("Created", retried.GetProperty("status").GetString());
        var calls = InvoiceCalls(world.Siigo);
        Assert.Equal(2, calls.Count);
        Assert.Equal(calls[0].Body, calls[1].Body);
        Assert.Equal(calls[0].Header("Idempotency-Key"), calls[1].Header("Idempotency-Key"));
        Assert.Equal(1, await ScalarAsync<long>(world.ConnectionString, "SELECT count(*) FROM siigo.invoices"));
    }

    [Fact]
    public async Task RetryingAPendingInvoiceWithoutConnectionLeavesItPending()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.OnRequest("/v1/invoices", _ => FakeSiigoApi.Respond(HttpStatusCode.ServiceUnavailable, "{}"));
        await ReadAsync(await PostAsync(world));
        await ExecuteAsync(world.ConnectionString, "UPDATE integrations.connections SET status = 'Paused'");

        var response = await PostAsync(world);

        AssertRejectedCode(response, HttpStatusCode.Conflict, "siigo.connection_unavailable", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Pending", await ScalarAsync<string>(world.ConnectionString, "SELECT status FROM siigo.invoices"));
        Assert.Single(InvoiceCalls(world.Siigo));
    }

    [Fact]
    public async Task TwoConcurrentIssuesCreateOneInvoice()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.OnRequest("/v1/invoices", _ =>
        {
            Thread.Sleep(1500);
            return Created("FV-1-123", "id-1", 0m);
        });

        var responses = await Task.WhenAll(PostAsync(world), PostAsync(world));

        foreach (var response in responses)
        {
            await EnsureSuccessAsync(response);
        }

        Assert.Single(InvoiceCalls(world.Siigo));
        Assert.Equal(1, await ScalarAsync<long>(world.ConnectionString, "SELECT count(*) FROM siigo.invoices"));
        Assert.True((await GetOrderAsync(world)).GetProperty("invoiced").GetBoolean());
    }

    [Fact]
    public async Task AMissingCustomerIsCreatedBeforeTheInvoiceAndAnExistingOneIsNot()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.OnRequest("/v1/customers", call => call.Method == HttpMethod.Get
            ? FakeSiigoApi.Respond(HttpStatusCode.OK, """{"pagination":{"page":1,"page_size":25,"total_results":0},"results":[]}""")
            : FakeSiigoApi.Respond(HttpStatusCode.Created, """{"id":"c-1","identification":"900123456","active":true}"""));

        await ReadAsync(await PostAsync(world));

        Assert.Equal(1, world.Siigo.Calls.Count(call => call.Path == "/v1/customers" && call.Method == HttpMethod.Post));
        var order = world.Siigo.Calls.Select((call, index) => (call, index)).ToList();
        Assert.True(order.First(item => item.call.Path == "/v1/customers" && item.call.Method == HttpMethod.Post).index
            < order.First(item => item.call.Path == "/v1/invoices").index);

        // Con otro pedido y el cliente ya en Siigo no se crea nada.
        await using var database2 = await StartDatabaseAsync();
        using var world2 = await ArrangeAsync(database2);
        world2.Siigo.OnRequest("/v1/customers", _ => FakeSiigoApi.Respond(
            HttpStatusCode.OK, """{"pagination":{"page":1,"page_size":25,"total_results":1},"results":[{"id":"c-9","identification":"1","active":true}]}"""));
        await ReadAsync(await PostAsync(world2));
        Assert.DoesNotContain(world2.Siigo.Calls, call => call.Path == "/v1/customers" && call.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task WithFreightSeparateTwoInvoicesAreCreatedAndAFreightRejectionKeepsTheOrderMarked()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database, freightSeparate: true, freight: true);
        world.Siigo.AddProduct("p-f", "FLETE", "Flete");
        var calls = 0;
        world.Siigo.OnRequest("/v1/invoices", _ => Interlocked.Increment(ref calls) == 1
            ? Created("FV-1-123", "id-1", 0m)
            : FakeSiigoApi.Respond(HttpStatusCode.BadRequest, """{"Errors":[{"Code":"invalid_code","Message":"El producto FLETE no existe"}]}"""));

        var rows = await ReadAsync(await PostAsync(world));

        var statuses = rows.EnumerateArray().Select(row => $"{row.GetProperty("kind").GetString()}:{row.GetProperty("status").GetString()}").ToList();
        Assert.Equal(["Order:Created", "Freight:Rejected"], statuses);
        Assert.True((await GetOrderAsync(world)).GetProperty("invoiced").GetBoolean());
        Assert.Equal(2, InvoiceCalls(world.Siigo).Count);
    }

    [Fact]
    public async Task AnUnknownResolvedAsMissingFreesTheOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.OnRequest("/v1/invoices", _ => FakeSiigoApi.Respond(HttpStatusCode.ServiceUnavailable, "{}"));
        await ReadAsync(await PostAsync(world));
        await ExecuteAsync(world.ConnectionString, "UPDATE siigo.invoices SET created_at = created_at - interval '2 days'");

        var unknown = (await ReadAsync(await world.Client.GetAsync(world.BaseRoute, Ct)))[0];
        Assert.Equal("Unknown", unknown.GetProperty("status").GetString());

        // Un POST con una Unknown la devuelve tal cual, sin llamar a Siigo.
        await ReadAsync(await PostAsync(world));
        Assert.Single(InvoiceCalls(world.Siigo));

        var resolved = await ReadAsync(await PostAsync(world, "/resolve", new { kind = "Order", outcome = "missing" }));
        Assert.Equal("Failed", resolved.GetProperty("status").GetString());
        world.Siigo.OnRequest("/v1/invoices", _ => Created("FV-1-124", "id-2", 0m));
        var again = (await ReadAsync(await PostAsync(world)))[0];
        Assert.Equal("Created", again.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AnUnknownResolvedAsExistsMarksTheOrderWithThatNumberAndOnlyAnUnknownCanBeResolved()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await ReadAsync(await PostAsync(world));

        var notUnknown = await PostAsync(world, "/resolve", new { outcome = "missing" });
        AssertRejectedCode(notUnknown, HttpStatusCode.UnprocessableEntity, "siigo.invoice.state_invalid", await notUnknown.Content.ReadAsStringAsync(Ct));

        await using var database2 = await StartDatabaseAsync();
        using var world2 = await ArrangeAsync(database2);
        world2.Siigo.OnRequest("/v1/invoices", _ => FakeSiigoApi.Respond(HttpStatusCode.ServiceUnavailable, "{}"));
        await ReadAsync(await PostAsync(world2));
        await ExecuteAsync(world2.ConnectionString, "UPDATE siigo.invoices SET created_at = created_at - interval '2 days'");

        var resolved = await ReadAsync(await PostAsync(world2, "/resolve", new { outcome = "exists", number = "FV-1-777" }));

        Assert.Equal(("Created", "FV-1-777"), (resolved.GetProperty("status").GetString(), resolved.GetProperty("number").GetString()));
        var order = await GetOrderAsync(world2);
        Assert.Equal("FV-1-777", order.GetProperty("invoiceNumber").GetString());
    }

    [Fact]
    public async Task StampRetryMovesCreatedToStamped()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await ReadAsync(await PostAsync(world));
        world.Siigo.OnRequest("/v1/invoices/d1b6c0a2-77aa-4e5f-9a0c-0a1b2c3d4e5f/stamp", _ => FakeSiigoApi.Respond(HttpStatusCode.OK, """{"stamp":{"status":"Accepted","cufe":"cufe-1"}}"""));
        world.Siigo.OnRequest("/v1/invoices", _ => Created("FV-1-123", "id-1", 0m));

        var row = await ReadAsync(await PostAsync(world, "/stamp?kind=Order"));

        Assert.Equal(("Stamped", "cufe-1"), (row.GetProperty("status").GetString(), row.GetProperty("cufe").GetString()));
    }

    [Fact]
    public async Task ThePreviewReportsAnAdvisorWithoutSiigoUser()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database, advisorInSiigo: false);

        var preview = await ReadAsync(await world.Client.GetAsync(world.BaseRoute + "/preview", Ct));

        var missing = Assert.Single(preview.GetProperty("missing").EnumerateArray());
        Assert.Equal("seller_not_found", missing.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, missing.GetProperty("reference").ValueKind);
        Assert.Equal(world.Tenant.Email, missing.GetProperty("label").GetString());
    }

    [Fact]
    public async Task IssuingWithAnAdvisorWithoutSiigoUserIsRefused()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database, advisorInSiigo: false);

        var response = await PostAsync(world);

        var text = await response.Content.ReadAsStringAsync(Ct);
        AssertRejectedCode(response, HttpStatusCode.UnprocessableEntity, "siigo.invoice.not_ready", text);
        using var json = JsonDocument.Parse(text);
        Assert.Equal(
            ["seller_not_found"],
            json.RootElement.GetProperty("details").GetProperty("missing").EnumerateArray().Select(item => item.GetProperty("code").GetString()));
        Assert.DoesNotContain(world.Siigo.Calls, call => call.Path is "/v1/invoices" or "/v1/customers");
        Assert.Equal(0, await ScalarAsync<long>(world.ConnectionString, "SELECT count(*) FROM siigo.invoices"));
    }

    [Fact]
    public async Task TheIssuedInvoiceCarriesTheAdvisorAsSeller()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);

        await ReadAsync(await PostAsync(world));

        using var body = JsonDocument.Parse(InvoiceCalls(world.Siigo).Single().Body!);
        Assert.Equal(629, body.RootElement.GetProperty("seller").GetInt32());
    }

    [Fact]
    public async Task ThePreviewShowsDraftsAndPaymentsWithoutWritingOrCallingSiigo()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);

        var preview = await ReadAsync(await world.Client.GetAsync(world.BaseRoute + "/preview", Ct));

        var draft = preview.GetProperty("drafts")[0];
        Assert.Equal("Order", draft.GetProperty("kind").GetString());
        Assert.Equal(35_700m, draft.GetProperty("total").GetDecimal());
        Assert.Empty(preview.GetProperty("missing").EnumerateArray());
        Assert.Equal(0, await ScalarAsync<long>(world.ConnectionString, "SELECT count(*) FROM siigo.invoices"));
        Assert.DoesNotContain(world.Siigo.Calls, call => call.Path is "/v1/invoices" or "/v1/customers");
    }

    [Fact]
    public async Task TheRealStatusAdapterSeesOpenInvoices()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.OnRequest("/v1/invoices", _ => FakeSiigoApi.Respond(HttpStatusCode.ServiceUnavailable, "{}"));
        await ReadAsync(await PostAsync(world));

        var manual = await world.Client.PostAsync($"/api/v1/tenants/{world.TenantId}/orders/{world.OrderId}/invoice", null, Ct);

        AssertRejectedCode(manual, HttpStatusCode.Conflict, "order.order.external_invoice_pending", await manual.Content.ReadAsStringAsync(Ct));
    }

    // Con el módulo apagado no hay pantalla ni endpoint (403) para resolver una factura Pending: no puede seguir bloqueando la marca manual.
    [Fact]
    public async Task WithTheModuleOffALiveSiigoInvoiceNoLongerBlocksTheManualMark()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.OnRequest("/v1/invoices", _ => FakeSiigoApi.Respond(HttpStatusCode.ServiceUnavailable, "{}"));
        await ReadAsync(await PostAsync(world));
        await ExecuteAsync(world.ConnectionString, "UPDATE tenancy.tenant_modules SET status = 'inactive' WHERE module_key = 'siigo'");

        var manual = await world.Client.PostAsync($"/api/v1/tenants/{world.TenantId}/orders/{world.OrderId}/invoice", null, Ct);

        Assert.Equal(HttpStatusCode.OK, manual.StatusCode);
    }

    // I1: la factura ya existe en Siigo; si el pedido quedó marcado a mano (o anulado) antes de que se pudiera marcar, el POST no falla.
    [Fact]
    public async Task AnOrderMarkedByHandAfterTheSendReturnsTheCreatedRowWithAWarningAndNeverA422()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.OnRequest("/v1/invoices", _ =>
        {
            ExecuteAsync(
                world.ConnectionString,
                "UPDATE quotations.orders SET invoiced_at = now(), invoiced_by = @by WHERE id = @id",
                ("by", Guid.CreateVersion7()), ("id", world.OrderId)).GetAwaiter().GetResult();
            return Created("FV-1-123", "id-1", 0m);
        });

        var first = (await ReadAsync(await PostAsync(world)))[0];
        var second = (await ReadAsync(await PostAsync(world)))[0];

        foreach (var row in new[] { first, second })
        {
            Assert.Equal("Created", row.GetProperty("status").GetString());
            Assert.Equal("FV-1-123", row.GetProperty("number").GetString());
            Assert.True(row.GetProperty("orderMarkFailed").GetBoolean());
        }

        Assert.Single(InvoiceCalls(world.Siigo));
        Assert.Null((await GetOrderAsync(world)).GetProperty("invoiceProvider").GetString());
    }

    // I2: la política, el grupo de rutas y la revalidación del handler.
    [Fact]
    public async Task WithoutThePermissionTheInvoiceEndpointsAre403AndNothingIsCreatedOrCalled()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        using var other = CreateClient(world.Factory, world.Tenant.OwnerUserId, world.TenantId, SiigoPermissions.SettingsManage);

        var responses = new[]
        {
            await other.PostAsync(world.BaseRoute, null, Ct),
            await other.GetAsync(world.BaseRoute, Ct),
            await other.GetAsync(world.BaseRoute + "/preview", Ct),
            await other.PostAsync(world.BaseRoute + "/stamp", null, Ct),
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
        await AssertUntouchedAsync(world);
    }

    [Fact]
    public async Task WithTheModuleOffTheInvoiceEndpointsAre403AndNothingIsCreatedOrCalled()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await ExecuteAsync(world.ConnectionString, "UPDATE tenancy.tenant_modules SET status = 'inactive' WHERE module_key = 'siigo'");

        var post = await PostAsync(world);
        var preview = await world.Client.GetAsync(world.BaseRoute + "/preview", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);
        await AssertUntouchedAsync(world);
    }

    [Fact]
    public async Task ARouteOfAnotherTenantIs403AndAForeignOrderIsNeverServed()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var intruder = await RegisterTenantAsync(world.Factory);
        await EnableSiigoAsync(world.ConnectionString, intruder.TenantId);
        using var client = CreateClient(world.Factory, intruder.OwnerUserId, intruder.TenantId, InvoicerPermissions);
        var foreign = $"/api/v1/tenants/{world.TenantId}/orders/{world.OrderId}/siigo-invoice";
        var own = $"/api/v1/tenants/{intruder.TenantId}/orders/{world.OrderId}/siigo-invoice";

        var viaForeignRoute = await client.PostAsync(foreign, null, Ct);
        var viaOwnRoute = await client.PostAsync(own, null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, viaForeignRoute.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, viaOwnRoute.StatusCode);
        await AssertUntouchedAsync(world);
    }

    // I3: un proceso que murió a mitad de envío. Dentro del reclamo no se reenvía; pasado, se reenvía lo mismo.
    [Fact]
    public async Task APendingWhoseSendLeaseExpiredIsResentWithTheSameBytesAndKeyAndOneInsideTheLeaseIsNot()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.OnRequest("/v1/invoices", _ => FakeSiigoApi.Respond(HttpStatusCode.ServiceUnavailable, "{}"));
        await ReadAsync(await PostAsync(world));
        // Murió a mitad de envío: el reclamo sigue puesto.
        await ExecuteAsync(world.ConnectionString, "UPDATE siigo.invoices SET sending_since = now() - interval '30 seconds'");
        world.Siigo.OnRequest("/v1/invoices", _ => Created("FV-1-123", "id-1", 0m));

        var inside = (await ReadAsync(await PostAsync(world)))[0];

        Assert.Equal("Pending", inside.GetProperty("status").GetString());
        Assert.Single(InvoiceCalls(world.Siigo));

        await ExecuteAsync(world.ConnectionString, "UPDATE siigo.invoices SET sending_since = now() - interval '3 minutes'");
        var expired = (await ReadAsync(await PostAsync(world)))[0];

        Assert.Equal("Created", expired.GetProperty("status").GetString());
        var calls = InvoiceCalls(world.Siigo);
        Assert.Equal(2, calls.Count);
        Assert.Equal(calls[0].Body, calls[1].Body);
        Assert.Equal(calls[0].Header("Idempotency-Key"), calls[1].Header("Idempotency-Key"));
    }

    private static async Task AssertUntouchedAsync(InvoicingWorld world)
    {
        Assert.Equal(0, await ScalarAsync<long>(world.ConnectionString, "SELECT count(*) FROM siigo.invoices"));
        Assert.DoesNotContain(world.Siigo.Calls, call => call.Path is "/v1/invoices" or "/v1/customers");
    }

    [Fact]
    public async Task LockOrderOutsideATransactionThrowsAndInsideItTakesTheAdvisoryLock()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await using var scope = world.Factory.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<Modules.Siigo.Application.ISiigoUnitOfWork>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.LockOrderAsync(world.OrderId, Ct));

        await using var transaction = await unitOfWork.BeginTransactionAsync(Ct);
        await unitOfWork.LockOrderAsync(world.OrderId, Ct);
        var held = await ScalarAsync<long>(
            world.ConnectionString,
            "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND classid = 20261119 AND objid::int8 = (hashtext(@key)::int8 & 4294967295) AND objsubid = 2 AND granted",
            ("key", Modules.Siigo.Application.SiigoInvoiceLock.Key(world.OrderId)));
        Assert.Equal(1, held);
    }
}

internal static class SiigoHttpExtensions
{
    public static Task<HttpResponseMessage> PostAsJsonAsyncSafe(this HttpClient client, string url, object? body) =>
        body is null ? client.PostAsync(url, null, TestContext.Current.CancellationToken) : client.PostAsJsonAsync(url, body, TestContext.Current.CancellationToken);
}
