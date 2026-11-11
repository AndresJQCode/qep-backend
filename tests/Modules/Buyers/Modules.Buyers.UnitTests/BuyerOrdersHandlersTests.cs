using BuildingBlocks.Application;
using FluentValidation;
using Modules.Buyers.Application;

namespace Modules.Buyers.UnitTests;

public sealed class BuyerOrdersHandlersTests
{
    private const string Host = "acme.lulocrm.com";
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task ListPassesTheStorefrontTenantAndTheSessionAccount()
    {
        var (handlerFor, orders, token, accountId) = await ArrangeAsync();

        await handlerFor.List.HandleAsync(new ListBuyerOrdersQuery(Host, token, 2, 20), TestContext.Current.CancellationToken);

        Assert.Equal((TenantId, accountId, 2, 20), orders.LastList);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 51)]
    public async Task ListRejectsPageAndPageSizeOutOfRange(int page, int pageSize)
    {
        var (handlerFor, orders, _, _) = await ArrangeAsync();

        await Assert.ThrowsAsync<ValidationException>(() => handlerFor.List.HandleAsync(
            new ListBuyerOrdersQuery(Host, "no-se-consulta", page, pageSize), TestContext.Current.CancellationToken));

        Assert.Equal(0, orders.Calls);
    }

    [Fact]
    public async Task ListWithoutAValidSessionIs401()
    {
        var (handlerFor, orders, _, _) = await ArrangeAsync();

        var ex = await Assert.ThrowsAsync<RequestUnauthorizedException>(() => handlerFor.List.HandleAsync(
            new ListBuyerOrdersQuery(Host, null, 1, 20), TestContext.Current.CancellationToken));

        Assert.Equal("store.buyer.session_invalid", ex.Code);
        Assert.Equal(0, orders.Calls);
    }

    [Fact]
    public async Task GetMapsANullFromThePortToOrderNotFound()
    {
        var (handlerFor, orders, token, _) = await ArrangeAsync();
        orders.Detail = null;

        var ex = await Assert.ThrowsAsync<ResourceNotFoundException>(() => handlerFor.Get.HandleAsync(
            new GetBuyerOrderQuery(Host, token, Guid.NewGuid()), TestContext.Current.CancellationToken));

        Assert.Equal("order.order.not_found", ex.Code);
    }

    [Fact]
    public async Task CancelMapsANullFromThePortToOrderNotFound()
    {
        var (handlerFor, orders, token, _) = await ArrangeAsync();
        orders.Detail = null;

        var ex = await Assert.ThrowsAsync<ResourceNotFoundException>(() => handlerFor.Cancel.HandleAsync(
            new CancelBuyerOrderCommand(Host, token, Guid.NewGuid()), TestContext.Current.CancellationToken));

        Assert.Equal("order.order.not_found", ex.Code);
    }

    [Fact]
    public async Task CancelLetsTheConflictThrough()
    {
        var (handlerFor, orders, token, _) = await ArrangeAsync();
        orders.Failure = new ResourceConflictException("order.order.not_cancellable_by_buyer", "No.");

        var ex = await Assert.ThrowsAsync<ResourceConflictException>(() => handlerFor.Cancel.HandleAsync(
            new CancelBuyerOrderCommand(Host, token, Guid.NewGuid()), TestContext.Current.CancellationToken));

        Assert.Same(orders.Failure, ex);
    }

    [Fact]
    public async Task ASessionOfAnotherHostIs401()
    {
        var (handlerFor, orders, _, accountId) = await ArrangeAsync();
        var (otherToken, _) = await handlerFor.Sessions.IssueAsync(
            accountId, "otra.lulocrm.com", null, TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<RequestUnauthorizedException>(() => handlerFor.Get.HandleAsync(
            new GetBuyerOrderQuery(Host, otherToken, Guid.NewGuid()), TestContext.Current.CancellationToken));

        Assert.Equal("store.buyer.session_invalid", ex.Code);
        Assert.Equal(0, orders.Calls);
    }

    private static async Task<(Handlers Handlers, RecordingBuyerOrders Orders, string Token, Guid AccountId)> ArrangeAsync()
    {
        var world = new BuyersWorld();
        var accountId = Guid.NewGuid();
        var sessions = new BuyerSessions(world.Sessions, world.UnitOfWork, world.Clock);
        var (token, _) = await sessions.IssueAsync(accountId, Host, null, TestContext.Current.CancellationToken);
        var orders = new RecordingBuyerOrders();
        return (new Handlers(sessions, orders), orders, token, accountId);
    }

    private sealed class Handlers
    {
        private readonly FixedStorefronts _storefronts = new();

        public Handlers(BuyerSessions sessions, IBuyerOrders orders)
        {
            Sessions = sessions;
            List = new ListBuyerOrdersHandler(_storefronts, sessions, orders, new ListBuyerOrdersQueryValidator());
            Get = new GetBuyerOrderHandler(_storefronts, sessions, orders);
            Cancel = new CancelBuyerOrderHandler(_storefronts, sessions, orders);
        }

        public BuyerSessions Sessions { get; }
        public ListBuyerOrdersHandler List { get; }
        public GetBuyerOrderHandler Get { get; }
        public CancelBuyerOrderHandler Cancel { get; }
    }

    private sealed class FixedStorefronts : IBuyerStorefronts
    {
        public Task<BuyerStorefront> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(new BuyerStorefront(host.Trim().ToLowerInvariant(), "Tienda Demo", TenantId));
    }

    private sealed class RecordingBuyerOrders : IBuyerOrders
    {
        public int Calls { get; private set; }
        public (Guid, Guid, int, int)? LastList { get; private set; }
        public BuyerOrderDetailDto? Detail { get; set; } = Sample();
        public Exception? Failure { get; set; }

        public Task<BuyerOrderPageDto> ListAsync(Guid tenantId, Guid accountId, int page, int pageSize, CancellationToken cancellationToken)
        {
            Calls++;
            LastList = (tenantId, accountId, page, pageSize);
            return Task.FromResult(new BuyerOrderPageDto([], page, pageSize, 0));
        }

        public Task<BuyerOrderDetailDto?> FindAsync(Guid tenantId, Guid accountId, Guid orderId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Detail);
        }

        public Task<BuyerOrderDetailDto?> CancelAsync(Guid tenantId, Guid accountId, Guid orderId, CancellationToken cancellationToken)
        {
            Calls++;
            return Failure is null ? Task.FromResult(Detail) : Task.FromException<BuyerOrderDetailDto?>(Failure);
        }

        private static BuyerOrderDetailDto Sample() =>
            new(Guid.NewGuid(), "P-1", DateTimeOffset.UnixEpoch, "New", "Pending", 10m, "COP", 1, "Camisa", [], null, true,
                10m, 0m, 0m, false, null, "FinalConsumer", null, null, null);
    }
}
