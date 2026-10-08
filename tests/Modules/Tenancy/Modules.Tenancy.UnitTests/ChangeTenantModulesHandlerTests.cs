using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>Spec 2026-10-08 §5, <c>POST …/modules/changes</c>.</summary>
public sealed class ChangeTenantModulesHandlerTests
{
    private static readonly TenantId Operator = TenantId.New();
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 15, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public List<string> Steps { get; } = [];
        public Tenant Target { get; } = Tenant.Create(TenantId.New(), "origen-botanico", "Origen Botánico", "es-CO",
            "America/Bogota", "yyyy-MM-dd", MembershipId.New(), Now.AddDays(-30));
        public StepsTenantModuleRepository Modules { get; }
        public RecordingTenantChangeRepository Changes { get; } = new();
        public RecordingAuditRecorder Audit { get; } = new();
        public FixedOperatorTenantReader Reader { get; } = new();
        public OperatorContext Context { get; } = new(Operator, OperatorPermissions.ModulesManage);

        public Fixture()
        {
            Modules = new StepsTenantModuleRepository(Steps, TenantModuleKeys.DefaultForNewTenants
                .Select(key => TenantModule.Create(Target.Id, key, TenantModuleSources.Signup, Now.AddDays(-30), null))
                .ToArray());
            Reader.Snapshots[Target.Id] = OperatorSnapshots.Of(Target.Id, OperatorSnapshots.Signup());
        }

        public ChangeTenantModulesHandler Handler(IExecutionContext? context = null) =>
            new(new StepsTenantRepository(Steps, Target), Modules, Changes, new StepsUnitOfWork(Steps), Audit,
                context ?? Context, new FixedOperatorTenant(Operator.Value), Reader, new FixedClock(Now),
                new ChangeTenantModulesValidator());

        public Task<OperatorTenantDetailDto> SendAsync(string reason, string? note, params (string? Key, string? Status)[] changes) =>
            Handler().HandleAsync(Command(Target.Id, reason, note, changes), TestContext.Current.CancellationToken);
    }

    private static ChangeTenantModulesCommand Command(
        TenantId target, string? reason, string? note, params (string? Key, string? Status)[] changes) =>
        new(Operator, target, changes.Select(change => new TenantModuleChangeInput(change.Key, change.Status)).ToArray(),
            reason, note);

    [Fact]
    public async Task ANonOperatorIsForbiddenBeforeTakingTheLockOrLookingUpTheTarget()
    {
        var fixture = new Fixture();

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            fixture.Handler(new OperatorContext(Operator, OperatorPermissions.TenantsRead))
                .HandleAsync(Command(TenantId.New(), "contract", null, ("pos", "active")), TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Steps);
    }

    // §3: sin el candado antes de leer, dos operadores pasarían la validación a la vez (Review Focus 3).
    [Fact]
    public async Task TheLockIsTakenBeforeReadingTheState()
    {
        var fixture = new Fixture();

        await fixture.SendAsync("cancellation", null, ("reporting", "inactive"));

        Assert.Equal($"lock:{fixture.Target.Id}", fixture.Steps[0]);
        Assert.Equal(["save", "commit"], fixture.Steps.TakeLast(2));
    }

    [Fact]
    public async Task AMissingTargetIsNotFoundAfterAuthorization()
    {
        var fixture = new Fixture();

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            fixture.Handler().HandleAsync(Command(TenantId.New(), "contract", null, ("pos", "active")), TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.tenant.not_found", error.Code);
    }

    [Fact]
    public async Task ACascadeWritesRowsHistoryAndAuditWithOneBatchId()
    {
        var fixture = new Fixture();

        await fixture.SendAsync("cancellation", "No lo usan por ahora",
            ("orders", "inactive"), ("customers", "inactive"), ("quotations", "inactive"));

        Assert.All(fixture.Modules.Rows.Where(row => row.ModuleKey == TenantModuleKeys.Customers
                || row.ModuleKey == TenantModuleKeys.Quotations || row.ModuleKey == TenantModuleKeys.Orders),
            row => Assert.Equal((TenantModuleStatus.Inactive, Now), (row.Status, row.StatusChangedAt)));
        Assert.Equal(3, fixture.Changes.Added.Count);
        Assert.Single(fixture.Changes.Added.Select(change => change.BatchId).Distinct());
        Assert.All(fixture.Changes.Added, change =>
            Assert.Equal(("active", "inactive", ChangeReason.Cancellation, "No lo usan por ahora", fixture.Context.SubjectId),
                (change.FromStatus, change.ToStatus, change.Reason, change.Note, change.ActorUserId)));
        var audit = Assert.Single(fixture.Audit.Entries);
        Assert.Equal((fixture.Target.Id.Value, "tenancy.tenant_modules.changed", "tenant", fixture.Target.Id.Value.ToString()),
            (audit.TenantId, audit.Action, audit.ResourceType, audit.ResourceId));
        Assert.Equal(
            ["customers:active->inactive", "quotations:active->inactive", "orders:active->inactive", "reason:cancellation"],
            audit.ChangedFields);
    }

    [Fact]
    public async Task ActivatingAKeyWithoutRowCreatesItFromTheConsole()
    {
        var fixture = new Fixture();

        await fixture.SendAsync("contract", null, ("pos", "active"));

        var created = Assert.Single(fixture.Modules.Added);
        Assert.Equal((TenantModuleKeys.Pos, TenantModuleSources.Operator, TenantModuleStatus.Active, (string?)null),
            (created.ModuleKey, created.Source, created.Status, created.Note));
        Assert.Null(Assert.Single(fixture.Changes.Added).FromStatus);
        Assert.Equal(["pos:none->active", "reason:contract"], Assert.Single(fixture.Audit.Entries).ChangedFields);
    }

    [Fact]
    public async Task AnInconsistentBatchWritesNothing()
    {
        var fixture = new Fixture();

        var error = await Assert.ThrowsAsync<TenantDomainException>(() =>
            fixture.SendAsync("cancellation", null, ("customers", "inactive")));

        Assert.Equal("tenancy.modules.inconsistent_dependencies", error.Code);
        Assert.Empty(fixture.Changes.Added);
        Assert.Empty(fixture.Audit.Entries);
        Assert.DoesNotContain("save", fixture.Steps);
        Assert.All(fixture.Modules.Rows, row => Assert.Equal(TenantModuleStatus.Active, row.Status));
    }

    // Review Focus 1 y 5: texto desconocido es 422 validation.failed, nunca el ArgumentException de Parse.
    public static TheoryData<string?, string?, string?, string?> InvalidShapes() => new()
    {
        { "POS", "active", "contract", null },
        { "inventory", "active", "contract", null },
        { "pos", "on", "contract", null },
        { "pos", "active", "refund", null },
        { "pos", "active", "Contract", null },
        { "pos", "active", "contract", new string('x', 301) },
    };

    [Theory]
    [MemberData(nameof(InvalidShapes))]
    public async Task AnInvalidShapeIsAValidationError(string? key, string? status, string? reason, string? note)
    {
        var fixture = new Fixture();

        await Assert.ThrowsAsync<ValidationException>(() =>
            fixture.Handler().HandleAsync(Command(fixture.Target.Id, reason, note, (key, status)), TestContext.Current.CancellationToken));
        Assert.DoesNotContain("save", fixture.Steps);
    }

    [Fact]
    public async Task ANullItemOrAnEmptyOrOversizedBatchIsAValidationError()
    {
        var fixture = new Fixture();
        ChangeTenantModulesCommand[] commands =
        [
            new(Operator, fixture.Target.Id, [null!], "contract", null),   // [null] en el JSON llega así
            new(Operator, fixture.Target.Id, [], "contract", null),
            new(Operator, fixture.Target.Id, null, "contract", null),
            new(Operator, fixture.Target.Id, Enumerable.Range(0, 8).Select(_ => new TenantModuleChangeInput("pos", "active")).ToArray(), "contract", null),
        ];

        foreach (var command in commands)
        {
            await Assert.ThrowsAsync<ValidationException>(() =>
                fixture.Handler().HandleAsync(command, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ANoteOfExactlyThreeHundredPasses()
    {
        var fixture = new Fixture();

        await fixture.SendAsync("contract", new string('x', 300), ("pos", "active"));

        Assert.Equal(300, Assert.Single(fixture.Changes.Added).Note!.Length);
    }
}
