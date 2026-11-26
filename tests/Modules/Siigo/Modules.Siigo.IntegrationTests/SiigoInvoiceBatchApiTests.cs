using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Siigo.Application;
using Modules.Siigo.Application.Invoicing;
using Modules.Siigo.Infrastructure.Batches;
using Npgsql;
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

    private static SiigoInvoiceBatchWorker Worker(InvoicingWorld world) =>
        world.Factory.Services.GetServices<IHostedService>().OfType<SiigoInvoiceBatchWorker>().Single();

    private static Task<TimeSpan?> DrainAsync(InvoicingWorld world) => Worker(world).DrainAsync(Ct);

    private static async Task<Guid> CreateBatchAsync(InvoicingWorld world, params Guid[] orderIds) =>
        (await ReadAsync(await PostBatchAsync(world, orderIds))).GetProperty("id").GetGuid();

    private static (string? OrderNumber, string? CustomerName) Label(JsonElement item) =>
        (item.GetProperty("orderNumber").GetString(), item.GetProperty("customerName").GetString());

    private static string[] SiigoErrors(JsonElement item) =>
        [.. item.GetProperty("siigoErrors").EnumerateArray().Select(code => code.GetString()!)];

    private static string[] Counts(JsonElement batch) =>
        [.. batch.GetProperty("statusCounts").EnumerateArray()
            .Select(count => $"{count.GetProperty("status").GetString()}={count.GetProperty("count").GetInt32()}")];

    private static (int Total, int Page, int PageSize) PageOf(JsonElement page) =>
        (page.GetProperty("total").GetInt32(), page.GetProperty("page").GetInt32(), page.GetProperty("pageSize").GetInt32());

    private static Guid[] Ids(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(summary => summary.GetProperty("id").GetGuid())];

    // Un segundo cliente en la misma base, con Siigo prendido y su owner con permiso de facturar.
    private static async Task<(Guid TenantId, HttpClient Client)> OtherTenantAsync(InvoicingWorld world)
    {
        var other = await RegisterTenantAsync(world.Factory);
        await EnableSiigoAsync(world.ConnectionString, other.TenantId);
        return (other.TenantId, CreateClient(world.Factory, other.OwnerUserId, other.TenantId, SiigoPermissions.InvoiceIssue));
    }

    // Un worker lo reclamó y murió sin guardar nada: el lease ya venció.
    private static Task AbandonTheItemAsync(InvoicingWorld world) =>
        ExecuteAsync(
            world.ConnectionString,
            "UPDATE siigo.invoice_batch_items SET status = 'Sending', attempts = 1, lease_until = now() - interval '1 minute'");

    private static Task<Guid> StoredInvoiceIdAsync(InvoicingWorld world) =>
        ScalarAsync<Guid>(world.ConnectionString, "SELECT id FROM siigo.invoices");

    private sealed record StoredItem(
        string Status,
        string? ErrorCode,
        Guid? InvoiceId,
        string? InvoiceNumber,
        bool OrderMarkFailed,
        bool LeaseReleased,
        bool BatchFinished);

    // Lo que quedó guardado del único pedido de la única tanda (no el estado efectivo que devuelve la API).
    private static async Task<StoredItem> StoredItemAsync(InvoicingWorld world)
    {
        await using var connection = new NpgsqlConnection(world.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT i.status, i.error_code, i.invoice_id, i.invoice_number, i.order_mark_failed, i.lease_until IS NULL,
                   b.finished_at IS NOT NULL
            FROM siigo.invoice_batch_items i
            JOIN siigo.invoice_batches b ON b.id = i.batch_id
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        var item = new StoredItem(
            reader.GetString(0),
            await reader.IsDBNullAsync(1, Ct) ? null : reader.GetString(1),
            await reader.IsDBNullAsync(2, Ct) ? null : reader.GetGuid(2),
            await reader.IsDBNullAsync(3, Ct) ? null : reader.GetString(3),
            reader.GetBoolean(4),
            reader.GetBoolean(5),
            reader.GetBoolean(6));
        Assert.False(await reader.ReadAsync(Ct));
        return item;
    }

    private static JsonElement Item(JsonElement batch, int position) => batch.GetProperty("items")[position];

    private static Task<HttpResponseMessage> PostBatchWithPaymentsAsync(InvoicingWorld world, object[] payments, params Guid[] orderIds) =>
        world.Client.PostAsJsonAsync(BatchesRoute(world), new { orderIds, payments }, Ct);

    private static object Payment(Guid proofId, string? siigoPaymentTypeId = "5101") => new { kind = "Order", proofId, siigoPaymentTypeId };

    private static async Task<JsonElement> ReadValidationErrorsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());
        return problem.GetProperty("errors");
    }

    private static async Task AssertOrderInProgressAsync(HttpResponseMessage response, params Guid[] orderIds)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{(int)response.StatusCode}: {text}");
        using var problem = JsonDocument.Parse(text);
        Assert.Equal("siigo.invoice_batch.order_in_progress", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            orderIds,
            problem.RootElement.GetProperty("details").GetProperty("orderIds").EnumerateArray().Select(value => value.GetGuid()));
    }

    private static Task<long> BatchCountAsync(InvoicingWorld world) =>
        ScalarAsync<long>(world.ConnectionString, "SELECT count(*) FROM siigo.invoice_batches");

    // Spec 2026-11-24 §1: las formas de pago elegidas viajan en el ítem y llegan al caso de uso de siempre; un comprobante que no es del
    // pedido lo salta sin llamar a Siigo (sin `payments` el mismo pedido se factura: ABatchIsAccepted…).
    [Fact]
    public async Task ABatchWithPaymentsIsAcceptedAndThePaymentsReachTheIssue()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);

        var response = await PostBatchWithPaymentsAsync(world, [Payment(Guid.NewGuid())], world.OrderId);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        await DrainAsync(world);

        var batch = await GetBatchAsync(world, created.GetProperty("id").GetGuid());
        Assert.Equal(
            ("Skipped", "siigo.invoice.payments_mismatch"),
            (Item(batch, 0).GetProperty("status").GetString(), Item(batch, 0).GetProperty("errorCode").GetString()));
        Assert.Empty(InvoiceCalls(world.Siigo));
    }

    [Fact]
    public async Task PaymentsWithTwoOrdersIs422OnPayments()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);

        var twoOrders = await PostBatchWithPaymentsAsync(world, [Payment(Guid.NewGuid())], world.OrderId, Guid.NewGuid());
        var withoutType = await PostBatchWithPaymentsAsync(world, [Payment(Guid.NewGuid(), null)], world.OrderId);

        Assert.True((await ReadValidationErrorsAsync(twoOrders)).TryGetProperty("payments", out _));
        // Las mismas claves por elemento que el comando de emisión.
        Assert.True((await ReadValidationErrorsAsync(withoutType)).TryGetProperty("Payments[0].siigoPaymentTypeId", out _));
        Assert.Equal(0, await BatchCountAsync(world));
    }

    // Un elemento nulo llegaba al mapeo antes de validar y salía como 500.
    [Fact]
    public async Task ANullPaymentIs422OnItsElement()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);

        var batch = await PostBatchWithPaymentsAsync(world, [null!], world.OrderId);

        Assert.True((await ReadValidationErrorsAsync(batch)).TryGetProperty("Payments[0]", out _));
        Assert.Equal(0, await BatchCountAsync(world));
        Assert.Empty(InvoiceCalls(world.Siigo));
    }

    [Fact]
    public async Task AnOrderAlreadyInAnOpenBatchIs409WithItsIds()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await ReadAsync(await PostBatchAsync(world, world.OrderId));

        var second = await PostBatchAsync(world, Guid.NewGuid(), world.OrderId);

        await AssertOrderInProgressAsync(second, world.OrderId);
        Assert.Equal(1, await BatchCountAsync(world));
        await DrainAsync(world);
        Assert.Equal(HttpStatusCode.Accepted, (await PostBatchAsync(world, world.OrderId)).StatusCode);
    }

    [Fact]
    public async Task TwoSimultaneousBatchesOfTheSameOrderCreateOne()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);

        var responses = await Task.WhenAll(PostBatchAsync(world, world.OrderId), PostBatchAsync(world, world.OrderId));

        var accepted = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Accepted);
        await AssertOrderInProgressAsync(Assert.Single(responses, response => response != accepted), world.OrderId);
        Assert.Equal(1, await BatchCountAsync(world));
    }

    // El mismo choque cuando la otra tanda todavía no ha confirmado: la consulta previa no la ve, el INSERT espera en el índice único y
    // el 23505 sale como el mismo 409 con sus ids.
    [Fact]
    public async Task TwoSimultaneousBatchesThatCollideAtTheIndexGetTheSame409()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await using var connection = new NpgsqlConnection(world.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO siigo.invoice_batches (id, tenant_id, requested_by, created_at)
            VALUES (@batch, @tenant, gen_random_uuid(), now());
            INSERT INTO siigo.invoice_batch_items
                (id, batch_id, tenant_id, order_id, position, status, missing_codes, attempts, available_at, updated_at)
            VALUES (gen_random_uuid(), @batch, @tenant, @order, 0, 'Pending', '', 0, now() + interval '1 hour', now());
            """,
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("batch", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("tenant", world.TenantId);
            insert.Parameters.AddWithValue("order", world.OrderId);
            await insert.ExecuteNonQueryAsync(Ct);
        }

        var post = PostBatchAsync(world, world.OrderId);
        await WaitUntilAQueryWaitsForALockAsync(world);
        await transaction.CommitAsync(Ct);

        await AssertOrderInProgressAsync(await post, world.OrderId);
        Assert.Equal(1, await BatchCountAsync(world));
    }

    private static async Task WaitUntilAQueryWaitsForALockAsync(InvoicingWorld world)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var waiting = await ScalarAsync<long>(
                world.ConnectionString,
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'");
            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(50, Ct);
        }

        Assert.Fail("The second batch never waited on the open order index.");
    }

    [Fact]
    public async Task TheBatchRequestAuditSaysWhetherItCarriedPayments()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);

        await ReadAsync(await PostBatchAsync(world, world.OrderId));
        await DrainAsync(world);
        await ReadAsync(await PostBatchWithPaymentsAsync(world, [Payment(Guid.NewGuid())], world.OrderId));

        Assert.Equal(
            "false,true",
            await ScalarAsync<string>(
                world.ConnectionString,
                """
                SELECT string_agg(changed_fields ->> 'withPayments', ',' ORDER BY occurred_at)
                FROM audit.entries WHERE action = 'siigo.invoice_batch.requested'
                """));
    }

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
    public async Task AFirstAttemptDataRejectionFailsWithTheInvoiceId()
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
        var item = await StoredItemAsync(world);
        Assert.Equal(("Failed", await StoredInvoiceIdAsync(world)), (item.Status, item.InvoiceId));
        Assert.Equal(item.InvoiceId, Item(batch.RootElement, 0).GetProperty("invoiceId").GetGuid());
        Assert.Equal(["invalid_item"], SiigoErrors(Item(batch.RootElement, 0)));
    }

    // La factura existe pero el pedido quedó marcado a mano en medio: el ítem es Invoiced y lo avisa.
    [Fact]
    public async Task AnInvoicedItemSaysWhenTheOrderCouldNotBeMarked()
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
        await ReadAsync(await PostBatchAsync(world, world.OrderId));

        await DrainAsync(world);

        var item = await StoredItemAsync(world);
        Assert.Equal(
            ("Invoiced", "FV-1-123", await StoredInvoiceIdAsync(world), true),
            (item.Status, item.InvoiceNumber, item.InvoiceId, item.OrderMarkFailed));
    }

    // Spec frontend 2026-11-24, H-1: «Generar» es una tanda de ese pedido. La factura ya existe y sólo falta la marca: la tanda se acepta,
    // marca el pedido y no vuelve a llamar a Siigo. Es el único camino para completar la marca.
    [Fact]
    public async Task ABatchOfAnOrderWhoseInvoiceWasNotMarkedMarksItWithoutCallingSiigo()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        // Sólo el primer envío llega a Siigo (se comprueba al final): alguien marca el pedido a mano en medio y la marca de Siigo falla.
        world.Siigo.OnRequest("/v1/invoices", _ =>
        {
            ExecuteAsync(
                world.ConnectionString,
                "UPDATE quotations.orders SET invoiced_at = now(), invoiced_by = @by WHERE id = @id",
                ("by", Guid.CreateVersion7()), ("id", world.OrderId)).GetAwaiter().GetResult();
            return Created("FV-1-123", "id-1", 0m);
        });
        var firstId = await CreateBatchAsync(world, world.OrderId);
        await DrainAsync(world);
        Assert.True(Item(await GetBatchAsync(world, firstId), 0).GetProperty("orderMarkFailed").GetBoolean());

        // Se quita la marca manual: la factura sigue creada y el pedido, sin marcar.
        await ExecuteAsync(
            world.ConnectionString,
            "UPDATE quotations.orders SET invoiced_at = NULL, invoiced_by = NULL WHERE id = @id",
            ("id", world.OrderId));
        Assert.False((await GetOrderAsync(world)).GetProperty("invoiced").GetBoolean());

        var response = await PostBatchAsync(world, world.OrderId);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var secondId = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        await DrainAsync(world);

        var item = Item(await GetBatchAsync(world, secondId), 0);
        Assert.Equal(
            ("Invoiced", "FV-1-123", false),
            (item.GetProperty("status").GetString(), item.GetProperty("invoiceNumber").GetString(), item.GetProperty("orderMarkFailed").GetBoolean()));
        var order = await GetOrderAsync(world);
        Assert.Equal(
            (true, "FV-1-123"),
            (order.GetProperty("invoiced").GetBoolean(), order.GetProperty("invoiceNumber").GetString()));
        Assert.Single(InvoiceCalls(world.Siigo));
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
    public async Task ASiigoOutageLeavesTheOrderToConfirmAndNothingIsScheduledForLater()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.OnRequest("/v1/invoices", _ => FakeSiigoApi.Respond(HttpStatusCode.ServiceUnavailable, "{}"));
        await ReadAsync(await PostBatchAsync(world, world.OrderId));

        await DrainAsync(world);
        await DrainAsync(world);

        var item = await StoredItemAsync(world);
        Assert.Equal(
            ("ToConfirm", "siigo.invoice.unavailable", await StoredInvoiceIdAsync(world)),
            (item.Status, item.ErrorCode, item.InvoiceId));
        Assert.True(item.BatchFinished);
        Assert.Single(InvoiceCalls(world.Siigo));
    }

    // Un proceso que murió antes de guardar la fila de la factura: no alcanzó a salir, y no se manda más tarde sin que alguien lo pida.
    [Fact]
    public async Task AnOrderCutShortBeforeItsInvoiceExistedIsFailedAsInterrupted()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await ReadAsync(await PostBatchAsync(world, world.OrderId));
        await AbandonTheItemAsync(world);

        await DrainAsync(world);

        var item = await StoredItemAsync(world);
        Assert.Equal(("Failed", "siigo.invoice.interrupted", (Guid?)null), (item.Status, item.ErrorCode, item.InvoiceId));
        Assert.True(item.BatchFinished);
        Assert.Empty(InvoiceCalls(world.Siigo));
    }

    // El ítem recuperado (lease vencido) toma el estado de la fila del pedido y nunca llama a Siigo (spec 2026-11-24 §4).
    [Fact]
    public async Task ARecoveredItemTakesThePendingInvoiceAndNeverCallsSiigo()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.OnRequest("/v1/invoices", _ => FakeSiigoApi.Respond(HttpStatusCode.ServiceUnavailable, "{}"));
        Assert.IsType<SiigoBatchItemOutcome.ToConfirm>(await IssueWithoutBatchAsync(world));
        var batchId = await CreateBatchAsync(world, world.OrderId);
        await AbandonTheItemAsync(world);

        await DrainAsync(world);

        var item = await StoredItemAsync(world);
        Assert.Equal(
            ("ToConfirm", "siigo.invoice.unavailable", await StoredInvoiceIdAsync(world)),
            (item.Status, item.ErrorCode, item.InvoiceId));
        Assert.Equal(item.InvoiceId, Item(await GetBatchAsync(world, batchId), 0).GetProperty("invoiceId").GetGuid());
        Assert.Single(InvoiceCalls(world.Siigo));
    }

    [Fact]
    public async Task ARecoveredItemWhoseInvoiceWasCreatedIsInvoiced()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        Assert.IsType<SiigoBatchItemOutcome.Invoiced>(await IssueWithoutBatchAsync(world));
        var batchId = await CreateBatchAsync(world, world.OrderId);
        await AbandonTheItemAsync(world);

        await DrainAsync(world);

        var item = await StoredItemAsync(world);
        Assert.Equal(
            ("Invoiced", "FV-1-123", await StoredInvoiceIdAsync(world)),
            (item.Status, item.InvoiceNumber, item.InvoiceId));
        var read = Item(await GetBatchAsync(world, batchId), 0);
        Assert.Equal(item.InvoiceId, read.GetProperty("invoiceId").GetGuid());
        // El pedido sí quedó marcado con esa factura: no hay nada que avisar.
        Assert.False(item.OrderMarkFailed);
        Assert.False(read.GetProperty("orderMarkFailed").GetBoolean());
        Assert.Single(InvoiceCalls(world.Siigo));
    }

    // El envío murió entre guardar la factura como creada y marcar el pedido: el ítem es Invoiced y avisa que falta la marca, para que
    // alguien lo reenvíe. La recuperación sólo la consulta: no marca el pedido.
    [Fact]
    public async Task ARecoveredItemWhoseOrderWasNeverMarkedSaysSo()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        Assert.IsType<SiigoBatchItemOutcome.Invoiced>(await IssueWithoutBatchAsync(world));
        var batchId = await CreateBatchAsync(world, world.OrderId);
        await ExecuteAsync(
            world.ConnectionString,
            """
            UPDATE quotations.orders
            SET invoiced_at = NULL, invoiced_by = NULL, invoice_provider = NULL, invoice_number = NULL, invoice_external_id = NULL
            WHERE id = @id
            """,
            ("id", world.OrderId));
        await AbandonTheItemAsync(world);

        await DrainAsync(world);

        var item = await StoredItemAsync(world);
        Assert.Equal(
            ("Invoiced", "FV-1-123", await StoredInvoiceIdAsync(world), true),
            (item.Status, item.InvoiceNumber, item.InvoiceId, item.OrderMarkFailed));
        Assert.True(Item(await GetBatchAsync(world, batchId), 0).GetProperty("orderMarkFailed").GetBoolean());
        Assert.False((await GetOrderAsync(world)).GetProperty("invoiced").GetBoolean());
        Assert.Single(InvoiceCalls(world.Siigo));
    }

    [Fact]
    public async Task ARecoveredItemIsSettledEvenIfTheOrderMarkCannotBeRead()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        Assert.IsType<SiigoBatchItemOutcome.Invoiced>(await IssueWithoutBatchAsync(world));
        await CreateBatchAsync(world, world.OrderId);
        await AbandonTheItemAsync(world);
        // La lectura del pedido falla en la base: sin confirmación, el ítem avisa y se cierra igual.
        await ExecuteAsync(world.ConnectionString, "ALTER TABLE quotations.orders RENAME COLUMN invoice_number TO invoice_number_gone");

        await DrainAsync(world);

        var item = await StoredItemAsync(world);
        Assert.Equal(("Invoiced", "FV-1-123", true), (item.Status, item.InvoiceNumber, item.OrderMarkFailed));
        Assert.True(item.BatchFinished);
    }

    // Con flete en factura aparte, la marca del pedido pudo fallar antes de que el apagado cortara el envío del flete: el ítem no sale
    // facturado (decidido el 2026-11-25) y las filas del pedido siguen avisando que no quedó marcado.
    [Fact]
    public async Task AShutdownDuringTheFreightSendFailsTheItemAndTheOrderStillSaysItIsNotMarked()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database, freightSeparate: true, freight: true);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var sends = 0;
        world.Siigo.OnRequest("/v1/invoices", _ =>
        {
            if (Interlocked.Increment(ref sends) == 1)
            {
                ExecuteAsync(
                    world.ConnectionString,
                    "UPDATE quotations.orders SET invoiced_at = now(), invoiced_by = @by WHERE id = @id",
                    ("by", Guid.CreateVersion7()), ("id", world.OrderId)).GetAwaiter().GetResult();
                return Created("FV-1-123", "id-1", 0m);
            }

            shutdown.Cancel();
            throw new OperationCanceledException(shutdown.Token);
        });
        await CreateBatchAsync(world, world.OrderId);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Worker(world).DrainAsync(shutdown.Token));

        var item = await StoredItemAsync(world);
        Assert.Equal(
            ("Failed", "siigo.invoice.freight_pending", (Guid?)null), (item.Status, item.ErrorCode, item.InvoiceId));
        Assert.True(item.LeaseReleased);
        Assert.True(item.BatchFinished);
        Assert.Equal(2, InvoiceCalls(world.Siigo).Count);
        var orderRow = (await ReadAsync(await world.Client.GetAsync(world.BaseRoute, Ct)))[0];
        Assert.Equal(
            ("Order", true), (orderRow.GetProperty("kind").GetString(), orderRow.GetProperty("orderMarkFailed").GetBoolean()));
    }

    // La de flete se queda sin respuesta: el ítem no sale facturado, y otra tanda del pedido manda sólo la de flete, con su clave.
    [Fact]
    public async Task AnUnsentFreightFailsTheItemAndTheNextBatchSendsOnlyTheFreight()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database, freightSeparate: true, freight: true);
        world.Siigo.AddProduct("p-f", "FLETE", "Flete");
        var sends = 0;
        world.Siigo.OnRequest("/v1/invoices", _ => Interlocked.Increment(ref sends) switch
        {
            1 => Created("FV-1-123", "id-1", 0m),
            2 => FakeSiigoApi.Respond(HttpStatusCode.ServiceUnavailable, "{}"),
            _ => Created("FV-1-124", "id-2", 0m),
        });

        var failed = await RunBatchOfOneAsync(world);

        Assert.Equal(
            ("Failed", "siigo.invoice.freight_pending", JsonValueKind.Null),
            (failed.GetProperty("status").GetString(), failed.GetProperty("errorCode").GetString(),
                failed.GetProperty("invoiceId").ValueKind));
        Assert.True((await GetOrderAsync(world)).GetProperty("invoiced").GetBoolean());

        var resent = await RunBatchOfOneAsync(world);

        Assert.Equal(
            ("Invoiced", "FV-1-123"),
            (resent.GetProperty("status").GetString(), resent.GetProperty("invoiceNumber").GetString()));
        var calls = InvoiceCalls(world.Siigo);
        Assert.Equal(3, calls.Count);
        Assert.Equal(calls[1].Header("Idempotency-Key"), calls[2].Header("Idempotency-Key"));
        Assert.NotEqual(calls[0].Header("Idempotency-Key"), calls[2].Header("Idempotency-Key"));
    }

    // La del pedido quedó por confirmar y la de flete nunca salió: declarar que la del pedido existe no deja el ítem facturado.
    [Fact]
    public async Task ResolvingTheOrderAsExistsLeavesTheItemFailedWhileItsFreightIsPending()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database, freightSeparate: true, freight: true);
        world.Siigo.AddProduct("p-f", "FLETE", "Flete");
        world.Siigo.OnRequest("/v1/invoices", _ => FakeSiigoApi.Respond(HttpStatusCode.ServiceUnavailable, "{}"));
        var batchId = await CreateBatchAsync(world, world.OrderId);
        await DrainAsync(world);

        await ReadAsync(await world.Client.PostAsJsonAsync(
            $"{world.BaseRoute}/resolve", new { kind = "Order", outcome = "exists", number = "FV-1-777" }, Ct));

        var batch = await GetBatchAsync(world, batchId);
        var item = batch.GetProperty("items")[0];
        Assert.Equal(
            ("Failed", "siigo.invoice.freight_pending", false),
            (item.GetProperty("status").GetString(), item.GetProperty("errorCode").GetString(),
                item.GetProperty("orderMarkFailed").GetBoolean()));
        var summary = (await ReadAsync(await world.Client.GetAsync(BatchesRoute(world), Ct))).GetProperty("items")[0];
        Assert.Equal(
            1,
            summary.GetProperty("statusCounts").EnumerateArray()
                .Single(count => count.GetProperty("status").GetString() == "Failed").GetProperty("count").GetInt32());
    }

    // Un redespliegue corta la llamada a Siigo: el ítem se cierra de una vez con la fila, sin esperar el lease ni volver a Pending.
    [Fact]
    public async Task AShutdownDuringTheSendClosesTheItemWithoutWaitingForTheLease()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        world.Siigo.OnRequest("/v1/invoices", _ =>
        {
            shutdown.Cancel();
            throw new OperationCanceledException(shutdown.Token);
        });
        var batchId = await CreateBatchAsync(world, world.OrderId);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Worker(world).DrainAsync(shutdown.Token));

        var item = await StoredItemAsync(world);
        Assert.Equal(
            ("ToConfirm", "siigo.invoice.interrupted", await StoredInvoiceIdAsync(world)),
            (item.Status, item.ErrorCode, item.InvoiceId));
        Assert.True(item.LeaseReleased);
        Assert.True(item.BatchFinished);
        // Mientras dure el reclamo del envío cortado, la fila se considera en camino; vencido, queda por confirmar.
        Assert.Equal("Sending", Item(await GetBatchAsync(world, batchId), 0).GetProperty("status").GetString());
        await ExecuteAsync(world.ConnectionString, "UPDATE siigo.invoices SET sending_since = now() - interval '9 minutes'");
        var expired = Item(await GetBatchAsync(world, batchId), 0);
        Assert.Equal(
            ("ToConfirm", "siigo.invoice.interrupted"),
            (expired.GetProperty("status").GetString(), expired.GetProperty("errorCode").GetString()));
        await DrainAsync(world);
        Assert.Single(InvoiceCalls(world.Siigo));
    }

    // Un redespliegue corta el envío de la del pedido con las dos filas reclamadas. La de flete nunca salió: el reenvío no la deja por
    // confirmar, y declarar que la del pedido no existe la descarta, así el pedido no queda bloqueado.
    [Fact]
    public async Task AfterACutOrderSendTheFreightIsNeverUnansweredAndDeclaringTheOrderMissingFreesTheOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database, freightSeparate: true, freight: true);
        world.Siigo.AddProduct("p-f", "FLETE", "Flete");
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var sends = 0;
        world.Siigo.OnRequest("/v1/invoices", _ =>
        {
            if (Interlocked.Increment(ref sends) > 1)
            {
                return FakeSiigoApi.Respond(HttpStatusCode.ServiceUnavailable, "{}");
            }

            shutdown.Cancel();
            throw new OperationCanceledException(shutdown.Token);
        });
        await CreateBatchAsync(world, world.OrderId);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Worker(world).DrainAsync(shutdown.Token));
        await ExecuteAsync(world.ConnectionString, "UPDATE siigo.invoices SET sending_since = now() - interval '9 minutes'");

        // Una persona lo reintenta desde la pantalla: otra tanda del pedido.
        await IssueRowsThroughBatchAsync(world);

        Assert.Equal(
            1,
            await ScalarAsync<long>(
                world.ConnectionString,
                """
                SELECT count(*) FROM siigo.invoices
                WHERE kind = 'Freight' AND status = 'Pending' AND unanswered_at IS NULL AND sending_since IS NULL
                """));

        await ExecuteAsync(world.ConnectionString, "UPDATE siigo.invoices SET last_unanswered_at = now() - interval '11 minutes'");
        await ReadAsync(await world.Client.PostAsJsonAsync(world.BaseRoute + "/resolve", new { kind = "Order", outcome = "missing" }, Ct));

        Assert.Equal(
            0,
            await ScalarAsync<long>(world.ConnectionString, "SELECT count(*) FROM siigo.invoices WHERE status NOT IN ('Rejected', 'Failed')"));
        Assert.Equal(2, InvoiceCalls(world.Siigo).Count);
    }

    // Spec 2026-11-24 §5: el número y el cliente que muestra /orders, y los seis estados contados, en orden fijo e incluso en cero.
    [Fact]
    public async Task TheDetailCarriesTheOrderNumberTheCustomerAndFullCounts()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);

        var created = await ReadAsync(await PostBatchAsync(world, world.OrderId));
        await DrainAsync(world);
        var batch = await GetBatchAsync(world, created.GetProperty("id").GetGuid());

        Assert.Equal((world.OrderNumber, "Verde Esencial S.A.S."), Label(Item(created, 0)));
        Assert.Equal(["Pending=1", "Sending=0", "Invoiced=0", "ToConfirm=0", "Failed=0", "Skipped=0"], Counts(created));
        var item = Item(batch, 0);
        Assert.Equal((world.OrderNumber, "Verde Esencial S.A.S."), Label(item));
        Assert.Equal(["Pending=0", "Sending=0", "Invoiced=1", "ToConfirm=0", "Failed=0", "Skipped=0"], Counts(batch));
        Assert.Equal(
            (0, "Invoiced", "FV-1-123"),
            (item.GetProperty("position").GetInt32(), item.GetProperty("status").GetString(), item.GetProperty("invoiceNumber").GetString()));
        Assert.Equal(await StoredInvoiceIdAsync(world), item.GetProperty("invoiceId").GetGuid());
        Assert.Empty(SiigoErrors(item));
        Assert.False(item.GetProperty("orderMarkFailed").GetBoolean());
        Assert.False(item.GetProperty("canResend").GetBoolean());
        Assert.False(item.GetProperty("canResolve").GetBoolean());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("missingAvailableAt").ValueKind);
    }

    [Fact]
    public async Task AForeignOrderHasNoNumberNorCustomer()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var (otherTenantId, otherClient) = await OtherTenantAsync(world);
        using var disposable = otherClient;
        var route = $"/api/v1/tenants/{otherTenantId}/siigo/invoice-batches";

        // Un pedido de otro tenant y uno que no existe.
        var created = await ReadAsync(await otherClient.PostAsJsonAsync(route, new { orderIds = new[] { world.OrderId, Guid.NewGuid() } }, Ct));
        await DrainAsync(world);
        var batch = await ReadAsync(await otherClient.GetAsync($"{route}/{created.GetProperty("id").GetGuid()}", Ct));

        foreach (var item in created.GetProperty("items").EnumerateArray().Concat(batch.GetProperty("items").EnumerateArray()))
        {
            Assert.Equal(((string?)null, (string?)null), Label(item));
        }

        Assert.Equal(["Pending=0", "Sending=0", "Invoiced=0", "ToConfirm=0", "Failed=0", "Skipped=2"], Counts(batch));
        Assert.Empty(InvoiceCalls(world.Siigo));
    }

    [Fact]
    public async Task TheListPagesNewestFirstWithFullCounts()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var oldest = await CreateBatchAsync(world, Guid.NewGuid());
        var middle = await CreateBatchAsync(world, Guid.NewGuid());
        var newest = await CreateBatchAsync(world, world.OrderId);

        var first = await ReadAsync(await world.Client.GetAsync($"{BatchesRoute(world)}?pageSize=2", Ct));
        await DrainAsync(world);
        var second = await ReadAsync(await world.Client.GetAsync($"{BatchesRoute(world)}?page=2&pageSize=2", Ct));
        var byDefault = await ReadAsync(await world.Client.GetAsync(BatchesRoute(world), Ct));

        Assert.Equal((3, 1, 2), PageOf(first));
        Assert.Equal([newest, middle], Ids(first));
        Assert.All(
            first.GetProperty("items").EnumerateArray(),
            summary =>
            {
                Assert.Equal(["Pending=1", "Sending=0", "Invoiced=0", "ToConfirm=0", "Failed=0", "Skipped=0"], Counts(summary));
                Assert.Equal(("InProgress", 1), (summary.GetProperty("status").GetString(), summary.GetProperty("total").GetInt32()));
                Assert.Equal(JsonValueKind.Null, summary.GetProperty("finishedAt").ValueKind);
            });
        Assert.Equal((3, 2, 2), PageOf(second));
        Assert.Equal([oldest], Ids(second));
        var finished = second.GetProperty("items")[0];
        Assert.Equal("Finished", finished.GetProperty("status").GetString());
        Assert.Equal(["Pending=0", "Sending=0", "Invoiced=0", "ToConfirm=0", "Failed=0", "Skipped=1"], Counts(finished));
        Assert.Equal((3, 1, 25), PageOf(byDefault));
        Assert.Equal([newest, middle, oldest], Ids(byDefault));
    }

    // La carrera de dos réplicas deja finished_at vacío con todos los pedidos resueltos: se lee terminada, en el detalle y en la lista.
    [Fact]
    public async Task ABatchLeftOpenWithEveryItemSettledReadsAsFinished()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var batchId = await CreateBatchAsync(world, world.OrderId);
        await DrainAsync(world);
        await ExecuteAsync(world.ConnectionString, "UPDATE siigo.invoice_batches SET finished_at = NULL");

        var detail = await GetBatchAsync(world, batchId);
        var summary = (await ReadAsync(await world.Client.GetAsync(BatchesRoute(world), Ct))).GetProperty("items")[0];

        foreach (var batch in new[] { detail, summary })
        {
            Assert.Equal("Finished", batch.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.String, batch.GetProperty("finishedAt").ValueKind);
        }
    }

    // (page - 1) * pageSize no cabe en un int: la página se recorta en vez de mandar un desplazamiento negativo.
    [Fact]
    public async Task AHugePageIsClampedAndComesBackEmpty()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await CreateBatchAsync(world, world.OrderId);

        var page = await ReadAsync(await world.Client.GetAsync($"{BatchesRoute(world)}?page={int.MaxValue}", Ct));

        Assert.Equal((1, int.MaxValue / 100, 25), PageOf(page));
        Assert.Empty(Ids(page));
    }

    [Fact]
    public async Task ABadPageIs422WithItsField()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);

        var page = await world.Client.GetAsync($"{BatchesRoute(world)}?page=x", Ct);
        var pageSize = await world.Client.GetAsync($"{BatchesRoute(world)}?pageSize=y", Ct);

        Assert.True((await ReadValidationErrorsAsync(page)).TryGetProperty("page", out _));
        Assert.True((await ReadValidationErrorsAsync(pageSize)).TryGetProperty("pageSize", out _));
    }

    [Fact]
    public async Task TheListIsTheTenantsAndNeedsThePermission()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        await CreateBatchAsync(world, world.OrderId);
        var (otherTenantId, otherClient) = await OtherTenantAsync(world);
        using var disposable = otherClient;
        using var withoutPermission = CreateClient(world.Factory, world.Tenant.OwnerUserId, world.TenantId, SiigoPermissions.SettingsManage);

        var forbidden = new[]
        {
            await withoutPermission.GetAsync(BatchesRoute(world), Ct),
            await otherClient.GetAsync(BatchesRoute(world), Ct),
        };
        var own = await ReadAsync(await otherClient.GetAsync($"/api/v1/tenants/{otherTenantId}/siigo/invoice-batches", Ct));

        Assert.All(forbidden, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
        Assert.Equal((0, 1, 25), PageOf(own));
        Assert.Empty(Ids(own));
    }

    // Spec 2026-11-24 §2: el ítem sigue a la factura al leerse; una resolución desde el pedido se ve en todas las tandas que la tocaron.
    [Fact]
    public async Task AResolutionFromTheOrderShowsInEveryBatchThatTouchedTheInvoice()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.OnRequest("/v1/invoices", _ => FakeSiigoApi.Respond(HttpStatusCode.ServiceUnavailable, "{}"));
        var first = await CreateBatchAsync(world, world.OrderId);
        await DrainAsync(world);
        var second = await CreateBatchAsync(world, world.OrderId);
        await DrainAsync(world);

        var pending = Item(await GetBatchAsync(world, first), 0);
        Assert.Equal(
            ("ToConfirm", "siigo.invoice.unavailable"),
            (pending.GetProperty("status").GetString(), pending.GetProperty("errorCode").GetString()));
        Assert.True(pending.GetProperty("canResend").GetBoolean());
        Assert.True(pending.GetProperty("canResolve").GetBoolean());
        Assert.Equal(JsonValueKind.String, pending.GetProperty("missingAvailableAt").ValueKind);

        await ExecuteAsync(world.ConnectionString, "UPDATE siigo.invoices SET last_unanswered_at = now() - interval '11 minutes'");
        await ReadAsync(await world.Client.PostAsJsonAsync(world.BaseRoute + "/resolve", new { outcome = "missing" }, Ct));

        foreach (var batchId in new[] { first, second })
        {
            var batch = await GetBatchAsync(world, batchId);
            var item = Item(batch, 0);
            Assert.Equal(
                ("Failed", "siigo.invoice.resolved_missing"),
                (item.GetProperty("status").GetString(), item.GetProperty("errorCode").GetString()));
            Assert.False(item.GetProperty("canResend").GetBoolean());
            Assert.False(item.GetProperty("canResolve").GetBoolean());
            Assert.Equal(1, batch.GetProperty("failed").GetInt32());
            Assert.Equal(["Pending=0", "Sending=0", "Invoiced=0", "ToConfirm=0", "Failed=1", "Skipped=0"], Counts(batch));
        }

        var list = await ReadAsync(await world.Client.GetAsync(BatchesRoute(world), Ct));
        Assert.All(
            list.GetProperty("items").EnumerateArray(),
            summary => Assert.Equal(["Pending=0", "Sending=0", "Invoiced=0", "ToConfirm=0", "Failed=1", "Skipped=0"], Counts(summary)));
    }

    // Mientras una tanda tiene el pedido, otra respondería 409: no se ofrece reenviar, ni desde el pedido ni desde una tanda anterior.
    [Theory]
    [InlineData("Pending")]
    [InlineData("Sending")]
    public async Task AnOrderHeldByAnOpenBatchIsNotOfferedForResend(string itemStatus)
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.OnRequest("/v1/invoices", _ => FakeSiigoApi.Respond(HttpStatusCode.ServiceUnavailable, "{}"));
        var first = await CreateBatchAsync(world, world.OrderId);
        await DrainAsync(world);

        var response = await PostBatchAsync(world, world.OrderId);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var second = created.GetProperty("id").GetGuid();
        await ExecuteAsync(
            world.ConnectionString,
            "UPDATE siigo.invoice_batch_items SET status = @status WHERE status = 'Pending'",
            ("status", itemStatus));

        var older = Item(await GetBatchAsync(world, first), 0);
        var row = (await ReadAsync(await world.Client.GetAsync(world.BaseRoute, Ct)))[0];

        Assert.False(Item(created, 0).GetProperty("canResend").GetBoolean());
        Assert.False(Item(await GetBatchAsync(world, second), 0).GetProperty("canResend").GetBoolean());
        Assert.Equal(
            ("ToConfirm", false, true),
            (older.GetProperty("status").GetString(), older.GetProperty("canResend").GetBoolean(), older.GetProperty("canResolve").GetBoolean()));
        Assert.Equal(
            (true, false, true),
            (row.GetProperty("awaitingConfirmation").GetBoolean(), row.GetProperty("canResend").GetBoolean(), row.GetProperty("canResolve").GetBoolean()));
    }

    // Con el reenvío en cola, «no existe» soltaría la fila y el worker mandaría una factura nueva con otra clave: se rechaza, y el
    // reenvío sale con la clave del primer envío.
    [Fact]
    public async Task AnOrderHeldByAnOpenBatchCannotBeDeclaredMissing()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.OnRequest("/v1/invoices", _ => FakeSiigoApi.Respond(HttpStatusCode.ServiceUnavailable, "{}"));
        await CreateBatchAsync(world, world.OrderId);
        await DrainAsync(world);
        await ExecuteAsync(world.ConnectionString, "UPDATE siigo.invoices SET last_unanswered_at = now() - interval '11 minutes'");
        await CreateBatchAsync(world, world.OrderId);

        var response = await world.Client.PostAsJsonAsync(world.BaseRoute + "/resolve", new { outcome = "missing" }, Ct);

        await AssertOrderInProgressAsync(response, world.OrderId);
        Assert.Equal("Pending", await ScalarAsync<string>(world.ConnectionString, "SELECT status FROM siigo.invoices"));
        await DrainAsync(world);
        var calls = InvoiceCalls(world.Siigo);
        Assert.Equal(2, calls.Count);
        Assert.Equal(calls[0].Header("Idempotency-Key"), calls[1].Header("Idempotency-Key"));
    }

    // El pedido de este ítem ya se cerró por confirmar; que a la tanda le falten otros no lo retiene.
    [Fact]
    public async Task AToConfirmItemOfABatchStillInProgressCanBeResent()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.OnRequest("/v1/invoices", _ => FakeSiigoApi.Respond(HttpStatusCode.ServiceUnavailable, "{}"));
        var waiting = Guid.NewGuid();
        var batchId = await CreateBatchAsync(world, world.OrderId, waiting);
        await ExecuteAsync(
            world.ConnectionString,
            "UPDATE siigo.invoice_batch_items SET available_at = now() + interval '1 hour' WHERE order_id = @order",
            ("order", waiting));
        await DrainAsync(world);

        var batch = await GetBatchAsync(world, batchId);

        Assert.Equal(
            ("InProgress", "ToConfirm", "Pending"),
            (batch.GetProperty("status").GetString(), Item(batch, 0).GetProperty("status").GetString(), Item(batch, 1).GetProperty("status").GetString()));
        Assert.True(Item(batch, 0).GetProperty("canResend").GetBoolean());
        Assert.True((await ReadAsync(await world.Client.GetAsync(world.BaseRoute, Ct)))[0].GetProperty("canResend").GetBoolean());
    }

    [Fact]
    public async Task AResendFromAnotherBatchShowsAsInvoicedInTheFirst()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.OnRequest("/v1/invoices", _ => FakeSiigoApi.Respond(HttpStatusCode.ServiceUnavailable, "{}"));
        var first = await CreateBatchAsync(world, world.OrderId);
        await DrainAsync(world);

        world.Siigo.OnRequest("/v1/invoices", _ => Created("FV-1-123", "id-1", 0m));
        await CreateBatchAsync(world, world.OrderId);
        await DrainAsync(world);

        var batch = await GetBatchAsync(world, first);
        var item = Item(batch, 0);
        Assert.Equal(("Invoiced", "FV-1-123"), (item.GetProperty("status").GetString(), item.GetProperty("invoiceNumber").GetString()));
        Assert.Equal(JsonValueKind.Null, item.GetProperty("errorCode").ValueKind);
        Assert.Equal(await StoredInvoiceIdAsync(world), item.GetProperty("invoiceId").GetGuid());
        Assert.False(item.GetProperty("canResend").GetBoolean());
        Assert.False(item.GetProperty("canResolve").GetBoolean());
        Assert.Equal(1, batch.GetProperty("invoiced").GetInt32());
        Assert.Equal(["Pending=0", "Sending=0", "Invoiced=1", "ToConfirm=0", "Failed=0", "Skipped=0"], Counts(batch));
        var calls = InvoiceCalls(world.Siigo);
        Assert.Equal(2, calls.Count);
        Assert.Equal(calls[0].Header("Idempotency-Key"), calls[1].Header("Idempotency-Key"));
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

    // La factura pudo haber quedado creada: que el reenvío no se pueda hacer no la da por fallida.
    [Theory]
    [InlineData("connection", "siigo.connection_unavailable")]
    [InlineData("permission", "siigo.invoice_batch.forbidden")]
    public async Task AResendThatCannotBeMadeLeavesTheAwaitingInvoiceToConfirm(string lost, string code)
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.OnRequest("/v1/invoices", _ => FakeSiigoApi.Respond(HttpStatusCode.ServiceUnavailable, "{}"));
        await CreateBatchAsync(world, world.OrderId);
        await DrainAsync(world);
        var second = await CreateBatchAsync(world, world.OrderId);
        await ExecuteAsync(
            world.ConnectionString,
            lost == "connection"
                ? "UPDATE integrations.connections SET status = 'Paused'"
                : "UPDATE siigo.invoice_batches SET requested_by = gen_random_uuid()");

        await DrainAsync(world);

        Assert.Equal(
            $"ToConfirm|{code}|{await StoredInvoiceIdAsync(world)}",
            await ScalarAsync<string>(
                world.ConnectionString,
                "SELECT concat(status, '|', error_code, '|', invoice_id) FROM siigo.invoice_batch_items WHERE batch_id = @batch",
                ("batch", second)));
        // La fila no se tocó: se ve por qué no salió el reenvío, y no el motivo del envío anterior.
        Assert.Equal(code, Item(await GetBatchAsync(world, second), 0).GetProperty("errorCode").GetString());
        Assert.Single(InvoiceCalls(world.Siigo));
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
