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
        Assert.Equal("siigo.invoice.unavailable", first.GetProperty("errorCode").GetString());
        Assert.False((await GetOrderAsync(world)).GetProperty("invoiced").GetBoolean());

        world.Siigo.OnRequest("/v1/invoices", _ => Created("FV-1-123", "id-1", 0m));
        var retried = (await ReadAsync(await PostAsync(world)))[0];

        Assert.Equal("Created", retried.GetProperty("status").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, retried.GetProperty("errorCode").ValueKind);
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

        // El último envío sin respuesta fue hace un momento: «No existe» espera aunque la fila ya sea Unknown.
        Assert.Equal(JsonValueKind.String, unknown.GetProperty("missingAvailableAt").ValueKind);
        var early = await PostAsync(world, "/resolve", new { kind = "Order", outcome = "missing" });
        AssertRejectedCode(
            early, HttpStatusCode.UnprocessableEntity, "siigo.invoice.resolve_too_early", await early.Content.ReadAsStringAsync(Ct));
        await AgeTheUnansweredSendAsync(world);

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

    // Un envío sin respuesta: la fila queda por confirmar (spec 2026-11-24 §3).
    private static async Task<JsonElement> LeaveAwaitingConfirmationAsync(InvoicingWorld world)
    {
        world.Siigo.OnRequest("/v1/invoices", _ => FakeSiigoApi.Respond(HttpStatusCode.ServiceUnavailable, "{}"));
        return await ReadAsync(await PostAsync(world));
    }

    // Han pasado más de los 10 minutos que «No existe» espera desde el último envío sin respuesta.
    private static Task AgeTheUnansweredSendAsync(InvoicingWorld world) =>
        ExecuteAsync(
            world.ConnectionString,
            """
            UPDATE siigo.invoices SET last_unanswered_at = now() - interval '11 minutes', sending_since = NULL
            WHERE last_unanswered_at IS NOT NULL
            """);

    [Fact]
    public async Task APendingAwaitingConfirmationIsResolvedAsExistsWithoutWaitingADay()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await LeaveAwaitingConfirmationAsync(world);

        var resolved = await ReadAsync(await PostAsync(world, "/resolve", new { outcome = "exists", number = "FV-1-777" }));

        Assert.Equal(("Created", "FV-1-777"), (resolved.GetProperty("status").GetString(), resolved.GetProperty("number").GetString()));
        Assert.False(resolved.GetProperty("awaitingConfirmation").GetBoolean());
        var order = await GetOrderAsync(world);
        Assert.True(order.GetProperty("invoiced").GetBoolean());
        Assert.Equal("FV-1-777", order.GetProperty("invoiceNumber").GetString());
        Assert.Single(InvoiceCalls(world.Siigo));
    }

    [Fact]
    public async Task MissingBeforeTenMinutesIs422WithAvailableAtAndAfterThemFreesTheOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await LeaveAwaitingConfirmationAsync(world);

        var early = await PostAsync(world, "/resolve", new { outcome = "missing" });

        var text = await early.Content.ReadAsStringAsync(Ct);
        AssertRejectedCode(early, HttpStatusCode.UnprocessableEntity, "siigo.invoice.resolve_too_early", text);
        using (var json = JsonDocument.Parse(text))
        {
            var availableAt = json.RootElement.GetProperty("details").GetProperty("availableAt").GetDateTimeOffset();
            Assert.InRange(availableAt, DateTimeOffset.UtcNow.AddMinutes(8), DateTimeOffset.UtcNow.AddMinutes(10));
        }

        await AgeTheUnansweredSendAsync(world);
        var resolved = await ReadAsync(await PostAsync(world, "/resolve", new { outcome = "missing" }));

        Assert.Equal("Failed", resolved.GetProperty("status").GetString());
        world.Siigo.OnRequest("/v1/invoices", _ => Created("FV-1-124", "id-2", 0m));
        var again = (await ReadAsync(await PostAsync(world)))[0];
        Assert.Equal("Created", again.GetProperty("status").GetString());
        Assert.Equal(2, await ScalarAsync<long>(world.ConnectionString, "SELECT count(*) FROM siigo.invoices"));
    }

    [Fact]
    public async Task APendingBeingSentCannotBeResolved()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await LeaveAwaitingConfirmationAsync(world);
        await ExecuteAsync(world.ConnectionString, "UPDATE siigo.invoices SET sending_since = now()");

        var missing = await PostAsync(world, "/resolve", new { outcome = "missing" });
        var exists = await PostAsync(world, "/resolve", new { outcome = "exists", number = "FV-1-777" });

        AssertRejectedCode(missing, HttpStatusCode.UnprocessableEntity, "siigo.invoice.state_invalid", await missing.Content.ReadAsStringAsync(Ct));
        AssertRejectedCode(exists, HttpStatusCode.UnprocessableEntity, "siigo.invoice.state_invalid", await exists.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Pending", await ScalarAsync<string>(world.ConnectionString, "SELECT status FROM siigo.invoices"));
    }

    [Fact]
    public async Task ResolvingTheOrderAsMissingDiscardsTheFreightThatNeverLeft()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database, freightSeparate: true, freight: true);
        world.Siigo.AddProduct("p-f", "FLETE", "Flete");
        var rows = await LeaveAwaitingConfirmationAsync(world);
        Assert.Equal(2, rows.GetArrayLength());
        Assert.False(rows[1].GetProperty("awaitingConfirmation").GetBoolean());
        await AgeTheUnansweredSendAsync(world);

        var resolved = await ReadAsync(await PostAsync(world, "/resolve", new { kind = "Order", outcome = "missing" }));

        Assert.Equal("Failed", resolved.GetProperty("status").GetString());
        Assert.Equal(
            "Failed|siigo.invoice.order_rejected",
            await ScalarAsync<string>(world.ConnectionString, "SELECT status || '|' || error_code FROM siigo.invoices WHERE kind = 'Freight'"));
        Assert.Equal(
            0,
            await ScalarAsync<long>(world.ConnectionString, "SELECT count(*) FROM siigo.invoices WHERE status NOT IN ('Rejected', 'Failed')"));
        Assert.Single(InvoiceCalls(world.Siigo));
    }

    // La de flete sólo sale después de que la del pedido existe: aunque haya quedado marcada sin respuesta (o ya venció a las 24 h), si la
    // del pedido no existe, nunca salió y se descarta con ella. Viva, dejaría el pedido bloqueado.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolvingTheOrderAsMissingDiscardsAFreightMarkedUnanswered(bool expired)
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database, freightSeparate: true, freight: true);
        world.Siigo.AddProduct("p-f", "FLETE", "Flete");
        await LeaveAwaitingConfirmationAsync(world);
        await ExecuteAsync(
            world.ConnectionString,
            "UPDATE siigo.invoices SET unanswered_at = now(), last_unanswered_at = now() WHERE kind = 'Freight'");
        if (expired)
        {
            await ExecuteAsync(world.ConnectionString, "UPDATE siigo.invoices SET created_at = now() - interval '25 hours'");
            // Leer las del pedido aplica la transición a Unknown.
            await ReadAsync(await world.Client.GetAsync(world.BaseRoute, Ct));
            Assert.Equal(
                2, await ScalarAsync<long>(world.ConnectionString, "SELECT count(*) FROM siigo.invoices WHERE status = 'Unknown'"));
        }

        await AgeTheUnansweredSendAsync(world);

        await ReadAsync(await PostAsync(world, "/resolve", new { kind = "Order", outcome = "missing" }));

        Assert.Equal("Failed", await ScalarAsync<string>(world.ConnectionString, "SELECT status FROM siigo.invoices WHERE kind = 'Order'"));
        Assert.Equal(
            "Failed|siigo.invoice.order_rejected",
            await ScalarAsync<string>(
                world.ConnectionString, "SELECT concat(status, '|', error_code) FROM siigo.invoices WHERE kind = 'Freight'"));
    }

    [Fact]
    public async Task TheListSaysWhichPendingAwaitsConfirmationAndWhatCanBeDone()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await LeaveAwaitingConfirmationAsync(world);

        var idle = (await ReadAsync(await world.Client.GetAsync(world.BaseRoute, Ct)))[0];

        Assert.True(idle.GetProperty("awaitingConfirmation").GetBoolean());
        Assert.True(idle.GetProperty("canResend").GetBoolean());
        Assert.True(idle.GetProperty("canResolve").GetBoolean());
        Assert.Equal(JsonValueKind.String, idle.GetProperty("missingAvailableAt").ValueKind);

        await ExecuteAsync(world.ConnectionString, "UPDATE siigo.invoices SET sending_since = now()");
        var sending = (await ReadAsync(await world.Client.GetAsync(world.BaseRoute, Ct)))[0];

        Assert.False(sending.GetProperty("awaitingConfirmation").GetBoolean());
        Assert.False(sending.GetProperty("canResend").GetBoolean());
        Assert.False(sending.GetProperty("canResolve").GetBoolean());
        Assert.Equal(JsonValueKind.Null, sending.GetProperty("missingAvailableAt").ValueKind);
    }

    [Fact]
    public async Task TheResolutionAuditCarriesWhereItCameFrom()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await LeaveAwaitingConfirmationAsync(world);

        await ReadAsync(await PostAsync(world, "/resolve", new { outcome = "exists", number = "FV-1-777" }));

        Assert.Equal(
            "Pending",
            await ScalarAsync<string>(
                world.ConnectionString,
                "SELECT changed_fields ->> 'fromStatus' FROM audit.entries WHERE action = 'siigo.invoice.resolved'"));
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

    private const string SiigoInvoiceGuid = "d1b6c0a2-77aa-4e5f-9a0c-0a1b2c3d4e5f";

    private static async Task<Guid> InvoiceIdAsync(InvoicingWorld world) =>
        (await ReadAsync(await world.Client.GetAsync(world.BaseRoute, Ct)))[0].GetProperty("id").GetGuid();

    [Fact]
    public async Task ACreatedInvoiceOffersItsPdfAndTheDownloadReturnsTheSiigoFile()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var rows = await ReadAsync(await PostAsync(world));
        Assert.True(rows[0].GetProperty("hasPdf").GetBoolean());
        world.Siigo.On($"/v1/invoices/{SiigoInvoiceGuid}/pdf", HttpStatusCode.OK, """{"base64":"JVBERi0xLjQ="}""");
        var id = await InvoiceIdAsync(world);

        var response = await world.Client.GetAsync($"{world.BaseRoute}/{id}/pdf", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("%PDF-1.4"u8.ToArray(), await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("FV-1-123.pdf", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName);
    }

    [Fact]
    public async Task ARejectedInvoiceHasNoPdfAndItsDownloadIs404WithoutCallingSiigo()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.OnRequest("/v1/invoices", _ => FakeSiigoApi.Respond(HttpStatusCode.BadRequest, """{"Errors":[{"Code":"invalid_item","Message":"x"}]}"""));
        await PostAsync(world);
        var id = await InvoiceIdAsync(world);
        var listed = (await ReadAsync(await world.Client.GetAsync(world.BaseRoute, Ct)))[0];

        var response = await world.Client.GetAsync($"{world.BaseRoute}/{id}/pdf", Ct);

        Assert.False(listed.GetProperty("hasPdf").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(world.Siigo.Calls, call => call.Path.EndsWith("/pdf", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThePdfOfAnotherOrdersInvoiceIs404()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await ReadAsync(await PostAsync(world));

        var response = await world.Client.GetAsync($"{world.BaseRoute}/{Guid.NewGuid()}/pdf", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WithoutTheInvoicePermissionThePdfIs403AndSiigoIsNotCalled()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await ReadAsync(await PostAsync(world));
        var id = await InvoiceIdAsync(world);
        using var other = CreateClient(world.Factory, world.Tenant.OwnerUserId, world.TenantId, SiigoPermissions.SettingsManage);

        var response = await other.GetAsync($"{world.BaseRoute}/{id}/pdf", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain(world.Siigo.Calls, call => call.Path.EndsWith("/pdf", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThePreviewReportsAnUnlinkedAdvisorWithoutCallingSiigoUsers()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database, advisorLinked: false);

        var preview = await ReadAsync(await world.Client.GetAsync(world.BaseRoute + "/preview", Ct));

        var missing = Assert.Single(preview.GetProperty("missing").EnumerateArray());
        Assert.Equal("seller_unlinked", missing.GetProperty("code").GetString());
        Assert.True(Guid.TryParse(missing.GetProperty("reference").GetString(), out _));
        Assert.Equal(world.Tenant.Email, missing.GetProperty("label").GetString());
        Assert.DoesNotContain(world.Siigo.Calls, call => call.Path == "/v1/users");
    }

    [Fact]
    public async Task IssuingWithAnUnlinkedAdvisorIsRefused()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database, advisorLinked: false);

        var response = await PostAsync(world);

        var text = await response.Content.ReadAsStringAsync(Ct);
        AssertRejectedCode(response, HttpStatusCode.UnprocessableEntity, "siigo.invoice.not_ready", text);
        using var json = JsonDocument.Parse(text);
        Assert.Equal(
            ["seller_unlinked"],
            json.RootElement.GetProperty("details").GetProperty("missing").EnumerateArray().Select(item => item.GetProperty("code").GetString()));
        Assert.DoesNotContain(world.Siigo.Calls, call => call.Path is "/v1/invoices" or "/v1/customers" or "/v1/users");
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
        Assert.DoesNotContain(world.Siigo.Calls, call => call.Path is "/v1/invoices" or "/v1/customers" or "/v1/users");
    }

    // La pantalla del pedido sólo ofrece «Facturar en Siigo» si la conexión de la empresa del pedido sirve: el backend lo dice, el
    // navegador no cruza conexiones con empresas.
    [Fact]
    public async Task AvailabilityIsTrueWhenTheConnectionOfTheOrderCompanyIsActive()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);

        var availability = await ReadAsync(await world.Client.GetAsync(world.BaseRoute + "/availability", Ct));

        Assert.True(availability.GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, availability.GetProperty("reason").ValueKind);
        await AssertUntouchedAsync(world);
    }

    [Fact]
    public async Task AvailabilityIsFalseWithConnectionUnavailableWhenTheConnectionOfTheOrderCompanyIsPaused()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await ExecuteAsync(world.ConnectionString, "UPDATE integrations.connections SET status = 'Paused'");

        var availability = await ReadAsync(await world.Client.GetAsync(world.BaseRoute + "/availability", Ct));

        Assert.False(availability.GetProperty("available").GetBoolean());
        Assert.Equal("siigo.connection_unavailable", availability.GetProperty("reason").GetString());
        await AssertUntouchedAsync(world);
    }

    [Fact]
    public async Task AvailabilityIsFalseWithNoConnectionWhenNoConnectionInvoicesForTheOrderCompany()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await ExecuteAsync(world.ConnectionString, "UPDATE siigo.settings SET company_id = NULL");

        var availability = await ReadAsync(await world.Client.GetAsync(world.BaseRoute + "/availability", Ct));

        Assert.False(availability.GetProperty("available").GetBoolean());
        Assert.Equal("siigo.invoice.no_connection", availability.GetProperty("reason").GetString());
        await AssertUntouchedAsync(world);
    }

    // Spec 2026-11-24 §1: la pantalla del pedido retoma el seguimiento de la tanda sin terminar al recargar.
    [Fact]
    public async Task TheAvailabilityPointsToTheOpenBatchOfTheOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        // Un rechazo termina la tanda y deja el pedido sin facturar: con él facturado, availability responde el 422 de la marca.
        world.Siigo.OnRequest("/v1/invoices", _ => FakeSiigoApi.Respond(
            HttpStatusCode.BadRequest, """{"Errors":[{"Code":"invalid_item","Message":"x"}]}"""));
        var batch = await ReadAsync(await PostBatchAsync(world, world.Client, world.TenantId));

        var queued = await ReadAsync(await world.Client.GetAsync(world.BaseRoute + "/availability", Ct));
        await world.Factory.Services.GetServices<IHostedService>().OfType<SiigoInvoiceBatchWorker>().Single().DrainAsync(Ct);
        var settled = await ReadAsync(await world.Client.GetAsync(world.BaseRoute + "/availability", Ct));

        Assert.Equal(batch.GetProperty("id").GetGuid(), queued.GetProperty("queuedBatchId").GetGuid());
        Assert.Equal(JsonValueKind.Null, settled.GetProperty("queuedBatchId").ValueKind);
    }

    // Las consultas de tandas abiertas son por tenant: la de otro cliente, aunque nombre el mismo pedido, no se ve ni bloquea.
    [Fact]
    public async Task TheOpenBatchOfAnotherTenantNeitherShowsNorBlocks()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var other = await RegisterTenantAsync(world.Factory);
        await EnableSiigoAsync(world.ConnectionString, other.TenantId);
        using var otherClient = CreateClient(world.Factory, other.OwnerUserId, other.TenantId, SiigoPermissions.InvoiceIssue);
        await ReadAsync(await PostBatchAsync(world, otherClient, other.TenantId));

        var availability = await ReadAsync(await world.Client.GetAsync(world.BaseRoute + "/availability", Ct));
        var own = await PostBatchAsync(world, world.Client, world.TenantId);

        Assert.Equal(JsonValueKind.Null, availability.GetProperty("queuedBatchId").ValueKind);
        Assert.Equal(HttpStatusCode.Accepted, own.StatusCode);
    }

    private static Task<HttpResponseMessage> PostBatchAsync(InvoicingWorld world, HttpClient client, Guid tenantId) =>
        client.PostAsJsonAsync($"/api/v1/tenants/{tenantId}/siigo/invoice-batches", new { orderIds = new[] { world.OrderId } }, Ct);

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

    // Spec 2026-11-24 §3: una caída de Siigo no dice si el documento quedó creado, así que el pedido sigue bloqueado hasta resolverla.
    [Fact]
    public async Task ADocumentsServiceOnTheFirstAttemptBlocksTheManualMarkUntilResolved()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.OnRequest("/v1/invoices", _ => FakeSiigoApi.Respond(
            HttpStatusCode.BadRequest, """{"Errors":[{"Code":"documents_service","Message":"Your request could not be completed"}]}"""));

        var row = (await ReadAsync(await PostAsync(world)))[0];
        var manual = await world.Client.PostAsync($"/api/v1/tenants/{world.TenantId}/orders/{world.OrderId}/invoice", null, Ct);

        Assert.Equal("Pending", row.GetProperty("status").GetString());
        Assert.Equal("siigo.invoice.siigo_service", row.GetProperty("errorCode").GetString());
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

    // La pantalla del pedido ofrece «Generar» sólo si la lectura le dice que falta la marca: la factura existe y el pedido no la lleva.
    [Fact]
    public async Task TheOrderRowsSayWhenTheInvoiceWasNeverMarked()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database, freightSeparate: true, freight: true);
        world.Siigo.AddProduct("p-f", "FLETE", "Flete");
        await ReadAsync(await PostAsync(world));
        // El pedido sí quedó marcado: no hay nada que avisar.
        Assert.Equal([("Order", false), ("Freight", false)], MarkWarnings(await ReadAsync(await world.Client.GetAsync(world.BaseRoute, Ct))));

        await RemoveTheOrderMarkAsync(world);

        // La de flete nunca marca el pedido, así que nunca avisa.
        Assert.Equal([("Order", true), ("Freight", false)], MarkWarnings(await ReadAsync(await world.Client.GetAsync(world.BaseRoute, Ct))));
    }

    [Fact]
    public async Task TheStampResponseSaysWhenTheInvoiceWasNeverMarked()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await ReadAsync(await PostAsync(world));
        await RemoveTheOrderMarkAsync(world);
        world.Siigo.OnRequest($"/v1/invoices/{SiigoInvoiceGuid}/stamp", _ => FakeSiigoApi.Respond(
            HttpStatusCode.OK, """{"stamp":{"status":"Accepted","cufe":"cufe-1"}}"""));

        var row = await ReadAsync(await PostAsync(world, "/stamp?kind=Order"));

        Assert.Equal(("Stamped", true), (row.GetProperty("status").GetString(), row.GetProperty("orderMarkFailed").GetBoolean()));
    }

    // Mientras una tanda tiene el pedido, el worker todavía puede estar marcándolo: avisar sería un parpadeo.
    [Theory]
    [InlineData("Pending")]
    [InlineData("Sending")]
    public async Task TheOrderRowsDoNotWarnAboutTheMarkWhileABatchHoldsTheOrder(string itemStatus)
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await ReadAsync(await PostAsync(world));
        await RemoveTheOrderMarkAsync(world);
        await ReadAsync(await PostBatchAsync(world, world.Client, world.TenantId));
        await ExecuteAsync(
            world.ConnectionString,
            "UPDATE siigo.invoice_batch_items SET status = @status WHERE status = 'Pending'",
            ("status", itemStatus));

        var row = (await ReadAsync(await world.Client.GetAsync(world.BaseRoute, Ct)))[0];

        Assert.False(row.GetProperty("orderMarkFailed").GetBoolean());
    }

    // Igual que la recuperación de una tanda: si la marca no se puede leer no hay confirmación, así que se avisa y la lista sale entera.
    [Fact]
    public async Task TheOrderRowsWarnWhenTheMarkCannotBeRead()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await ReadAsync(await PostAsync(world));
        await ExecuteAsync(world.ConnectionString, "ALTER TABLE quotations.orders RENAME COLUMN invoice_number TO invoice_number_gone");

        var row = (await ReadAsync(await world.Client.GetAsync(world.BaseRoute, Ct)))[0];

        Assert.Equal(("Created", true), (row.GetProperty("status").GetString(), row.GetProperty("orderMarkFailed").GetBoolean()));
    }

    private static (string? Kind, bool OrderMarkFailed)[] MarkWarnings(JsonElement rows) =>
        [.. rows.EnumerateArray().Select(row => (row.GetProperty("kind").GetString(), row.GetProperty("orderMarkFailed").GetBoolean()))];

    // La factura sigue creada en Siigo y el pedido queda como si la marca nunca hubiera llegado.
    private static Task RemoveTheOrderMarkAsync(InvoicingWorld world) =>
        ExecuteAsync(
            world.ConnectionString,
            """
            UPDATE quotations.orders
            SET invoiced_at = NULL, invoiced_by = NULL, invoice_provider = NULL, invoice_number = NULL, invoice_external_id = NULL
            WHERE id = @id
            """,
            ("id", world.OrderId));

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

        await ExecuteAsync(world.ConnectionString, "UPDATE siigo.invoices SET sending_since = now() - interval '9 minutes'");
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
