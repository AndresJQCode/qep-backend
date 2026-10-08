using System.Net;
using System.Net.Http.Json;
using Modules.Pos.Application;
using Modules.Pos.Infrastructure.Persistence;
using Npgsql;
using static Modules.Pos.IntegrationTests.PosApiHarness;

namespace Modules.Pos.IntegrationTests;

public sealed class PosSaleApiTests
{
    private static Task<long> SalesWithIdAsync(Testcontainers.PostgreSql.PostgreSqlContainer database, Guid id) =>
        CountAsync(database, "SELECT count(*) FROM pos.sales WHERE id = @id", ("id", id));

    [Fact]
    public async Task TheSamePostTwiceAnswers201Then200WithOneRowAndOneNumber()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var session = await world.OpenSessionAsync(world.Admin);
        var id = Guid.CreateVersion7();
        var body = PosWorld.SaleBody(id, session.Id, world.WorkedExampleLines(), PosWorld.SplitPayments());

        var first = await world.Admin.PostAsJsonAsync($"{world.Url}/sales", body, TestContext.Current.CancellationToken);
        var repeat = await world.Admin.PostAsJsonAsync($"{world.Url}/sales", body, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        var a = await first.Content.ReadFromJsonAsync<PosSaleResponse>(TestContext.Current.CancellationToken);
        var b = await repeat.Content.ReadFromJsonAsync<PosSaleResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(a!.SaleNumber, b!.SaleNumber);
        Assert.Equal(1, await SalesWithIdAsync(database, id));
        var register = await world.Admin.GetFromJsonAsync<RegisterContextResponse>($"{world.Url}/register", TestContext.Current.CancellationToken);
        Assert.Equal(1, register!.Session!.SalesCount);
    }

    [Fact]
    public async Task TheSameIdWithAnotherQuantityIsAConflictAndTheOriginalStays()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var session = await world.OpenSessionAsync(world.Admin);
        var id = Guid.CreateVersion7();
        await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(id, session.Id, world.PlainLine(1m), PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken);

        var other = await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(id, session.Id, world.PlainLine(2m), PosWorld.CashPayment(30_000m)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, other.StatusCode);
        Assert.Contains("pos.sale.id_conflict", await other.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        var original = await world.Admin.GetFromJsonAsync<PosSaleResponse>($"{world.Url}/sales/{id}", TestContext.Current.CancellationToken);
        Assert.Equal(11_900m, original!.Total);
    }

    // Spec, «Integración»: dos POST idénticos en paralelo, repetido 20 veces → cada vez una fila,
    // las dos respuestas 2xx y el mismo saleNumber. Si el planificador serializa las dos peticiones,
    // las dos salidas son repeticiones válidas y el camino del choque no se ejerce (preflight F-24):
    // la prueba de abajo, con el candado retenido a mano, es la que lo fuerza.
    [Fact]
    public async Task TwoIdenticalPostsInParallelAlwaysLeaveExactlyOneSale()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var session = await world.OpenSessionAsync(world.Admin);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var id = Guid.CreateVersion7();
            var body = PosWorld.SaleBody(id, session.Id, world.PlainLine(), PosWorld.CashPayment(20_000m));

            var responses = await Task.WhenAll(
                world.Admin.PostAsJsonAsync($"{world.Url}/sales", body, TestContext.Current.CancellationToken),
                world.Admin.PostAsJsonAsync($"{world.Url}/sales", body, TestContext.Current.CancellationToken));

            Assert.All(responses, response => Assert.True(response.IsSuccessStatusCode, $"attempt {attempt}: {response.StatusCode}"));
            var sales = await Task.WhenAll(responses.Select(response =>
                response.Content.ReadFromJsonAsync<PosSaleResponse>(TestContext.Current.CancellationToken)));
            Assert.Equal(sales[0]!.SaleNumber, sales[1]!.SaleNumber);
            Assert.Equal(1, await SalesWithIdAsync(database, id));
        }

        Assert.Equal(20, await CountAsync(database, "SELECT count(*) FROM pos.sales WHERE tenant_id = @tenantId", ("tenantId", world.Tenant.TenantId)));
        var register = await world.Admin.GetFromJsonAsync<RegisterContextResponse>($"{world.Url}/register", TestContext.Current.CancellationToken);
        Assert.Equal(20, register!.Session!.SalesCount);
        Assert.Equal(PosSaleNumberFor(20), (await world.Admin.GetFromJsonAsync<PosPage<PosSaleListItemResponse>>($"{world.Url}/sales?pageSize=1", TestContext.Current.CancellationToken))!.Items[0].SaleNumber);
    }

    // Carry B10: el candado consultivo (tenantId, saleId) de CreatePosSaleHandler. Un tercero retiene
    // el candado —el «primer envío en vuelo»—, llegan el envío y su reintento con el mismo id, y
    // sólo cuando Postgres confirma que los dos están esperando se suelta. Sin esperas fijas: la
    // espera es por la condición observable (pg_locks), no por tiempo.
    [Fact]
    public async Task ARetryWhileTheFirstSendIsInFlightWaitsForItAndLeavesOneSale()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var session = await world.OpenSessionAsync(world.Admin);
        var id = Guid.CreateVersion7();
        var body = PosWorld.SaleBody(id, session.Id, world.PlainLine(), PosWorld.CashPayment(20_000m));
        var key = PosSaleIdLock.KeyFor(world.Tenant.TenantId, id);

        await using var holder = new NpgsqlConnection(database.GetConnectionString());
        await holder.OpenAsync(TestContext.Current.CancellationToken);
        await using var holding = await holder.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", holder, holding))
        {
            acquire.Parameters.AddWithValue("key", key);
            await acquire.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var firstSend = world.Admin.PostAsJsonAsync($"{world.Url}/sales", body, TestContext.Current.CancellationToken);
        var retry = world.Admin.PostAsJsonAsync($"{world.Url}/sales", body, TestContext.Current.CancellationToken);

        await WaitUntilAsync(
            async () => await WaitingOnLockAsync(database, key) == 2,
            "both requests to be waiting on the sale id advisory lock");
        Assert.False(firstSend.IsCompleted);
        Assert.False(retry.IsCompleted);
        Assert.Equal(0, await SalesWithIdAsync(database, id));

        await holding.RollbackAsync(TestContext.Current.CancellationToken);
        var responses = await Task.WhenAll(firstSend, retry);

        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.Created],
            responses.Select(response => response.StatusCode).OrderBy(status => (int)status).ToArray());
        var sales = await Task.WhenAll(responses.Select(response =>
            response.Content.ReadFromJsonAsync<PosSaleResponse>(TestContext.Current.CancellationToken)));
        Assert.Equal(sales[0]!.SaleNumber, sales[1]!.SaleNumber);
        Assert.Equal(PosSaleNumberFor(1), sales[0]!.SaleNumber);
        Assert.Equal(1, await SalesWithIdAsync(database, id));
        Assert.Equal(1, await AuditCountAsync(database, world.Tenant.TenantId, "pos.sale.created"));
        var register = await world.Admin.GetFromJsonAsync<RegisterContextResponse>($"{world.Url}/register", TestContext.Current.CancellationToken);
        Assert.Equal(1, register!.Session!.SalesCount);
    }

    /// <summary>Sesiones de Postgres esperando el candado consultivo de esa clave (aún no concedido).</summary>
    private static Task<long> WaitingOnLockAsync(Testcontainers.PostgreSql.PostgreSqlContainer database, long key) =>
        CountAsync(
            database,
            """
            SELECT count(*) FROM pg_locks
            WHERE locktype = 'advisory' AND NOT granted
              AND ((classid::bigint << 32) | objid::bigint) = @key
            """,
            ("key", key));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string description)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!await condition())
        {
            if (timeout.IsCancellationRequested)
            {
                Assert.Fail($"Timed out waiting for {description}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        }
    }

    private static string PosSaleNumberFor(int value) => $"POS-{value:D6}";

    [Fact]
    public async Task CloseWithAStaleIfMatchIs412AndWithoutItIs428()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var session = await world.OpenSessionAsync(world.Admin);
        await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.PlainLine(), PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken);

        using var stale = new HttpRequestMessage(HttpMethod.Post, $"{world.Url}/sessions/{session.Id}/close")
        {
            Content = JsonContent.Create(new { countedCash = 111_900m }),
        };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        var conflict = await world.Admin.SendAsync(stale, TestContext.Current.CancellationToken);
        var missing = await world.Admin.PostAsJsonAsync($"{world.Url}/sessions/{session.Id}/close", new { countedCash = 111_900m }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PreconditionFailed, conflict.StatusCode);
        Assert.Contains("concurrency.conflict", await conflict.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        Assert.Contains("precondition.if_match_required", await missing.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VoidGivesTheMoneyBackWhileOpenAndIsRejectedOnceClosed()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var session = await world.OpenSessionAsync(world.Admin);
        var voidedId = Guid.CreateVersion7();
        var keptId = Guid.CreateVersion7();
        await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(voidedId, session.Id, world.WorkedExampleLines(), PosWorld.SplitPayments()), TestContext.Current.CancellationToken);
        await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(keptId, session.Id, world.PlainLine(), PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken);

        var voided = await world.Admin.PostAsJsonAsync($"{world.Url}/sales/{voidedId}/void", new { reason = "Cliente se arrepintió" }, TestContext.Current.CancellationToken);
        var again = await world.Admin.PostAsJsonAsync($"{world.Url}/sales/{voidedId}/void", new { reason = "Otra vez" }, TestContext.Current.CancellationToken);
        var register = await world.Admin.GetFromJsonAsync<RegisterContextResponse>($"{world.Url}/register", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);
        Assert.Equal("Voided", (await voided.Content.ReadFromJsonAsync<PosSaleResponse>(TestContext.Current.CancellationToken))!.Status);
        Assert.Contains("pos.sale.already_voided", await again.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(111_900m, register!.Session!.ExpectedCash);
        Assert.Equal(1, register.Session.VoidedCount);

        using var close = new HttpRequestMessage(HttpMethod.Post, $"{world.Url}/sessions/{session.Id}/close")
        {
            Content = JsonContent.Create(new { countedCash = 111_900m }),
        };
        close.Headers.TryAddWithoutValidation("If-Match", $"\"{register.Session.Version}\"");
        Assert.Equal(HttpStatusCode.OK, (await world.Admin.SendAsync(close, TestContext.Current.CancellationToken)).StatusCode);

        var late = await world.Admin.PostAsJsonAsync($"{world.Url}/sales/{keptId}/void", new { reason = "Tarde" }, TestContext.Current.CancellationToken);
        var list = await world.Admin.GetFromJsonAsync<PosPage<PosSaleListItemResponse>>($"{world.Url}/sales", TestContext.Current.CancellationToken);

        Assert.Contains("pos.sale.void_session_closed", await late.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal((false, "AlreadyVoided"), (list!.Items.Single(item => item.Id == voidedId).Voidable, list.Items.Single(item => item.Id == voidedId).VoidBlockedReason));
        Assert.Equal((false, "SessionClosed"), (list.Items.Single(item => item.Id == keptId).Voidable, list.Items.Single(item => item.Id == keptId).VoidBlockedReason));
    }

    // Auditoría en la misma transacción que el cambio, y ninguna fila si el cambio falla.
    [Fact]
    public async Task EveryOperationLeavesItsAuditRowAndAFailedSaleLeavesNone()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var session = await world.OpenSessionAsync(world.Admin);
        var saleId = Guid.CreateVersion7();
        await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(saleId, session.Id, world.WorkedExampleLines(), PosWorld.SplitPayments()), TestContext.Current.CancellationToken);
        var failed = await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.PlainLine(), PosWorld.CashPayment(1m)), TestContext.Current.CancellationToken);
        await world.Admin.PostAsJsonAsync($"{world.Url}/sales/{saleId}/void", new { reason = "Motivo" }, TestContext.Current.CancellationToken);

        Assert.Contains("pos.sale.payment_insufficient", await failed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(1, await AuditCountAsync(database, world.Tenant.TenantId, "pos.session.opened"));
        Assert.Equal(1, await AuditCountAsync(database, world.Tenant.TenantId, "pos.sale.created"));
        Assert.Equal(1, await AuditCountAsync(database, world.Tenant.TenantId, "pos.sale.voided"));
        Assert.Equal(1, await CountAsync(database,
            "SELECT count(*) FROM platform.outbox_messages WHERE payload->>'action' = 'pos.sale.created' AND payload::text LIKE '%discount:1:SH-400:10%'"));
    }

    [Fact]
    public async Task CardOnlyAndTransferOnlyCarryNoCashLineAndBadCashBodiesAre422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var session = await world.OpenSessionAsync(world.Admin);

        var card = await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.PlainLine(), [new { method = "Card", amount = 11_900m }]), TestContext.Current.CancellationToken);
        var transfer = await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.PlainLine(), [new { method = "Transfer", amount = 11_900m, reference = "TRX-9" }]), TestContext.Current.CancellationToken);
        var cashWithAmount = await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.PlainLine(), [new { method = "Cash", amount = 11_900m, tendered = 20_000m }]), TestContext.Current.CancellationToken);
        var zeroWithTendered = await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id,
                [new { productId = world.Shampoo, quantity = 1m, discountPercentage = 100m, expectedUnitPrice = 11_900m, expectedTaxPercentage = 19 }],
                PosWorld.CashPayment(1_000m)), TestContext.Current.CancellationToken);
        var next = await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.PlainLine(), PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, card.StatusCode);
        Assert.DoesNotContain((await card.Content.ReadFromJsonAsync<PosSaleResponse>(TestContext.Current.CancellationToken))!.Payments, payment => payment.Method == "Cash");
        Assert.Equal(HttpStatusCode.Created, transfer.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, cashWithAmount.StatusCode);
        Assert.Contains("validation.failed", await cashWithAmount.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, zeroWithTendered.StatusCode);
        Assert.Contains("pos.sale.tendered_invalid", await zeroWithTendered.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        // Ni el 422 de validación ni el de dominio gastaron número.
        Assert.Equal("POS-000003", (await next.Content.ReadFromJsonAsync<PosSaleResponse>(TestContext.Current.CancellationToken))!.SaleNumber);
    }

    // Precio igual y tasa cambiada después del preview: el total no se mueve, el desglose sí.
    [Fact]
    public async Task OnlyTheTaxRateChangedAfterThePreviewIsPriceChanged()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var session = await world.OpenSessionAsync(world.Admin);
        await ExecuteSqlAsync(database, "UPDATE catalog.products SET tax_rate_id = @tax WHERE id = @id", ("tax", world.Tax5), ("id", world.Shampoo));

        var response = await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.PlainLine(), PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("pos.sale.price_changed", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }
}
