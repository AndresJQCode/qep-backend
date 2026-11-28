using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Purchases.Application;
using Modules.Purchases.Infrastructure.Persistence;
using static Modules.Purchases.IntegrationTests.PurchasesApiHarness;

namespace Modules.Purchases.IntegrationTests;

/// <summary>Recibir una orden escribe <c>purchases.order.received.v1</c> en el outbox, en la misma transacción que el cambio.</summary>
public sealed class PurchaseOrderReceivedOutboxTests(SharedPurchasesHost host) : IClassFixture<SharedPurchasesHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReceiveWritesTheEventInTheSameTransaction()
    {
        using var scenario = await NewScenarioAsync(host.Factory);
        var draft = await CreateOrderAsync(
            scenario.Manager, scenario.TenantId, OrderBody(scenario.SupplierId, (scenario.VariantA, 2m, 100m), (scenario.VariantB, 3m, 50m)));
        var sent = await TransitionAsync(scenario.Manager, scenario.TenantId, draft, "send");
        var confirmed = await ConfirmAsIsAsync(scenario.Manager, scenario.TenantId, sent);

        var received = await TransitionAsync(scenario.Manager, scenario.TenantId, confirmed, "receive");

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PurchasesDbContext>();
        var rows = await dbContext.Outbox
            .AsNoTracking()
            .Where(row => row.EventName == PurchaseOrderReceivedEvent.EventName)
            .ToListAsync(Ct);
        var row = Assert.Single(rows, candidate =>
            JsonDocument.Parse(candidate.PayloadJson).RootElement.GetProperty("purchaseOrderId").GetGuid() == received.Id);
        var payload = JsonSerializer.Deserialize<PurchaseOrderReceivedEvent>(row.PayloadJson)!;
        Assert.Equal((scenario.TenantId, received.Id, PurchaseOrderEventOrigins.Manual), (payload.tenantId, payload.purchaseOrderId, payload.origin));
        Assert.Equal(received.Version, payload.version);
        Assert.Equal(
            [(scenario.VariantA, 2m), (scenario.VariantB, 3m)],
            payload.lines.Select(line => (line.variantId!.Value, line.quantity)).ToArray());
        // La correlación del outbox es el id de la orden.
        var correlation = await dbContext.Database
            .SqlQuery<string>($"SELECT correlation_id AS \"Value\" FROM platform.outbox_messages WHERE id = {row.Id}")
            .SingleAsync(Ct);
        Assert.Equal(received.Id.ToString("D"), correlation);
    }
}
