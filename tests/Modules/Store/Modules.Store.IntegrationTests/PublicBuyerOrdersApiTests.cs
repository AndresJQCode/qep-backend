using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Buyers.Application;
using Modules.Quotations.Application;
using Npgsql;
using static Modules.Store.IntegrationTests.PublicStoreFixture;
using static Modules.Store.IntegrationTests.StoreApiHarness;

namespace Modules.Store.IntegrationTests;

/// <summary>
/// Los pedidos del comprador de punta a punta: la cuenta se registra, verifica e inicia sesión por las rutas reales, el pedido nace
/// por el checkout real y se enlaza a la cuenta por SQL, para que estas pruebas no dependan del sellado. Con la API real y Postgres.
/// Necesitan Docker.
/// </summary>
public sealed class PublicBuyerOrdersApiTests
{
    private const string Password = "una-contrasena-larga";
    private const string BuyerCancellationReason = "Cancelado por el comprador desde la tienda";

    // ---- Comprador ----------------------------------------------------------------------------------------------------------

    private static async Task<HttpResponseMessage> PostBuyerJsonAsync(BuyerOrdersRig rig, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url($"/buyer{path}")) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Qep-Visitor-Ip", BuyerOrdersRig.NextIp());
        return await rig.Fixture.Storefront.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Registra, verifica e inicia sesión por las rutas reales. Devuelve la cuenta y el token de sesión.</summary>
    private static async Task<(Guid AccountId, string Session)> SignInAsync(BuyerOrdersRig rig, string email)
    {
        var registered = await PostBuyerJsonAsync(
            rig, "/register", new { email, password = Password, acceptsDataPolicy = true, turnstileToken = "ok", profile = (object?)null });
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);

        var token = (await TextsAsync(
                rig,
                "SELECT payload::text FROM platform.outbox_messages WHERE event_name = @name ORDER BY occurred_at, id",
                ("name", BuyerIntegrationEvents.EmailVerificationRequested)))
            .Select(payload => JsonNode.Parse(payload)!)
            .Last(payload => (string?)payload["email"] == email)["token"]!.GetValue<string>();
        var verified = await PostBuyerJsonAsync(rig, "/email/verify", new { token });
        Assert.Equal(HttpStatusCode.NoContent, verified.StatusCode);

        var login = await PostBuyerJsonAsync(rig, "/session", new { email, password = Password });
        var json = await JsonAsync(login);
        Assert.True(login.StatusCode == HttpStatusCode.OK, json.ToJsonString());

        var accountId = Guid.Parse((await rig.TextAsync("SELECT id::text FROM buyers.accounts WHERE email = @email", ("email", email)))!);
        return (accountId, (string)json["sessionToken"]!);
    }

    private static async Task<HttpResponseMessage> BuyerGetAsync(
        BuyerOrdersRig rig, string path, string? session, bool withIp = true, string host = Host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url($"/buyer{path}", host));
        if (withIp)
        {
            request.Headers.Add("X-Qep-Visitor-Ip", BuyerOrdersRig.NextIp());
        }

        if (session is not null)
        {
            request.Headers.Add("X-Qep-Buyer-Session", session);
        }

        return await rig.Fixture.Storefront.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> BuyerPostAsync(BuyerOrdersRig rig, string path, string session)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url($"/buyer{path}"));
        request.Headers.Add("X-Qep-Visitor-Ip", BuyerOrdersRig.NextIp());
        request.Headers.Add("X-Qep-Buyer-Session", session);
        return await rig.Fixture.Storefront.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonNode> OkAsync(HttpResponseMessage response)
    {
        var json = await JsonAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.OK, json.ToJsonString());
        return json;
    }

    private static Task<HttpResponseMessage> CancelAsync(BuyerOrdersRig rig, Guid orderId, string session) =>
        BuyerPostAsync(rig, $"/orders/{orderId}/cancel", session);

    // ---- Pedidos ------------------------------------------------------------------------------------------------------------

    /// <summary>Un pedido por el checkout real (2 × Camiseta a 10.000 COP, Pending, PaymentPending), enlazado a la cuenta por SQL
    /// si se da una; sin cuenta queda como pedido de invitado.</summary>
    private static async Task<Guid> SeedBuyerOrderAsync(BuyerOrdersRig rig, Guid? accountId)
    {
        var orderId = (Guid)(await rig.CheckoutOkAsync())["id"]!;
        if (accountId is { } account)
        {
            await rig.ExecuteAsync(
                "UPDATE quotations.orders SET buyer_account_id = @account WHERE id = @order", ("account", account), ("order", orderId));
        }

        return orderId;
    }

    private static Task MarkPartiallyPaidAsync(BuyerOrdersRig rig, Guid orderId) =>
        rig.ExecuteAsync("UPDATE quotations.orders SET payment_status = 'PartialPaymentReceived' WHERE id = @order", ("order", orderId));

    /// <summary>Un archivo de comprobante siempre disponible: la subida real a R2 no existe en esta suite, y lo que se prueba es el pedido.</summary>
    private sealed class AvailableProofFileLookup : IQuotationFileLookup
    {
        public Task<QuotationFileRef?> FindAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken) =>
            Task.FromResult<QuotationFileRef?>(new QuotationFileRef(fileId, tenantId, "application/pdf", 1024, IsAvailable: true));

        public Task<string> CreateDownloadUrlAsync(Guid tenantId, Guid fileId, string downloadFileName, CancellationToken cancellationToken) =>
            Task.FromResult($"https://files.test/{fileId}");
    }

    private static Task<BuyerOrdersRig> OpenWithProofFilesAsync() =>
        BuyerOrdersRig.OpenAsync(configureServices: services =>
        {
            services.RemoveAll<IQuotationFileLookup>();
            services.AddSingleton<IQuotationFileLookup>(new AvailableProofFileLookup());
        });

    /// <summary>Registra un comprobante por la ruta real de la consola, dejando el pago en PaymentPending (el asesor lo está verificando).</summary>
    private static async Task AddProofKeepingPaymentPendingAsync(BuyerOrdersRig rig, Guid orderId)
    {
        using var console = CreateClient(rig.Factory, SubjectId, TenantId, "quotations.order.manage,quotations.order.read");
        var response = await console.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/orders/{orderId}/proofs",
            new
            {
                paymentStatus = "PaymentPending",
                paymentProofs = new[] { new { fileId = Guid.CreateVersion7(), amount = 5_000m } },
            },
            TestContext.Current.CancellationToken);
        var json = await JsonAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.OK, json.ToJsonString());
    }

    private static async Task ApproveAsync(BuyerOrdersRig rig, Guid orderId)
    {
        using var console = CreateClient(rig.Factory, SubjectId, TenantId, "quotations.order.approve,quotations.order.read");
        var response = await console.PostAsync(
            $"/api/v1/tenants/{TenantId}/orders/{orderId}/approve", content: null, TestContext.Current.CancellationToken);
        var json = await JsonAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.OK, json.ToJsonString());
    }

    // ---- Base ---------------------------------------------------------------------------------------------------------------

    private static string ConnectionString(BuyerOrdersRig rig) =>
        rig.Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;

    private static async Task<long> CountAsync(BuyerOrdersRig rig, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString(rig));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<IReadOnlyList<string>> TextsAsync(
        BuyerOrdersRig rig, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString(rig));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var texts = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            texts.Add(reader.GetString(0));
        }

        return texts;
    }

    private static Task<long> CancelledHistoryAsync(BuyerOrdersRig rig, Guid orderId) =>
        CountAsync(
            rig,
            "SELECT count(*) FROM quotations.order_history WHERE order_id = @id AND event_type = 'Cancelled' AND member_id IS NULL",
            ("id", orderId));

    private static Task<long> CancelledEventsAsync(BuyerOrdersRig rig, Guid orderId) =>
        CountAsync(
            rig,
            """
            SELECT count(*) FROM platform.outbox_messages
            WHERE event_name = 'quotations.order.cancelled.v1' AND correlation_id = @id AND payload->>'orderId' = @id
            """,
            ("id", orderId.ToString()));

    private static void AssertNoStore(HttpResponseMessage response) =>
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);

    // ---- Lista --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutASessionTheListIs401SessionInvalid()
    {
        await using var rig = await BuyerOrdersRig.OpenAsync();

        var response = await BuyerGetAsync(rig, "/orders", session: null);

        await BuyerOrdersRig.AssertCodeAsync(response, HttpStatusCode.Unauthorized, "store.buyer.session_invalid");
    }

    [Fact]
    public async Task WithoutTheVisitorIpTheListIs422VisitorIpRequired()
    {
        await using var rig = await BuyerOrdersRig.OpenAsync();
        var (_, session) = await SignInAsync(rig, "ana@test.co");

        var response = await BuyerGetAsync(rig, "/orders", session, withIp: false);

        await BuyerOrdersRig.AssertCodeAsync(response, HttpStatusCode.UnprocessableEntity, "store.public.visitor_ip_required");
    }

    [Fact]
    public async Task TheListIsPagedNewestFirstAndOnlyShowsTheBuyerOrders()
    {
        await using var rig = await BuyerOrdersRig.OpenAsync();
        var (account, session) = await SignInAsync(rig, "ana@test.co");
        var (other, _) = await SignInAsync(rig, "otro@test.co");
        var first = await SeedBuyerOrderAsync(rig, account);
        await SeedBuyerOrderAsync(rig, other);
        var second = await SeedBuyerOrderAsync(rig, account);
        await SeedBuyerOrderAsync(rig, accountId: null);
        var third = await SeedBuyerOrderAsync(rig, account);

        var pageOne = await OkAsync(await BuyerGetAsync(rig, "/orders?page=1&pageSize=2", session));
        var pageTwo = await OkAsync(await BuyerGetAsync(rig, "/orders?page=2&pageSize=2", session));

        Assert.Equal(3, (int)pageOne["total"]!);
        Assert.Equal(1, (int)pageOne["page"]!);
        Assert.Equal(2, (int)pageOne["pageSize"]!);
        var items = pageOne["items"]!.AsArray().Concat(pageTwo["items"]!.AsArray()).Select(item => item!).ToList();
        Assert.Equal(2, pageOne["items"]!.AsArray().Count);
        Assert.Single(pageTwo["items"]!.AsArray());
        Assert.Equal(3, (int)pageTwo["total"]!);
        Assert.Equal([third, second, first], items.Select(item => (Guid)item["id"]!));
        var createdAt = items.Select(item => (DateTimeOffset)item["createdAt"]!).ToList();
        Assert.Equal(createdAt.OrderByDescending(value => value), createdAt);
        foreach (var item in items)
        {
            var console = (await rig.OrderAsync((Guid)item["id"]!))["order"]!;
            Assert.Equal((string)console["orderNumber"]!, (string)item["number"]!);
            Assert.False(string.IsNullOrWhiteSpace((string)item["number"]!));
            Assert.Equal("Pending", (string)item["status"]!);
            Assert.Equal("PaymentPending", (string)item["paymentStatus"]!);
            Assert.Equal((decimal)console["total"]!, (decimal)item["total"]!);
            Assert.Equal(20_000m, (decimal)item["total"]!);
            Assert.Equal("COP", (string)item["currency"]!);
            Assert.Equal(1, (int)item["lineCount"]!);
            Assert.Equal("Camiseta", (string)item["firstLineName"]!);
            Assert.True((bool)item["canCancel"]!);
        }
    }

    [Fact]
    public async Task TheListReportsCanCancelPerOrder()
    {
        await using var rig = await BuyerOrdersRig.OpenAsync();
        var (account, session) = await SignInAsync(rig, "ana@test.co");
        var open = await SeedBuyerOrderAsync(rig, account);
        var paid = await SeedBuyerOrderAsync(rig, account);
        var approved = await SeedBuyerOrderAsync(rig, account);
        await MarkPartiallyPaidAsync(rig, paid);
        await ApproveAsync(rig, approved);

        var list = await OkAsync(await BuyerGetAsync(rig, "/orders", session));

        var items = list["items"]!.AsArray().ToDictionary(item => (Guid)item!["id"]!, item => item!);
        Assert.Equal(3, items.Count);
        Assert.True((bool)items[open]["canCancel"]!);
        Assert.False((bool)items[paid]["canCancel"]!);
        Assert.Equal("PartialPaymentReceived", (string)items[paid]["paymentStatus"]!);
        Assert.Equal("Pending", (string)items[paid]["status"]!);
        Assert.False((bool)items[approved]["canCancel"]!);
        Assert.NotEqual("Pending", (string)items[approved]["status"]!);
        foreach (var (id, item) in items)
        {
            var detail = await OkAsync(await BuyerGetAsync(rig, $"/orders/{id}", session));
            Assert.Equal((bool)item["canCancel"]!, (bool)detail["canCancel"]!);
            Assert.Equal((string)item["status"]!, (string)detail["status"]!);
            Assert.Equal((string)item["paymentStatus"]!, (string)detail["paymentStatus"]!);
            Assert.Equal((string)item["number"]!, (string)detail["number"]!);
            Assert.Equal((decimal)item["total"]!, (decimal)detail["total"]!);
        }
    }

    [Fact]
    public async Task ListRejectsAnOutOfRangePageSize()
    {
        await using var rig = await BuyerOrdersRig.OpenAsync();
        var (_, session) = await SignInAsync(rig, "ana@test.co");

        var tooBig = await BuyerGetAsync(rig, "/orders?pageSize=51", session);
        var pageZero = await BuyerGetAsync(rig, "/orders?page=0", session);

        var tooBigJson = await JsonAsync(tooBig);
        Assert.True(tooBig.StatusCode == HttpStatusCode.UnprocessableEntity, tooBigJson.ToJsonString());
        Assert.Equal("validation.failed", (string)tooBigJson["code"]!);
        Assert.True(tooBigJson["errors"]!.AsObject().ContainsKey("PageSize"), tooBigJson.ToJsonString());
        var pageZeroJson = await JsonAsync(pageZero);
        Assert.True(pageZero.StatusCode == HttpStatusCode.UnprocessableEntity, pageZeroJson.ToJsonString());
        Assert.Equal("validation.failed", (string)pageZeroJson["code"]!);
        Assert.True(pageZeroJson["errors"]!.AsObject().ContainsKey("Page"), pageZeroJson.ToJsonString());
    }

    // ---- Detalle ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheDetailCarriesLinesAndCanCancel()
    {
        await using var rig = await BuyerOrdersRig.OpenAsync();
        var (account, session) = await SignInAsync(rig, "ana@test.co");
        var orderId = await SeedBuyerOrderAsync(rig, account);

        var detail = await OkAsync(await BuyerGetAsync(rig, $"/orders/{orderId}", session));

        var consoleDetail = await rig.OrderAsync(orderId);
        var console = consoleDetail["order"]!;
        var consoleItem = Assert.Single(consoleDetail["items"]!.AsArray())!;
        var line = Assert.Single(detail["lines"]!.AsArray())!;
        Assert.Equal(orderId, (Guid)detail["id"]!);
        Assert.Equal("Camiseta", (string)line["name"]!);
        Assert.Equal(2m, (decimal)line["quantity"]!);
        Assert.Equal(10_000m, (decimal)line["unitPrice"]!);
        Assert.Equal((decimal)line["unitPrice"]! * (decimal)line["quantity"]!, (decimal)line["lineTotal"]!);
        Assert.Equal((decimal)consoleItem["subtotal"]! + (decimal)consoleItem["taxAmount"]!, (decimal)line["lineTotal"]!);
        Assert.Equal(1, (int)detail["lineCount"]!);
        Assert.Equal("Camiseta", (string)detail["firstLineName"]!);
        Assert.Equal((decimal)console["total"]!, (decimal)detail["total"]!);
        Assert.True((bool)detail["canCancel"]!);
        Assert.Null(detail["cancelledAt"]);
    }

    [Fact]
    public async Task AnotherBuyersOrderIs404OrderNotFound()
    {
        await using var rig = await BuyerOrdersRig.OpenAsync();
        var (_, session) = await SignInAsync(rig, "ana@test.co");
        var (other, _) = await SignInAsync(rig, "otro@test.co");
        var orderId = await SeedBuyerOrderAsync(rig, other);

        var detail = await BuyerGetAsync(rig, $"/orders/{orderId}", session);
        var cancel = await CancelAsync(rig, orderId, session);

        await BuyerOrdersRig.AssertCodeAsync(detail, HttpStatusCode.NotFound, "order.order.not_found");
        await BuyerOrdersRig.AssertCodeAsync(cancel, HttpStatusCode.NotFound, "order.order.not_found");
        Assert.Equal("Pending", (string)(await rig.OrderAsync(orderId))["order"]!["status"]!);
    }

    [Fact]
    public async Task AnOrderOfTheSameBuyerInAnotherTenantIs404()
    {
        await using var rig = await BuyerOrdersRig.OpenAsync();
        var (account, session) = await SignInAsync(rig, "ana@test.co");
        var orderId = await SeedBuyerOrderAsync(rig, account);
        await SeedTenantAsync(rig.Factory, OtherTenantId);
        await rig.ExecuteAsync(
            "UPDATE quotations.orders SET tenant_id = @other WHERE id = @order", ("other", Guid.Parse(OtherTenantId)), ("order", orderId));

        var list = await OkAsync(await BuyerGetAsync(rig, "/orders", session));
        var detail = await BuyerGetAsync(rig, $"/orders/{orderId}", session);
        var cancel = await CancelAsync(rig, orderId, session);

        Assert.Equal(0, (int)list["total"]!);
        Assert.Empty(list["items"]!.AsArray());
        await BuyerOrdersRig.AssertCodeAsync(detail, HttpStatusCode.NotFound, "order.order.not_found");
        await BuyerOrdersRig.AssertCodeAsync(cancel, HttpStatusCode.NotFound, "order.order.not_found");
        Assert.Equal(
            "Pending",
            await rig.TextAsync("SELECT status FROM quotations.orders WHERE id = @order", ("order", orderId)));
    }

    [Fact]
    public async Task AnUnknownHostIs404StorePublicNotFound()
    {
        await using var rig = await BuyerOrdersRig.OpenAsync();
        var (_, session) = await SignInAsync(rig, "ana@test.co");

        var response = await BuyerGetAsync(rig, "/orders", session, host: "no-existe.lulocrm.com");

        await BuyerOrdersRig.AssertCodeAsync(response, HttpStatusCode.NotFound, "store.public.not_found");
    }

    [Fact]
    public async Task ANonGuidOrderIdIs404ByRouting()
    {
        await using var rig = await BuyerOrdersRig.OpenAsync();
        var (_, session) = await SignInAsync(rig, "ana@test.co");

        var detail = await BuyerGetAsync(rig, "/orders/no-es-un-guid", session);
        var cancel = await BuyerPostAsync(rig, "/orders/no-es-un-guid/cancel", session);

        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
    }

    // ---- Anular -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CancelCancelsThePendingUnpaidOrder()
    {
        await using var rig = await BuyerOrdersRig.OpenAsync();
        var (account, session) = await SignInAsync(rig, "ana@test.co");
        var orderId = await SeedBuyerOrderAsync(rig, account);

        var cancelled = await OkAsync(await CancelAsync(rig, orderId, session));

        Assert.Equal(orderId, (Guid)cancelled["id"]!);
        Assert.Equal("Cancelled", (string)cancelled["status"]!);
        Assert.False((bool)cancelled["canCancel"]!);
        Assert.NotNull(cancelled["cancelledAt"]);
        Assert.Equal(1, await CancelledHistoryAsync(rig, orderId));
        Assert.Equal(1, await CancelledEventsAsync(rig, orderId));
        Assert.Equal(
            BuyerCancellationReason,
            await rig.TextAsync("SELECT cancellation_reason FROM quotations.orders WHERE id = @order", ("order", orderId)));
        Assert.Equal(
            1,
            await CountAsync(rig, "SELECT count(*) FROM quotations.orders WHERE id = @order AND cancelled_by IS NULL", ("order", orderId)));
        var detail = await OkAsync(await BuyerGetAsync(rig, $"/orders/{orderId}", session));
        Assert.Equal("Cancelled", (string)detail["status"]!);
        Assert.False((bool)detail["canCancel"]!);
    }

    [Fact]
    public async Task ASecondCancelIs409AndWritesNothing()
    {
        await using var rig = await BuyerOrdersRig.OpenAsync();
        var (account, session) = await SignInAsync(rig, "ana@test.co");
        var orderId = await SeedBuyerOrderAsync(rig, account);
        await OkAsync(await CancelAsync(rig, orderId, session));
        Assert.Equal(1, await CancelledHistoryAsync(rig, orderId));
        Assert.Equal(1, await CancelledEventsAsync(rig, orderId));
        var historyBefore = await CountAsync(rig, "SELECT count(*) FROM quotations.order_history WHERE order_id = @id", ("id", orderId));
        var outboxBefore = await CountAsync(rig, "SELECT count(*) FROM platform.outbox_messages WHERE correlation_id = @id", ("id", orderId.ToString()));

        var second = await CancelAsync(rig, orderId, session);

        await BuyerOrdersRig.AssertCodeAsync(second, HttpStatusCode.Conflict, "order.order.not_cancellable_by_buyer");
        Assert.Equal(1, await CancelledHistoryAsync(rig, orderId));
        Assert.Equal(1, await CancelledEventsAsync(rig, orderId));
        Assert.Equal(historyBefore, await CountAsync(rig, "SELECT count(*) FROM quotations.order_history WHERE order_id = @id", ("id", orderId)));
        Assert.Equal(
            outboxBefore,
            await CountAsync(rig, "SELECT count(*) FROM platform.outbox_messages WHERE correlation_id = @id", ("id", orderId.ToString())));
    }

    [Fact]
    public async Task APendingOrderWithPaymentIsNotCancellable()
    {
        await using var rig = await BuyerOrdersRig.OpenAsync();
        var (account, session) = await SignInAsync(rig, "ana@test.co");
        var orderId = await SeedBuyerOrderAsync(rig, account);
        await MarkPartiallyPaidAsync(rig, orderId);

        var response = await CancelAsync(rig, orderId, session);

        await BuyerOrdersRig.AssertCodeAsync(response, HttpStatusCode.Conflict, "order.order.not_cancellable_by_buyer");
        var detail = await OkAsync(await BuyerGetAsync(rig, $"/orders/{orderId}", session));
        Assert.Equal("Pending", (string)detail["status"]!);
        Assert.Equal("PartialPaymentReceived", (string)detail["paymentStatus"]!);
        Assert.False((bool)detail["canCancel"]!);
        Assert.Null(detail["cancelledAt"]);
        Assert.Equal(0, await CancelledHistoryAsync(rig, orderId));
        Assert.Equal(0, await CancelledEventsAsync(rig, orderId));
    }

    [Fact]
    public async Task AnOrderWithARegisteredProofIsNotCancellableEvenIfStillPaymentPending()
    {
        await using var rig = await OpenWithProofFilesAsync();
        var (account, session) = await SignInAsync(rig, "ana@test.co");
        var withProof = await SeedBuyerOrderAsync(rig, account);
        var untouched = await SeedBuyerOrderAsync(rig, account);
        await AddProofKeepingPaymentPendingAsync(rig, withProof);

        var response = await CancelAsync(rig, withProof, session);

        await BuyerOrdersRig.AssertCodeAsync(response, HttpStatusCode.Conflict, "order.order.not_cancellable_by_buyer");
        var detail = await OkAsync(await BuyerGetAsync(rig, $"/orders/{withProof}", session));
        Assert.Equal("Pending", (string)detail["status"]!);
        Assert.Equal("PaymentPending", (string)detail["paymentStatus"]!);
        Assert.False((bool)detail["canCancel"]!);
        Assert.Equal(0, await CancelledHistoryAsync(rig, withProof));
        Assert.Equal(0, await CancelledEventsAsync(rig, withProof));
        var list = await OkAsync(await BuyerGetAsync(rig, "/orders", session));
        var items = list["items"]!.AsArray().ToDictionary(item => (Guid)item!["id"]!, item => item!);
        Assert.False((bool)items[withProof]["canCancel"]!);
        Assert.True((bool)items[untouched]["canCancel"]!);
    }

    [Fact]
    public async Task AnApprovedOrderIsNotCancellable()
    {
        await using var rig = await BuyerOrdersRig.OpenAsync();
        var (account, session) = await SignInAsync(rig, "ana@test.co");
        var orderId = await SeedBuyerOrderAsync(rig, account);
        await ApproveAsync(rig, orderId);

        var response = await CancelAsync(rig, orderId, session);

        await BuyerOrdersRig.AssertCodeAsync(response, HttpStatusCode.Conflict, "order.order.not_cancellable_by_buyer");
        Assert.NotEqual("Cancelled", (string)(await rig.OrderAsync(orderId))["order"]!["status"]!);
        Assert.Equal(0, await CancelledHistoryAsync(rig, orderId));
    }

    [Fact]
    public async Task TheConsoleDetailShowsTheBuyerCancellation()
    {
        await using var rig = await BuyerOrdersRig.OpenAsync();
        var (account, session) = await SignInAsync(rig, "ana@test.co");
        var orderId = await SeedBuyerOrderAsync(rig, account);
        await OkAsync(await CancelAsync(rig, orderId, session));

        var console = await rig.OrderAsync(orderId);

        Assert.Equal("Cancelled", (string)console["order"]!["status"]!);
        Assert.Null(console["order"]!["cancelledBy"]);
        Assert.Equal(BuyerCancellationReason, (string)console["order"]!["cancellationReason"]!);
        var entry = Assert.Single(
            console["history"]!.AsArray(), entry => (string)entry!["eventType"]! == "Cancelled")!;
        Assert.Null(entry["memberId"]);
        Assert.Equal($"Anulado: {BuyerCancellationReason}", (string)entry["details"]!);
    }

    [Fact]
    public async Task EveryBuyerOrderResponseIsNoStore()
    {
        await using var rig = await BuyerOrdersRig.OpenAsync();
        var (account, session) = await SignInAsync(rig, "ana@test.co");
        var (other, _) = await SignInAsync(rig, "otro@test.co");
        var own = await SeedBuyerOrderAsync(rig, account);
        var foreign = await SeedBuyerOrderAsync(rig, other);
        await MarkPartiallyPaidAsync(rig, own);

        var list = await BuyerGetAsync(rig, "/orders", session);
        var detail = await BuyerGetAsync(rig, $"/orders/{own}", session);
        var notFound = await BuyerGetAsync(rig, $"/orders/{foreign}", session);
        var conflict = await CancelAsync(rig, own, session);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        AssertNoStore(list);
        AssertNoStore(detail);
        AssertNoStore(notFound);
        AssertNoStore(conflict);
    }
}
