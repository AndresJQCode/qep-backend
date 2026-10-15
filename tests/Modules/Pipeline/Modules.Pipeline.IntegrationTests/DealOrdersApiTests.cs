using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Pipeline.Api;
using Modules.Pipeline.Application;
using Modules.Pipeline.Domain;
using static Modules.Pipeline.IntegrationTests.PipelineApiHarness;

namespace Modules.Pipeline.IntegrationTests;

/// <summary>Spec 2026-10-15 §7: con varios negocios abiertos del cliente, un pedido sin cotización deja la foto suelta y el
/// asesor elige desde el negocio (como D27). El detalle la muestra entre los pedidos sueltos del cliente y enlazarla gana el
/// negocio, porque un pedido es una venta. Las fotos se siembran con el mismo <see cref="QuotationEventApplier"/> que usa el
/// worker.</summary>
public sealed class DealOrdersApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task OrderCreatedAsync(QepApiFactory factory, Guid tenantId, Guid clientId, Guid orderId, decimal total)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<QuotationEventApplier>().ApplyOrderCreatedAsync(
            new OrderFact(tenantId, orderId, "PED-0001", clientId, Guid.CreateVersion7(), total, "COP", "Shopify", null),
            DateTimeOffset.UtcNow, Ct);
        await scope.ServiceProvider.GetRequiredService<IPipelineUnitOfWork>().SaveChangesAsync(Ct);
    }

    private static async Task<DealDetailDto> CreateDealAsync(HttpClient client, Guid tenantId, Guid customerId, string title)
    {
        var response = await client.PostAsJsonAsync($"{PipelineUrl(tenantId)}/deals", new CreateDealRequest(customerId, title, null, null, null, null), Ct);
        return (await response.Content.ReadFromJsonAsync<DealDetailDto>(Ct))!;
    }

    private static Task<HttpResponseMessage> LinkAsync(HttpClient client, Guid tenantId, Guid dealId, Guid orderId, long version) =>
        client.PostAsJsonAsync($"{PipelineUrl(tenantId)}/deals/{dealId}/orders/{orderId}/link", new DealVersionRequest(version), Ct);

    [Fact]
    public async Task LinkingALooseOrderWinsTheDeal()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        var (customerId, _) = await SeedCustomerAsync(factory, tenant.TenantId, "Laura");
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var first = await CreateDealAsync(client, tenant.TenantId, customerId, "Uno");
        var second = await CreateDealAsync(client, tenant.TenantId, customerId, "Dos");
        var orderId = Guid.CreateVersion7();
        await OrderCreatedAsync(factory, tenant.TenantId, customerId, orderId, 500m);

        var before = (await client.GetFromJsonAsync<DealDetailDto>($"{PipelineUrl(tenant.TenantId)}/deals/{first.Id}", Ct))!;
        var linked = await LinkAsync(client, tenant.TenantId, first.Id, orderId, before.Version);
        var detail = (await linked.Content.ReadFromJsonAsync<DealDetailDto>(Ct))!;
        var other = (await client.GetFromJsonAsync<DealDetailDto>($"{PipelineUrl(tenant.TenantId)}/deals/{second.Id}", Ct))!;

        Assert.Equal(orderId, Assert.Single(before.UnlinkedCustomerOrders).OrderId);
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        Assert.Equal(("Won", (decimal?)500m), (detail.StageKind, detail.Amount));
        Assert.Equal(orderId, Assert.Single(detail.Orders).OrderId);
        Assert.Empty(detail.UnlinkedCustomerOrders);
        Assert.Equal("Open", other.StageKind);
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM audit.entries WHERE tenant_id = @t AND action = 'pipeline.deal.order_linked' AND resource_id = @d",
            ("t", tenant.TenantId), ("d", first.Id.ToString())));
    }

    [Fact]
    public async Task ForeignUnknownAndAlreadyLinkedOrdersAre422()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnablePipelineAsync(connectionString, tenant.TenantId);
        var (customerId, _) = await SeedCustomerAsync(factory, tenant.TenantId, "Laura");
        var (otherCustomer, _) = await SeedCustomerAsync(factory, tenant.TenantId, "Pedro");
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var deal = await CreateDealAsync(client, tenant.TenantId, customerId, "Uno");
        await CreateDealAsync(client, tenant.TenantId, otherCustomer, "Otro A");
        await CreateDealAsync(client, tenant.TenantId, otherCustomer, "Otro B");
        var foreignOrder = Guid.CreateVersion7();
        await OrderCreatedAsync(factory, tenant.TenantId, otherCustomer, foreignOrder, 700m);
        // Con un solo negocio abierto del cliente el aplicador enlaza solo: ese pedido ya tiene negocio.
        var (lonelyCustomer, _) = await SeedCustomerAsync(factory, tenant.TenantId, "Ana");
        var lonelyOrder = Guid.CreateVersion7();
        await OrderCreatedAsync(factory, tenant.TenantId, lonelyCustomer, lonelyOrder, 300m);
        var lonelyDeal = await CreateDealAsync(client, tenant.TenantId, lonelyCustomer, "Ana");

        var mismatch = await LinkAsync(client, tenant.TenantId, deal.Id, foreignOrder, deal.Version);
        var unknown = await LinkAsync(client, tenant.TenantId, deal.Id, Guid.CreateVersion7(), deal.Version);
        var taken = await LinkAsync(client, tenant.TenantId, lonelyDeal.Id, lonelyOrder, lonelyDeal.Version);

        Assert.Equal(PipelineErrorCodes.DealOrderCustomerMismatch, (await ProblemAsync(mismatch)).Code);
        Assert.Equal(PipelineErrorCodes.DealOrderNotFound, (await ProblemAsync(unknown)).Code);
        Assert.Equal(PipelineErrorCodes.DealOrderAlreadyLinked, (await ProblemAsync(taken)).Code);
    }
}
