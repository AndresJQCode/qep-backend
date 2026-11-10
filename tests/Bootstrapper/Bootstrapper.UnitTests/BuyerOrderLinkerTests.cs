using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;

namespace Bootstrapper.UnitTests;

/// <summary>Buyers → Quotations para el alta diferida: el enlace va al gateway en un scope propio, con los ids tal cual.</summary>
public sealed class BuyerOrderLinkerTests
{
    private sealed class RecordingGateway : IBuyerOrderGateway
    {
        public List<(Guid TenantId, Guid OrderId, Guid AccountId)> Links { get; } = [];

        public Task<BuyerOrderPage> ListAsync(Guid tenantId, Guid buyerAccountId, int page, int pageSize, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BuyerOrderDetail?> FindAsync(Guid tenantId, Guid buyerAccountId, Guid orderId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BuyerOrderDetail?> CancelAsync(Guid tenantId, Guid buyerAccountId, Guid orderId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> LinkAsync(Guid tenantId, Guid orderId, Guid buyerAccountId, CancellationToken cancellationToken)
        {
            Links.Add((tenantId, orderId, buyerAccountId));
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task LinkForwardsTheIdsToTheGatewayInItsOwnScope()
    {
        var gateways = new List<RecordingGateway>();
        var services = new ServiceCollection()
            .AddScoped<IBuyerOrderGateway>(_ =>
            {
                var gateway = new RecordingGateway();
                gateways.Add(gateway);
                return gateway;
            })
            .BuildServiceProvider();
        var linker = new BuyerOrderLinker(services.GetRequiredService<IServiceScopeFactory>());
        var (tenant, order, account) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        await linker.LinkAsync(tenant, order, account, TestContext.Current.CancellationToken);
        await linker.LinkAsync(tenant, order, account, TestContext.Current.CancellationToken);

        Assert.Equal(2, gateways.Count);
        Assert.All(gateways, gateway => Assert.Equal((tenant, order, account), Assert.Single(gateway.Links)));
    }
}
