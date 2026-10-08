using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Pos.Application;
using static Modules.Pos.IntegrationTests.PosApiHarness;

namespace Modules.Pos.IntegrationTests;

public sealed class PosIsolationApiTests
{
    [Fact]
    public async Task ARouteForAnotherTenantIs403AndASaleOfAnotherTenantIs404()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var mine = await PosWorld.ArrangeAsync(factory, database);
        var theirs = await PosWorld.ArrangeAsync(factory, database);
        var theirSession = await theirs.OpenSessionAsync(theirs.Admin);
        var theirSaleId = Guid.CreateVersion7();
        await theirs.Admin.PostAsJsonAsync($"{theirs.Url}/sales",
            PosWorld.SaleBody(theirSaleId, theirSession.Id, theirs.PlainLine(), PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken);

        var foreignRoute = await mine.Admin.GetAsync($"{theirs.Url}/register", TestContext.Current.CancellationToken);
        var foreignSale = await mine.Admin.GetAsync($"{mine.Url}/sales/{theirSaleId}", TestContext.Current.CancellationToken);
        var foreignVoid = await mine.Admin.PostAsJsonAsync($"{mine.Url}/sales/{theirSaleId}/void", new { reason = "Ajena" }, TestContext.Current.CancellationToken);
        var foreignSession = await mine.Admin.GetAsync($"{mine.Url}/sessions/{theirSession.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, foreignRoute.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignSale.StatusCode);
        Assert.Contains("pos.sale.not_found", await foreignSale.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, foreignVoid.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignSession.StatusCode);
    }

    // La FK a companies.companies la crea la migración de Pos: sólo este harness la tiene.
    [Fact]
    public async Task DeletingACompanyWithACashSessionIsInUse()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        await world.OpenSessionAsync(world.Admin);

        var response = await world.Tenant.Seeder.DeleteAsync(
            $"/api/v1/tenants/{world.Tenant.TenantId}/companies/{world.Company.Id}", TestContext.Current.CancellationToken);

        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{response.StatusCode}: {text}");
        Assert.Contains("companies.company.in_use", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheUserProbeKeepsACashierWithSales()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var cashier = await InviteCashierAsync(factory, database, world.Tenant, CashierPermissions);
        var session = await world.OpenSessionAsync(cashier.Client);
        await cashier.Client.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), session.Id, world.PlainLine(), PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken);

        using var scope = factory.Services.CreateScope();
        var probe = scope.ServiceProvider.GetServices<IUserReferenceProbe>().Single(item => item.Source == "pos");

        Assert.True(await probe.HasReferencesAsync(cashier.UserId, TestContext.Current.CancellationToken));
        Assert.False(await probe.HasReferencesAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ACashierOnlySeesTheirOwnSalesAndSessionsAndASupervisorSeesAll()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var first = await InviteCashierAsync(factory, database, world.Tenant, CashierPermissions);
        var second = await InviteCashierAsync(factory, database, world.Tenant, CashierPermissions);
        // «Hoy» en Bogotá antes de vender y después de vender: la corrida puede cruzar la medianoche.
        var from = BogotaToday();
        var firstSession = await world.OpenSessionAsync(first.Client);
        var secondSession = await world.OpenSessionAsync(second.Client);
        var firstSale = Guid.CreateVersion7();
        await first.Client.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(firstSale, firstSession.Id, world.PlainLine(), PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken);
        await second.Client.PostAsJsonAsync($"{world.Url}/sales",
            PosWorld.SaleBody(Guid.CreateVersion7(), secondSession.Id, world.PlainLine(), PosWorld.CashPayment(20_000m)), TestContext.Current.CancellationToken);
        var to = BogotaToday();

        var peek = await second.Client.GetAsync($"{world.Url}/sales/{firstSale}", TestContext.Current.CancellationToken);
        var ownSales = await second.Client.GetFromJsonAsync<PosPage<PosSaleListItemResponse>>($"{world.Url}/sales?sessionId={firstSession.Id}", TestContext.Current.CancellationToken);
        var ownSessions = await second.Client.GetFromJsonAsync<PosPage<PosSessionSummaryResponse>>($"{world.Url}/sessions", TestContext.Current.CancellationToken);
        var allToday = await world.Admin.GetFromJsonAsync<PosPage<PosSaleListItemResponse>>($"{world.Url}/sales?from={from}&to={to}", TestContext.Current.CancellationToken);
        var allSessions = await world.Admin.GetFromJsonAsync<PosPage<PosSessionSummaryResponse>>($"{world.Url}/sessions?status=Open", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, peek.StatusCode);
        Assert.Empty(ownSales!.Items);
        Assert.Equal([secondSession.Id], ownSessions!.Items.Select(item => item.Id).ToArray());
        Assert.Equal(2, allToday!.Total);
        Assert.Equal(2, allSessions!.Total);
    }

    private static string BogotaToday() =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
