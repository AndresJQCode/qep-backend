using BuildingBlocks.Application;
using Modules.Store.Application;
using Modules.Store.Domain;
using Modules.Tenancy.Application;
using static Modules.Store.UnitTests.StoreTestData;

namespace Modules.Store.UnitTests;

/// <summary>
/// «Descartar» no puede dejar huérfano un pedido que «Tomar» ya creó (revisión final, I-1): rechaza cualquier lease vivo y, si el
/// pedido de la solicitud existe, la marca convertida en vez de descartarla.
/// </summary>
public sealed class DiscardOrderRequestHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DiscardOrderRequestCommand Command(Guid requestId) => new(TenantId, requestId, "Spam", null);

    [Fact]
    public async Task ALiveLeaseOfTheSameMemberIs409BeingTakenAndNothingIsWritten()
    {
        var rig = new DiscardRig();
        rig.Request.BeginConversion(Member, Now, Lease);

        var error = await Assert.ThrowsAsync<ResourceConflictException>(
            () => rig.Handler().HandleAsync(Command(rig.Request.Id), Ct));

        Assert.Equal(StoreErrorCodes.BeingTaken, error.Code);
        Assert.Equal(StoreOrderRequestStatus.New, rig.Request.Status);
        Assert.Equal(0, rig.UnitOfWork.Saves);
        Assert.Empty(rig.Audit.Entries);
        Assert.Equal(0, rig.Orders.FindCalls);
    }

    [Fact]
    public async Task WhenTheOrderAlreadyExistsTheRequestIsConvertedNotDiscarded()
    {
        var rig = new DiscardRig();
        var order = new StoreOrderCreated(Guid.NewGuid(), "P-9", 20000m, Guid.NewGuid(), OtherMember, AlreadyExisted: true);
        rig.Orders.Existing = order;

        var error = await Assert.ThrowsAsync<ResourceConflictException>(
            () => rig.Handler().HandleAsync(Command(rig.Request.Id), Ct));

        Assert.Equal(StoreErrorCodes.NotNew, error.Code);
        Assert.Equal(StoreOrderRequestStatus.Converted, rig.Request.Status);
        Assert.Equal(order.OrderId, rig.Request.OrderId);
        Assert.Equal(OtherMember, rig.Request.ConvertedBy);
        Assert.Equal(1, rig.UnitOfWork.Saves);
        Assert.Equal(StoreAuditActions.RequestConverted, Assert.Single(rig.Audit.Entries).Action);
    }

    [Fact]
    public async Task WithoutALeaseOrAnOrderTheRequestIsDiscarded()
    {
        var rig = new DiscardRig();

        var row = await rig.Handler().HandleAsync(Command(rig.Request.Id), Ct);

        Assert.Equal("Discarded", row.Status);
        Assert.Equal(StoreAuditActions.RequestDiscarded, Assert.Single(rig.Audit.Entries).Action);
    }

    private sealed class DiscardRig
    {
        public StoreOrderRequest Request { get; } = StoreTestData.Request();

        public FakeUnitOfWork UnitOfWork { get; } = new(new CallLog());

        public FakeAudit Audit { get; } = new();

        public FakeOrders Orders { get; } = new();

        public DiscardOrderRequestHandler Handler() =>
            new(
                new FakeExecutionContext(TenantId, StorePermissions.OrderManage),
                FakeTenantModules.AllEnabled(),
                new FakeMembers(),
                new FakeRequests(Request),
                UnitOfWork,
                Audit,
                new FakeTenantClock(),
                new OrderRequestRowComposer(new FakeMembers(), new FakeCustomers()),
                Orders,
                new DiscardOrderRequestValidator());
    }

    private sealed class FakeOrders : IStoreOrders
    {
        public StoreOrderCreated? Existing { get; set; }

        public int FindCalls { get; private set; }

        public Task<StoreOrderCreated?> FindExistingAsync(Guid tenantId, Guid requestId, CancellationToken cancellationToken)
        {
            FindCalls++;
            return Task.FromResult(Existing);
        }

        public Task<StoreQuoteView> QuoteAsync(
            Guid tenantId,
            string currency,
            StorePricingMode mode,
            IReadOnlyList<(Guid VariantId, int Quantity)> lines,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StoreOrderCreated> CreateAsync(StoreOrderDraft draft, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeMembers : IStoreMemberDirectory
    {
        public Task<(Guid MembershipId, Guid UserId)> ResolveCallerAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult((Member, Member));

        public Task<IReadOnlyDictionary<Guid, string>> NamesAsync(
            Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(memberIds.ToDictionary(id => id, _ => "Asesor"));
    }

    private sealed class FakeCustomers : IStoreCustomerDirectory
    {
        public Task<IReadOnlySet<int>> KnownAsync(
            Guid tenantId,
            IReadOnlyCollection<(string PhoneE164, string? Type, string? Number)> keys,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());

        public Task<IReadOnlyList<StoreCustomerCandidate>> FindCandidatesAsync(
            Guid tenantId, string phoneE164, string? identificationType, string? identificationNumber, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StoreCustomerRef?> FindEligibleAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<(StoreCustomerRef Customer, bool Created)> CreateAsync(
            Guid tenantId, Guid actorUserId, StoreNewCustomer data, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StoreCustomerRef> CompleteAsync(
            Guid tenantId, Guid actorUserId, Guid customerId, StoreNewCustomer billing, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StoreAddressOutcome>> EnsureAddressesAsync(
            Guid tenantId, Guid customerId, IReadOnlyList<StoreAddressInput> addresses, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeRequests(StoreOrderRequest request) : IStoreOrderRequestRepository
    {
        public void Add(StoreOrderRequest value) => throw new NotSupportedException();

        public Task<StoreOrderRequest?> FindAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<StoreOrderRequest?>(id == request.Id ? request : null);

        public Task<StoreOrderRequest?> FindByClientRequestIdAsync(Guid tenantId, Guid clientRequestId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<(IReadOnlyList<StoreOrderRequest> Items, int Total)> ListAsync(
            StoreInboxFilter filter, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StoreInboxCounts> CountAsync(
            Guid tenantId,
            Guid callerMemberId,
            DateTimeOffset todayStartUtc,
            DateTimeOffset todayEndUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> CountReceivedSinceAsync(Guid tenantId, DateTimeOffset since, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StoreOrderRequest?> FindByOrderAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeTenantClock : ITenantClock
    {
        public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new TenantCalendar(Now, TimeZoneInfo.Utc));
    }
}
