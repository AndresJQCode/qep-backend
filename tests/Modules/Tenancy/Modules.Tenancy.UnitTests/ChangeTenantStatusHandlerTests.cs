using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>Spec 2026-10-08 §4 y §5, <c>POST …/status</c>.</summary>
public sealed class ChangeTenantStatusHandlerTests
{
    private static readonly TenantId Operator = TenantId.New();
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 15, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public List<string> Steps { get; } = [];
        public Tenant Target { get; } = Tenant.Create(TenantId.New(), "origen-botanico", "Origen Botánico", "es-CO",
            "America/Bogota", "yyyy-MM-dd", MembershipId.New(), Now.AddDays(-30));
        public RecordingTenantChangeRepository Changes { get; } = new();
        public RecordingAuditRecorder Audit { get; } = new();
        public FixedOperatorTenantReader Reader { get; } = new();
        public OperatorContext Context { get; } = new(Operator, OperatorPermissions.TenantsManage);

        public Fixture() => Reader.Snapshots[Target.Id] = OperatorSnapshots.Of(Target.Id, OperatorSnapshots.Signup());

        public ChangeTenantStatusHandler Handler(IExecutionContext? context = null) =>
            new(new StepsTenantRepository(Steps, Target), Changes, new StepsUnitOfWork(Steps), Audit, context ?? Context,
                new FixedOperatorTenant(Operator.Value), Reader, new FixedClock(Now), new ChangeTenantStatusValidator());

        public Task<OperatorTenantDetailDto> SendAsync(
            string? status, string? reason, long version = 1, TenantId? target = null, string? note = "Factura de septiembre") =>
            Handler().HandleAsync(new ChangeTenantStatusCommand(Operator, target ?? Target.Id, status, reason, note, version),
                TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SuspendingWritesHistoryAndAuditAndBumpsTheVersion()
    {
        var fixture = new Fixture();

        await fixture.SendAsync("inactive", "nonpayment");

        Assert.Equal((TenantStatus.Suspended, 2L), (fixture.Target.Status, fixture.Target.Version));
        var change = Assert.Single(fixture.Changes.Added);
        Assert.Equal((TenantChangeKind.TenantStatus, "Active", "Suspended", ChangeReason.Nonpayment, "Factura de septiembre"),
            (change.Kind, change.FromStatus, change.ToStatus, change.Reason, change.Note));
        var audit = Assert.Single(fixture.Audit.Entries);
        Assert.Equal((fixture.Target.Id.Value, "tenancy.tenant.status_changed", "tenant"), (audit.TenantId, audit.Action, audit.ResourceType));
        Assert.Equal(["status:Active->Suspended", "reason:nonpayment"], audit.ChangedFields);
        Assert.Contains("save", fixture.Steps);
    }

    [Fact]
    public async Task ReactivatingReturnsToActive()
    {
        var fixture = new Fixture();
        fixture.Target.Suspend(ChangeReason.Nonpayment, Now);

        await fixture.SendAsync("active", "contract", version: 2);

        Assert.Equal(TenantStatus.Active, fixture.Target.Status);
        Assert.Equal("Active", Assert.Single(fixture.Changes.Added).ToStatus);
    }

    // §4: antes de tocar el agregado; el dominio no conoce la configuración.
    [Fact]
    public async Task TheOperatorTenantCannotBeSuspended()
    {
        var fixture = new Fixture();

        var error = await Assert.ThrowsAsync<TenantDomainException>(() => fixture.SendAsync("inactive", "nonpayment", target: Operator));

        Assert.Equal("tenancy.tenant.operator_cannot_be_suspended", error.Code);
        Assert.Empty(fixture.Steps);
    }

    [Fact]
    public async Task AStaleVersionIsAConcurrencyConflict()
    {
        var fixture = new Fixture();

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() => fixture.SendAsync("inactive", "nonpayment", version: 7));

        Assert.Equal("concurrency.conflict", error.Code);
        Assert.Empty(fixture.Changes.Added);
    }

    [Fact]
    public async Task ANonOperatorIsForbiddenBeforeLookingUpTheTarget()
    {
        var fixture = new Fixture();

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            fixture.Handler(new OperatorContext(Operator, OperatorPermissions.ModulesManage))
                .HandleAsync(new ChangeTenantStatusCommand(Operator, TenantId.New(), "inactive", "nonpayment", null, 1),
                    TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Steps);
    }

    [Fact]
    public async Task AMissingTargetIsNotFound()
    {
        var fixture = new Fixture();

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            fixture.SendAsync("inactive", "nonpayment", target: TenantId.New()));

        Assert.Equal("tenancy.tenant.not_found", error.Code);
    }

    [Theory]
    [InlineData("paused", "nonpayment")]
    [InlineData("Inactive", "nonpayment")]
    [InlineData("inactive", "refund")]
    [InlineData(null, "nonpayment")]
    [InlineData("inactive", null)]
    public async Task AnUnknownStatusOrReasonIsAValidationError(string? status, string? reason) =>
        await Assert.ThrowsAsync<ValidationException>(() => new Fixture().SendAsync(status, reason));

    // Review Focus 5: el mismo límite que en modules/changes, en las dos escrituras.
    [Fact]
    public async Task ANoteOverThreeHundredIsAValidationErrorAndThreeHundredPasses()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            new Fixture().SendAsync("inactive", "nonpayment", note: new string('x', 301)));

        var fixture = new Fixture();
        await fixture.SendAsync("inactive", "nonpayment", note: new string('x', 300));
        Assert.Equal(300, Assert.Single(fixture.Changes.Added).Note!.Length);
    }

    [Fact]
    public async Task AReasonOfTheOtherDirectionIsADomainError() =>
        Assert.Equal("tenancy.tenant.reason_not_allowed",
            (await Assert.ThrowsAsync<TenantDomainException>(() => new Fixture().SendAsync("inactive", "contract"))).Code);
}
