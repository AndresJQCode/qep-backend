using System.Net;
using System.Net.Http.Json;
using Modules.Pos.Application;
using static Modules.Pos.IntegrationTests.PosApiHarness;

namespace Modules.Pos.IntegrationTests;

public sealed class PosRegisterApiTests
{
    private static readonly int[] ExpectedTaxPercentages = [0, 5, 19];

    // Spec, «Integración»: abrir → vender (dividido, tarjeta, efectivo) → cerrar, con cuerpos
    // completos, paymentTotals con los tres medios y la diferencia.
    [Fact]
    public async Task OpenSellWithSplitCardAndCashThenCloseWithADifference()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);

        var before = await world.Admin.GetFromJsonAsync<RegisterContextResponse>($"{world.Url}/register", TestContext.Current.CancellationToken);
        Assert.NotNull(before);
        Assert.Null(before.Session);
        Assert.Equal(world.Company.Id, before.DefaultCompanyId);

        var session = await world.OpenSessionAsync(world.Admin);
        Assert.Equal(1, session.Version);

        var preview = await world.Admin.PostAsJsonAsync(
            $"{world.Url}/sales/preview",
            new { lines = new[] { new { productId = world.Shampoo, quantity = 2m, discountPercentage = 10m }, new { productId = world.Avena, quantity = 1.5m, discountPercentage = 0m }, new { productId = world.Jabon, quantity = 3m, discountPercentage = 0m } } },
            TestContext.Current.CancellationToken);
        var previewBody = await preview.Content.ReadFromJsonAsync<PosPreviewResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal(37_890m, previewBody!.Total);
        Assert.Equal(3_847.14m, previewBody.TaxAmount);

        var splitId = Guid.CreateVersion7();
        var split = await world.Admin.PostAsJsonAsync(
            $"{world.Url}/sales",
            PosWorld.SaleBody(splitId, session.Id, world.WorkedExampleLines(), PosWorld.SplitPayments()),
            TestContext.Current.CancellationToken);
        var sale = await split.Content.ReadFromJsonAsync<PosSaleResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, split.StatusCode);
        Assert.Equal($"/api/v1/tenants/{world.Tenant.TenantId}/pos/sales/{splitId}", split.Headers.Location?.OriginalString);
        Assert.Equal("POS-000001", sale!.SaleNumber);
        Assert.Equal(2_110m, sale.ChangeAmount);
        Assert.Equal(34_042.86m, sale.Subtotal);
        Assert.Equal(ExpectedTaxPercentages, sale.TaxBreakdown.Select(entry => entry.TaxPercentage));
        Assert.Equal(17_890m, sale.Payments.Single(payment => payment.Method == "Cash").Amount);
        Assert.Equal("Consumidor final", sale.Customer.Name);
        Assert.Equal(world.Company.TaxId, sale.Issuer.TaxId);
        Assert.Equal(TimeSpan.FromHours(-5), sale.CreatedAtLocal.Offset);

        var card = await world.Admin.PostAsJsonAsync(
            $"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.WorkedExampleLines(), [new { method = "Card", amount = 37_890m }]),
            TestContext.Current.CancellationToken);
        var cash = await world.Admin.PostAsJsonAsync(
            $"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.WorkedExampleLines(), PosWorld.CashPayment(50_000m)),
            TestContext.Current.CancellationToken);
        Assert.Equal("POS-000002", (await card.Content.ReadFromJsonAsync<PosSaleResponse>(TestContext.Current.CancellationToken))!.SaleNumber);
        Assert.Equal(12_110m, (await cash.Content.ReadFromJsonAsync<PosSaleResponse>(TestContext.Current.CancellationToken))!.ChangeAmount);

        var live = await world.Admin.GetFromJsonAsync<RegisterContextResponse>($"{world.Url}/register", TestContext.Current.CancellationToken);
        Assert.Equal(4, live!.Session!.Version);
        Assert.Equal(100_000m + 17_890m + 37_890m, live.Session.ExpectedCash);
        Assert.Equal(
            new[] { new PosPaymentTotalResponse("Cash", 55_780m), new PosPaymentTotalResponse("Card", 57_890m), new PosPaymentTotalResponse("Transfer", 0m) },
            live.Session.PaymentTotals);

        using var close = new HttpRequestMessage(HttpMethod.Post, $"{world.Url}/sessions/{session.Id}/close")
        {
            Content = JsonContent.Create(new { countedCash = 155_780m - 890m, note = "Faltan 890" }),
        };
        close.Headers.TryAddWithoutValidation("If-Match", "\"4\"");
        var closed = await world.Admin.SendAsync(close, TestContext.Current.CancellationToken);
        var summary = await closed.Content.ReadFromJsonAsync<PosSessionSummaryResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        Assert.Equal("Closed", summary!.Status);
        Assert.Equal(155_780m, summary.ExpectedCash);
        Assert.Equal(-890m, summary.CashDifference);
        Assert.Equal(3, summary.SalesCount);
    }

    [Fact]
    public async Task ARejectedSaleDoesNotLeaveAGapInTheNumbering()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var session = await world.OpenSessionAsync(world.Admin);

        var first = await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.PlainLine(), PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken);
        var stale = await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id,
                [new { productId = world.Shampoo, quantity = 1m, discountPercentage = 0m, expectedUnitPrice = 12_500m, expectedTaxPercentage = 19 }],
                PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken);
        var second = await world.Admin.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.PlainLine(), PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, stale.StatusCode);
        Assert.Contains("pos.sale.price_changed", await stale.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal("POS-000002", (await second.Content.ReadFromJsonAsync<PosSaleResponse>(TestContext.Current.CancellationToken))!.SaleNumber);
    }

    // Decisión 30 y P10: exacto y con mayúsculas; inactivo y sin precio vuelven marcados.
    [Fact]
    public async Task ByCodeIsExactAndMarksInactiveAndUnpricedProducts()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        await ExecuteSqlAsync(database, "UPDATE catalog.products SET is_active = false WHERE id = @id", ("id", world.Jabon));
        await ExecuteSqlAsync(database, "UPDATE catalog.products SET price_base_cop = NULL, price_base_usd = 10 WHERE id = @id", ("id", world.Avena));

        var exact = await world.Admin.GetFromJsonAsync<PosProductResponse>($"{world.Url}/products/by-code?code=SH-400", TestContext.Current.CancellationToken);
        var lower = await world.Admin.GetAsync($"{world.Url}/products/by-code?code=sh-400", TestContext.Current.CancellationToken);
        var inactive = await world.Admin.GetFromJsonAsync<PosProductResponse>($"{world.Url}/products/by-code?code=JB-03", TestContext.Current.CancellationToken);
        var unpriced = await world.Admin.GetFromJsonAsync<PosProductResponse>($"{world.Url}/products/by-code?code=AV-01", TestContext.Current.CancellationToken);
        var search = await world.Admin.GetFromJsonAsync<PosPage<PosProductResponse>>($"{world.Url}/products?search=sham", TestContext.Current.CancellationToken);

        Assert.True(exact!.Sellable);
        Assert.Equal(19, exact.TaxPercentage);
        Assert.Equal(HttpStatusCode.NotFound, lower.StatusCode);
        Assert.Contains("pos.product.not_found", await lower.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(("Inactive", false), (inactive!.UnsellableReason, inactive.Sellable));
        Assert.Equal(("PriceMissing", (decimal?)null), (unpriced!.UnsellableReason, unpriced.UnitPrice));
        Assert.Equal("SH-400", Assert.Single(search!.Items).Code);
    }

    // Spec, «Integración»: las seis políticas resuelven (403 y no 500 sin el permiso).
    [Fact]
    public async Task EveryEndpointAnswers403WithoutItsPermission()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        using var nobody = CreateClient(factory, world.Tenant.OwnerUserId, world.Tenant.TenantId, "advisorship.read");
        var id = Guid.CreateVersion7();

        HttpResponseMessage[] responses =
        [
            await nobody.GetAsync($"{world.Url}/register", TestContext.Current.CancellationToken),
            await nobody.PostAsJsonAsync($"{world.Url}/sessions", new { openingFloat = 0m }, TestContext.Current.CancellationToken),
            await nobody.PostAsJsonAsync($"{world.Url}/sessions/{id}/close", new { countedCash = 0m }, TestContext.Current.CancellationToken),
            await nobody.GetAsync($"{world.Url}/sessions", TestContext.Current.CancellationToken),
            await nobody.GetAsync($"{world.Url}/sessions/{id}", TestContext.Current.CancellationToken),
            await nobody.GetAsync($"{world.Url}/products", TestContext.Current.CancellationToken),
            await nobody.GetAsync($"{world.Url}/products/by-code?code=SH-400", TestContext.Current.CancellationToken),
            await nobody.PostAsJsonAsync($"{world.Url}/sales/preview", new { lines = world.PlainLine() }, TestContext.Current.CancellationToken),
            await nobody.PostAsJsonAsync($"{world.Url}/sales", PosWorld.SaleBody(id, id, world.PlainLine(), PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken),
            await nobody.GetAsync($"{world.Url}/sales/{id}", TestContext.Current.CancellationToken),
            await nobody.GetAsync($"{world.Url}/sales", TestContext.Current.CancellationToken),
            await nobody.PostAsJsonAsync($"{world.Url}/sales/{id}/void", new { reason = "Motivo" }, TestContext.Current.CancellationToken),
        ];

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
    }

    // Con el spec de entitlements: sin la fila `pos`, los permisos se enmascaran y todo /pos/* da 403.
    [Fact]
    public async Task WithoutThePosModuleTheRegisterIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        await DisablePosAsync(database, world.Tenant.TenantId);

        var response = await world.Admin.GetAsync($"{world.Url}/register", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // El rol de sistema `cashier` vende sin descontar (spec, decisión 4).
    [Fact]
    public async Task AnInvitedCashierSellsButCannotDiscount()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var cashier = await InviteCashierAsync(factory, database, world.Tenant, CashierPermissions);
        var session = await world.OpenSessionAsync(cashier.Client);

        var plain = await cashier.Client.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.PlainLine(), PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken);
        var discounted = await cashier.Client.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.WorkedExampleLines(), PosWorld.SplitPayments()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, plain.StatusCode);
        Assert.Equal("Cajero Invitado", (await plain.Content.ReadFromJsonAsync<PosSaleResponse>(TestContext.Current.CancellationToken))!.CashierName);
        Assert.Equal(HttpStatusCode.Forbidden, discounted.StatusCode);
        Assert.Contains("pos.sale.discount_not_allowed", await discounted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }
}
