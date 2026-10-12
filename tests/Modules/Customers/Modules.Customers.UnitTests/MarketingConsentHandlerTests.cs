using BuildingBlocks.Application;
using Modules.Customers.Application;
using Modules.Customers.Domain;
using static Modules.Customers.UnitTests.EngagementTestBed;

namespace Modules.Customers.UnitTests;

/// <summary>Spec 2026-10-11 §4.2: <c>PUT /customers/{id}/marketing-consent</c>.</summary>
public sealed class MarketingConsentHandlerTests
{
    private readonly FakeCustomers _customers = new();
    private readonly RecordingAudit _audit = new();
    private readonly CountingUnitOfWork _unitOfWork = new();
    private readonly Customer _customer = NewCustomer();

    public MarketingConsentHandlerTests()
    {
        _customers.Items.Add(_customer);
    }

    private SetMarketingConsentHandler Handler(Guid tenant, params string[] permissions) =>
        new(_customers, _unitOfWork, _audit, new Context(tenant, UserA, permissions), new FixedClock());

    [Fact]
    public async Task OptingOutIsManualRecordedAndAudited()
    {
        var dto = await Handler(TenantId, Manager).HandleAsync(
            new SetMarketingConsentCommand(TenantId, _customer.Id.Value, true), TestContext.Current.CancellationToken);

        Assert.Equal(new MarketingConsentDto(true, Now, "manual"), dto);
        Assert.True(_customer.MarketingOptOut);
        Assert.Equal(1, _unitOfWork.Saves);
        var recorded = Assert.Single(_audit.Events);
        Assert.Equal(("customers.customer.marketing_consent_changed", _customer.Id.ToString()), (recorded.Action, recorded.ResourceId));
    }

    [Fact]
    public async Task OptingBackInClearsTheRecord()
    {
        _customer.OptOutOfMarketing(MarketingConsentSource.Meta, Now.AddDays(-3));

        var dto = await Handler(TenantId, Manager).HandleAsync(
            new SetMarketingConsentCommand(TenantId, _customer.Id.Value, false), TestContext.Current.CancellationToken);

        Assert.Equal(new MarketingConsentDto(false, null, null), dto);
        Assert.Single(_audit.Events);
    }

    [Fact]
    public async Task RepeatingTheSameValueChangesNothingAndLeavesNoAuditTrail()
    {
        _customer.OptOutOfMarketing(MarketingConsentSource.Meta, Now.AddDays(-3));

        var dto = await Handler(TenantId, Manager).HandleAsync(
            new SetMarketingConsentCommand(TenantId, _customer.Id.Value, true), TestContext.Current.CancellationToken);

        Assert.Equal(new MarketingConsentDto(true, Now.AddDays(-3), "meta"), dto);
        Assert.Empty(_audit.Events);
        Assert.Equal(0, _unitOfWork.Saves);
    }

    [Fact]
    public async Task AnotherTenantIsForbiddenAndReadOnlyIsForbidden()
    {
        await Assert.ThrowsAsync<RequestForbiddenException>(() => Handler(OtherTenantId, Manager).HandleAsync(
            new SetMarketingConsentCommand(TenantId, _customer.Id.Value, true), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<RequestForbiddenException>(() => Handler(TenantId, CustomersPermissions.CustomerRead).HandleAsync(
            new SetMarketingConsentCommand(TenantId, _customer.Id.Value, true), TestContext.Current.CancellationToken));
        Assert.False(_customer.MarketingOptOut);
    }

    [Fact]
    public async Task AMissingCustomerIs404()
    {
        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => Handler(TenantId, Manager).HandleAsync(
            new SetMarketingConsentCommand(TenantId, Guid.NewGuid(), true), TestContext.Current.CancellationToken));

        Assert.Equal("customers.customer.not_found", error.Code);
    }
}
