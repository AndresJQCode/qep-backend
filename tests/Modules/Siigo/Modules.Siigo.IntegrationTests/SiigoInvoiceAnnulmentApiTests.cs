using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Siigo.Application;
using static Modules.Siigo.IntegrationTests.SiigoApiHarness;
using static Modules.Siigo.IntegrationTests.SiigoInvoicingHarness;

namespace Modules.Siigo.IntegrationTests;

/// <summary>Spec 2026-11-25: la factura que una persona anuló en Siigo deja de ser vigente y libera el pedido, de punta a punta con el
/// host entero y el doble <see cref="FakeSiigoApi"/>. Ninguna prueba llama a Siigo.</summary>
public sealed class SiigoInvoiceAnnulmentApiTests
{
    // El id de la factura del pedido que crea el doble por defecto (SiigoInvoicingHarness.ArrangeAsync).
    private const string OrderInvoiceId = "d1b6c0a2-77aa-4e5f-9a0c-0a1b2c3d4e5f";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Deja anuladas las facturas que existen en Siigo sin pasar por la acción: sólo el efecto que ven los lectores de la tabla.
    private static Task AnnulBySqlAsync(InvoicingWorld world) =>
        ExecuteAsync(
            world.ConnectionString,
            "UPDATE siigo.invoices SET status = 'Annulled', error_code = 'siigo.invoice.annulled_in_siigo' "
            + "WHERE status IN ('Created','Stamped','StampRejected')");

    private static Task RemoveTheOrderMarkAsync(InvoicingWorld world) =>
        ExecuteAsync(
            world.ConnectionString,
            """
            UPDATE quotations.orders
            SET invoiced_at = NULL, invoiced_by = NULL, invoice_provider = NULL, invoice_number = NULL, invoice_external_id = NULL
            WHERE id = @id
            """,
            ("id", world.OrderId));

    // El índice parcial y SiigoOpenInvoiceCheck filtran por el texto del estado: los dos tienen que dejar por fuera a la anulada.
    [Fact]
    public async Task AnAnnulledRowNoLongerBlocksTheManualMarkNorANewInvoice()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await IssueRowsThroughBatchAsync(world);
        await RemoveTheOrderMarkAsync(world);
        await AnnulBySqlAsync(world);

        var manual = await world.Client.PostAsync($"/api/v1/tenants/{world.TenantId}/orders/{world.OrderId}/invoice", null, Ct);

        Assert.True(manual.StatusCode == HttpStatusCode.OK, await manual.Content.ReadAsStringAsync(Ct));

        await using var otherDatabase = await StartDatabaseAsync();
        using var other = await ArrangeAsync(otherDatabase);
        await IssueRowsThroughBatchAsync(other);
        await RemoveTheOrderMarkAsync(other);
        await AnnulBySqlAsync(other);
        other.Siigo.OnRequest("/v1/invoices", _ => Created("FV-1-124", "id-2", 0m));

        var rows = await IssueRowsThroughBatchAsync(other);

        var row = rows[0];
        Assert.Equal(("Created", "FV-1-124"), (row.GetProperty("status").GetString(), row.GetProperty("number").GetString()));
        Assert.Equal(2, await ScalarAsync<long>(other.ConnectionString, "SELECT count(*) FROM siigo.invoices WHERE kind = 'Order'"));
        Assert.Equal(
            1,
            await ScalarAsync<long>(
                other.ConnectionString, "SELECT count(*) FROM siigo.invoices WHERE kind = 'Order' AND status = 'Annulled'"));
    }

    // --- POST …/siigo-invoice/annulment (spec §3) ---

    [Fact]
    public async Task AnInvoiceAnnulledInSiigoReleasesTheOrderAndItCanBeInvoicedAgain()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await IssueRowsThroughBatchAsync(world);
        AnnulledInSiigo(world, OrderInvoiceId);

        var rows = await ReadAsync(await PostAnnulmentAsync(world));

        var row = Assert.Single(rows.EnumerateArray());
        Assert.Equal(
            ("Order", "Annulled", "FV-1-123", "siigo.invoice.annulled_in_siigo"),
            (Text(row, "kind"), Text(row, "status"), Text(row, "number"), Text(row, "errorCode")));
        var order = await GetOrderAsync(world);
        Assert.False(order.GetProperty("invoiced").GetBoolean());
        Assert.Equal(JsonValueKind.Null, order.GetProperty("invoiceProvider").ValueKind);
        Assert.Equal(
            "annulled|Created",
            await ScalarAsync<string>(
                world.ConnectionString,
                """
                SELECT concat(changed_fields ->> 'outcome', '|', changed_fields ->> 'fromStatus')
                FROM audit.entries WHERE action = 'siigo.invoice.annulled'
                """));
        await WaitForTheRevertedAuditAsync(world);

        world.Siigo.OnRequest("/v1/invoices", _ => Created("FV-1-124", "id-2", 0m));
        var again = (await IssueRowsThroughBatchAsync(world))[0];

        Assert.Equal(("Created", "FV-1-124"), (Text(again, "status"), Text(again, "number")));
        Assert.Equal(
            2,
            await ScalarAsync<long>(world.ConnectionString, "SELECT count(DISTINCT idempotency_key) FROM siigo.invoices"));
    }

    // Review Focus 2: todo o nada. Con la de flete viva en Siigo, tampoco se suelta la del pedido.
    [Fact]
    public async Task WithTheFreightStillLiveInSiigoNothingChanges()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database, freightSeparate: true, freight: true);
        world.Siigo.AddProduct("p-f", "FLETE", "Flete");
        var calls = 0;
        world.Siigo.OnRequest("/v1/invoices", _ => Interlocked.Increment(ref calls) == 1
            ? Created("FV-1-123", OrderInvoiceId, 0m)
            : Created("FV-1-124", "id-f", 0m));
        await IssueRowsThroughBatchAsync(world);
        AnnulledInSiigo(world, OrderInvoiceId);
        NotAnnulledInSiigo(world, "id-f");

        var response = await PostAnnulmentAsync(world);

        await AssertRejectedAnnulmentAsync(response, "siigo.invoice.not_annulled", "Freight", "FV-1-124");
        Assert.Equal("Order:Created,Freight:Created", await StatusesAsync(world));
        await AssertStillMarkedAsync(world);

        AnnulledInSiigo(world, "id-f");
        var rows = await ReadAsync(await PostAnnulmentAsync(world));

        Assert.Equal(
            ["Order:Annulled", "Freight:Annulled"],
            rows.EnumerateArray().Select(row => $"{Text(row, "kind")}:{Text(row, "status")}").ToList());
        Assert.False((await GetOrderAsync(world)).GetProperty("invoiced").GetBoolean());
        Assert.Equal(
            2,
            await ScalarAsync<long>(world.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'siigo.invoice.annulled'"));
    }

    [Fact]
    public async Task NotAnnulledInSiigoIs422AndChangesNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await IssueRowsThroughBatchAsync(world);
        NotAnnulledInSiigo(world, OrderInvoiceId);

        var response = await PostAnnulmentAsync(world);

        await AssertRejectedAnnulmentAsync(response, "siigo.invoice.not_annulled", "Order", "FV-1-123");
        await AssertNothingChangedAsync(world);
    }

    // Review Focus 1: un 404 pelado también es una ruta mal armada; nunca libera el pedido y queda la evidencia.
    [Fact]
    public async Task ABare404FromSiigoReleasesNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await IssueRowsThroughBatchAsync(world);
        world.Siigo.On($"/v1/invoices/{OrderInvoiceId}", HttpStatusCode.NotFound, string.Empty);

        var response = await PostAnnulmentAsync(world);

        await AssertRejectedAnnulmentAsync(response, "siigo.invoice.annulment_unclear", "Order", "FV-1-123");
        await AssertNothingChangedAsync(world);
        Assert.Equal(
            1,
            await ScalarAsync<long>(world.ConnectionString, "SELECT count(*) FROM siigo.call_failures WHERE operation = 'InvoiceRead'"));
    }

    [Fact]
    public async Task AnAnnulledButDianAcceptedAnswerIsUnclear()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await IssueRowsThroughBatchAsync(world);
        world.Siigo.On(
            $"/v1/invoices/{OrderInvoiceId}",
            HttpStatusCode.OK,
            $$$"""{"id":"{{{OrderInvoiceId}}}","annulled":true,"stamp":{"status":"Accepted"}}""");

        var response = await PostAnnulmentAsync(world);

        await AssertRejectedAnnulmentAsync(response, "siigo.invoice.annulment_unclear", "Order", "FV-1-123");
        await AssertNothingChangedAsync(world);
    }

    [Fact]
    public async Task AnInvoiceResolvedByHandOrStillPendingCannotBeVerified()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.OnRequest("/v1/invoices", _ => FakeSiigoApi.Respond(HttpStatusCode.ServiceUnavailable, "{}"));
        await IssueRowsThroughBatchAsync(world);

        var pending = await PostAnnulmentAsync(world);

        await AssertRejectedAnnulmentAsync(pending, "siigo.invoice.annulment_unverifiable", "Order", null);
        Assert.Equal("Order:Pending", await StatusesAsync(world));
        Assert.Equal(0, InvoiceReads(world));

        // Resuelta a mano como existente: queda Created y con número, pero sin el id de Siigo con que se consulta.
        await ExecuteAsync(world.ConnectionString, "UPDATE siigo.invoices SET created_at = created_at - interval '2 days'");
        await ReadAsync(await world.Client.PostAsJsonAsync(world.BaseRoute + "/resolve", new { outcome = "exists", number = "FV-1-777" }, Ct));

        var byHand = await PostAnnulmentAsync(world);

        await AssertRejectedAnnulmentAsync(byHand, "siigo.invoice.annulment_unverifiable", "Order", "FV-1-777");
        Assert.Equal("Order:Created", await StatusesAsync(world));
        Assert.True((await GetOrderAsync(world)).GetProperty("invoiced").GetBoolean());
        Assert.Equal(0, InvoiceReads(world));
    }

    [Fact]
    public async Task AnOrderInAnOpenBatchIs409()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await IssueRowsThroughBatchAsync(world);
        AnnulledInSiigo(world, OrderInvoiceId);
        await ExecuteAsync(
            world.ConnectionString,
            "UPDATE siigo.invoice_batch_items SET status = 'Pending', available_at = now() + interval '1 hour'");

        var response = await PostAnnulmentAsync(world);

        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, text);
        using var problem = JsonDocument.Parse(text);
        Assert.Equal("siigo.invoice_batch.order_in_progress", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            world.OrderId,
            Assert.Single(problem.RootElement.GetProperty("details").GetProperty("orderIds").EnumerateArray()).GetGuid());
        await AssertNothingChangedAsync(world);
        Assert.Equal(0, InvoiceReads(world));
    }

    [Fact]
    public async Task SiigoDownIs503AndChangesNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await IssueRowsThroughBatchAsync(world);
        world.Siigo.On($"/v1/invoices/{OrderInvoiceId}", HttpStatusCode.ServiceUnavailable, "{}");

        var response = await PostAnnulmentAsync(world);

        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "siigo.unavailable");
        await AssertNothingChangedAsync(world);
    }

    // Review Focus 3: doble pulsación o repetir después del éxito.
    [Fact]
    public async Task RepeatingAfterSuccessIsIdempotentAndDoesNotCallSiigo()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await IssueRowsThroughBatchAsync(world);
        AnnulledInSiigo(world, OrderInvoiceId);

        var first = await ReadAsync(await PostAnnulmentAsync(world));
        var second = await ReadAsync(await PostAnnulmentAsync(world));

        var expected = Assert.Single(first.EnumerateArray());
        var repeated = Assert.Single(second.EnumerateArray());
        Assert.Equal(expected.GetProperty("id").GetGuid(), repeated.GetProperty("id").GetGuid());
        Assert.Equal("Annulled", Text(repeated, "status"));
        Assert.Equal(1, world.Siigo.CallsTo($"/v1/invoices/{OrderInvoiceId}"));
        Assert.Equal(
            1,
            await ScalarAsync<long>(world.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'siigo.invoice.annulled'"));
        Assert.Equal(1, await RevertedEventsAsync(world));
    }

    [Fact]
    public async Task NothingToAnnulIs422AndNoRowsIs404()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);

        await AssertProblemAsync(await PostAnnulmentAsync(world), HttpStatusCode.NotFound, "siigo.invoice.not_found");

        world.Siigo.OnRequest("/v1/invoices", _ => FakeSiigoApi.Respond(
            HttpStatusCode.BadRequest, """{"Errors":[{"Code":"invalid_total_payments","Message":"El total de pagos debe ser 288490.5"}]}"""));
        await IssueThroughBatchAsync(world);

        await AssertProblemAsync(
            await PostAnnulmentAsync(world), HttpStatusCode.UnprocessableEntity, "siigo.invoice.state_invalid");
        Assert.Equal("Order:Rejected", await StatusesAsync(world));
        Assert.Equal(0, InvoiceReads(world));
    }

    // Una marca manual puesta en medio es una declaración de la persona: la factura queda anulada y el pedido sigue marcado.
    [Fact]
    public async Task AManualMarkPutInBetweenIsNotTouched()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await IssueRowsThroughBatchAsync(world);
        AnnulledInSiigo(world, OrderInvoiceId);
        await ExecuteAsync(
            world.ConnectionString,
            "UPDATE quotations.orders SET invoice_provider = NULL, invoice_number = NULL, invoice_external_id = NULL WHERE id = @id",
            ("id", world.OrderId));

        var rows = await ReadAsync(await PostAnnulmentAsync(world));

        Assert.Equal("Annulled", Text(rows[0], "status"));
        Assert.True((await GetOrderAsync(world)).GetProperty("invoiced").GetBoolean());
        Assert.Equal(0, await RevertedEventsAsync(world));
    }

    [Fact]
    public async Task WithoutThePermissionItIs403()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await IssueRowsThroughBatchAsync(world);
        AnnulledInSiigo(world, OrderInvoiceId);
        using var client = CreateClient(
            world.Factory,
            world.Tenant.OwnerUserId,
            world.TenantId,
            [.. InvoicerPermissions.Where(permission => permission != SiigoPermissions.InvoiceIssue)]);

        var response = await client.PostAsync(world.BaseRoute + "/annulment", null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNothingChangedAsync(world);
        Assert.Equal(0, InvoiceReads(world));
    }

    // --- Ayudas ---

    private static void AnnulledInSiigo(InvoicingWorld world, string id) =>
        world.Siigo.On(
            $"/v1/invoices/{id}", HttpStatusCode.OK, $$$"""{"id":"{{{id}}}","annulled":true,"stamp":{"status":"Draft"}}""");

    private static void NotAnnulledInSiigo(InvoicingWorld world, string id) =>
        world.Siigo.On(
            $"/v1/invoices/{id}", HttpStatusCode.OK, $$$"""{"id":"{{{id}}}","annulled":false,"stamp":{"status":"Draft"}}""");

    private static Task<HttpResponseMessage> PostAnnulmentAsync(InvoicingWorld world) =>
        world.Client.PostAsync(world.BaseRoute + "/annulment", null, Ct);

    private static string? Text(JsonElement element, string name) => element.GetProperty(name).GetString();

    // Las consultas de una factura a Siigo (GET /v1/invoices/{id}); la creación es un POST a /v1/invoices.
    private static int InvoiceReads(InvoicingWorld world) =>
        world.Siigo.Calls.Count(call => call.Method == HttpMethod.Get && call.Path.StartsWith("/v1/invoices/", StringComparison.Ordinal));

    private static Task<string> StatusesAsync(InvoicingWorld world) =>
        ScalarAsync<string>(
            world.ConnectionString,
            "SELECT string_agg(concat(kind, ':', status), ',' ORDER BY kind DESC, created_at) FROM siigo.invoices");

    // El evento sale por el Outbox en la misma transacción que el cambio del pedido: contarlo ahí no depende del proyector.
    private static Task<long> RevertedEventsAsync(InvoicingWorld world) =>
        ScalarAsync<long>(
            world.ConnectionString,
            "SELECT count(*) FROM platform.outbox_messages WHERE payload ->> 'action' = 'quotation.order.invoice_reverted'");

    // La entrada de Quotations llega a audit.entries por el proyector del Outbox (AuditProjectionWorker, cada 15 s).
    private static async Task WaitForTheRevertedAuditAsync(InvoicingWorld world)
    {
        for (var attempt = 0; attempt < 180; attempt++)
        {
            var entries = await ScalarAsync<long>(
                world.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'quotation.order.invoice_reverted'");
            if (entries == 1)
            {
                return;
            }

            await Task.Delay(250, Ct);
        }

        Assert.Fail("The reverted invoicing never reached audit.entries.");
    }

    private static async Task AssertStillMarkedAsync(InvoicingWorld world)
    {
        var order = await GetOrderAsync(world);
        Assert.True(order.GetProperty("invoiced").GetBoolean());
        Assert.Equal(("siigo", "FV-1-123"), (Text(order, "invoiceProvider"), Text(order, "invoiceNumber")));
    }

    // Un pedido con una sola factura creada: la fila, la marca y la auditoría siguen como estaban.
    private static async Task AssertNothingChangedAsync(InvoicingWorld world)
    {
        Assert.Equal("Order:Created", await StatusesAsync(world));
        await AssertStillMarkedAsync(world);
        Assert.Equal(
            0,
            await ScalarAsync<long>(world.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'siigo.invoice.annulled'"));
        Assert.Equal(0, await RevertedEventsAsync(world));
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {text}");
        using var problem = JsonDocument.Parse(text);
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }

    private static async Task AssertRejectedAnnulmentAsync(HttpResponseMessage response, string code, string kind, string? number)
    {
        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, code);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var details = problem.RootElement.GetProperty("details");
        Assert.Equal((kind, number), (details.GetProperty("kind").GetString(), details.GetProperty("number").GetString()));
    }
}
