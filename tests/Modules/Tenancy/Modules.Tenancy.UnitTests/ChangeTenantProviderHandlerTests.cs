using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>Plan 2026-10-21, Task 3: <c>POST …/providers/changes</c>, con el mismo molde que los módulos.</summary>
public sealed class ChangeTenantProviderHandlerTests
{
    private static readonly TenantId Operator = TenantId.New();
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 15, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public List<string> Steps { get; } = [];
        public Tenant Target { get; } = Tenant.Create(TenantId.New(), "origen-botanico", "Origen Botánico", "es-CO",
            "America/Bogota", "yyyy-MM-dd", MembershipId.New(), Now.AddDays(-30));
        public StepsTenantProviderRepository Providers { get; }
        public RecordingTenantChangeRepository Changes { get; } = new();
        public RecordingAuditRecorder Audit { get; } = new();
        public FixedOperatorTenantReader Reader { get; } = new();
        public OperatorContext Context { get; } = new(Operator, OperatorPermissions.ModulesManage);

        public Fixture(params TenantProvider[] rows)
        {
            Providers = new StepsTenantProviderRepository(Steps, rows);
            Reader.Snapshots[Target.Id] = OperatorSnapshots.Of(Target.Id, OperatorSnapshots.Signup());
        }

        public ChangeTenantProviderHandler Handler(IExecutionContext? context = null) =>
            new(new StepsTenantRepository(Steps, Target), Providers, Changes, new StepsUnitOfWork(Steps), Audit,
                context ?? Context, new FixedOperatorTenant(Operator.Value), Reader, new FixedClock(Now),
                new ChangeTenantProviderValidator());

        public Task<OperatorTenantDetailDto> SendAsync(string? provider, string? status, string? reason, string? note = null) =>
            Handler().HandleAsync(new ChangeTenantProviderCommand(Operator, Target.Id, provider, status, reason, note),
                TestContext.Current.CancellationToken);
    }

    private static TenantProvider ActiveZenvia(Fixture fixture) =>
        TenantProvider.Create(fixture.Target.Id, TenantProviderKeys.Zenvia, Now.AddDays(-10));

    [Fact]
    public async Task EnablingWithoutARowCreatesItAndLogsTheChange()
    {
        var fixture = new Fixture();

        await fixture.SendAsync("zenvia", "active", "contract", "Contrato firmado");

        var created = Assert.Single(fixture.Providers.Added);
        Assert.Equal((fixture.Target.Id, TenantProviderKeys.Zenvia, TenantModuleStatus.Active, Now),
            (created.TenantId, created.ProviderKey, created.Status, created.StatusChangedAt));
        var change = Assert.Single(fixture.Changes.Added);
        Assert.Equal(
            (TenantChangeKind.Provider, (string?)null, "active", "zenvia", (TenantModuleKey?)null, ChangeReason.Contract,
                "Contrato firmado", fixture.Context.SubjectId, Now),
            (change.Kind, change.FromStatus, change.ToStatus, change.ProviderKey, change.ModuleKey, change.Reason,
                change.Note, change.ActorUserId, change.OccurredAt));
        Assert.NotEqual(Guid.Empty, change.BatchId);
        // §3: el candado antes de leer nada, el tenant incluido; guardar y commitear al final.
        Assert.Equal($"lock:{fixture.Target.Id}", fixture.Steps[0]);
        Assert.Equal(["save", "commit"], fixture.Steps.TakeLast(2));
        Assert.Contains($"find:{fixture.Target.Id}", fixture.Reader.Asked);
    }

    [Fact]
    public async Task DisablingAnActiveProvider()
    {
        var fixture = new Fixture();
        var row = ActiveZenvia(fixture);
        fixture.Providers.Rows.Add(row);

        await fixture.SendAsync("zenvia", "inactive", "nonpayment");

        Assert.Empty(fixture.Providers.Added);
        Assert.Equal((TenantModuleStatus.Inactive, Now), (row.Status, row.StatusChangedAt));
        var change = Assert.Single(fixture.Changes.Added);
        Assert.Equal(("active", "inactive", ChangeReason.Nonpayment), (change.FromStatus, change.ToStatus, change.Reason));
    }

    [Fact]
    public async Task ReEnablingAnInactiveProviderReactivatesTheRow()
    {
        var fixture = new Fixture();
        var row = ActiveZenvia(fixture);
        row.Deactivate(Now.AddDays(-1));
        fixture.Providers.Rows.Add(row);

        await fixture.SendAsync("zenvia", "active", "correction");

        Assert.Empty(fixture.Providers.Added);
        Assert.Equal((TenantModuleStatus.Active, Now), (row.Status, row.StatusChangedAt));
        Assert.Equal("inactive", Assert.Single(fixture.Changes.Added).FromStatus);
    }

    [Fact]
    public async Task DisablingWithoutARowIsNoChange()
    {
        var fixture = new Fixture();

        var error = await Assert.ThrowsAsync<TenantDomainException>(() => fixture.SendAsync("zenvia", "inactive", "cancellation"));

        Assert.Equal("tenancy.providers.no_changes", error.Code);
        AssertNothingWritten(fixture);
    }

    [Fact]
    public async Task EnablingAnActiveProviderIsNoChange()
    {
        var fixture = new Fixture();
        fixture.Providers.Rows.Add(ActiveZenvia(fixture));

        var error = await Assert.ThrowsAsync<TenantDomainException>(() => fixture.SendAsync("zenvia", "active", "contract"));

        Assert.Equal("tenancy.providers.no_changes", error.Code);
        AssertNothingWritten(fixture);
    }

    [Fact]
    public async Task EnablingWithANonpaymentReasonIsRefused()
    {
        var fixture = new Fixture();

        var error = await Assert.ThrowsAsync<TenantDomainException>(() => fixture.SendAsync("zenvia", "active", "nonpayment"));

        Assert.Equal("tenancy.providers.reason_not_allowed", error.Code);
        AssertNothingWritten(fixture);
    }

    [Fact]
    public async Task DisablingWithAContractReasonIsRefused()
    {
        var fixture = new Fixture();
        fixture.Providers.Rows.Add(ActiveZenvia(fixture));

        var error = await Assert.ThrowsAsync<TenantDomainException>(() => fixture.SendAsync("zenvia", "inactive", "contract"));

        Assert.Equal("tenancy.providers.reason_not_allowed", error.Code);
        AssertNothingWritten(fixture);
    }

    [Fact]
    public async Task UnknownProviderFailsValidation()
    {
        var fixture = new Fixture();

        var error = await Assert.ThrowsAsync<ValidationException>(() => fixture.SendAsync("whatsapp-cloud", "active", "contract"));

        var failure = Assert.Single(error.Errors);
        Assert.Equal((nameof(ChangeTenantProviderCommand.Provider), "Unknown provider key."), (failure.PropertyName, failure.ErrorMessage));
        Assert.DoesNotContain("save", fixture.Steps);
    }

    // Texto desconocido es 422 validation.failed, nunca el ArgumentException de Parse (500).
    public static TheoryData<string?, string?, string?, string?> InvalidShapes() => new()
    {
        { null, "active", "contract", null },
        { "Zenvia", "active", "contract", null },
        { "zenvia", null, "contract", null },
        { "zenvia", "on", "contract", null },
        { "zenvia", "active", null, null },
        { "zenvia", "active", "refund", null },
        { "zenvia", "active", "contract", new string('x', 301) },
    };

    [Theory]
    [MemberData(nameof(InvalidShapes))]
    public async Task AnInvalidShapeIsAValidationError(string? provider, string? status, string? reason, string? note)
    {
        var fixture = new Fixture();

        await Assert.ThrowsAsync<ValidationException>(() => fixture.SendAsync(provider, status, reason, note));
        Assert.DoesNotContain("save", fixture.Steps);
    }

    [Fact]
    public async Task RequiresModulesManage()
    {
        var fixture = new Fixture();

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            fixture.Handler(new OperatorContext(Operator, OperatorPermissions.TenantsRead)).HandleAsync(
                new ChangeTenantProviderCommand(Operator, TenantId.New(), "zenvia", "active", "contract", null),
                TestContext.Current.CancellationToken));
        // Doble capa: un no autorizado nunca toma el candado ni busca el destino.
        Assert.Empty(fixture.Steps);
        Assert.Empty(fixture.Reader.Asked);
    }

    [Fact]
    public async Task AMissingTargetIsNotFoundAfterAuthorization()
    {
        var fixture = new Fixture();

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            fixture.Handler().HandleAsync(
                new ChangeTenantProviderCommand(Operator, TenantId.New(), "zenvia", "active", "contract", null),
                TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.tenant.not_found", error.Code);
        Assert.DoesNotContain("save", fixture.Steps);
    }

    [Fact]
    public async Task AuditsTheChange()
    {
        var fixture = new Fixture();

        await fixture.SendAsync("zenvia", "active", "contract");

        var audit = Assert.Single(fixture.Audit.Entries);
        Assert.Equal(
            (fixture.Target.Id.Value, fixture.Context.SubjectId, "tenancy.tenant_providers.changed", "tenant", fixture.Target.Id.Value.ToString()),
            (audit.TenantId, audit.ActorId, audit.Action, audit.ResourceType, audit.ResourceId));
        Assert.Equal(["zenvia:none->active", "reason:contract"], audit.ChangedFields);
    }

    [Fact]
    public async Task TheResponseIsTheDetailReadAfterTheCommit()
    {
        var fixture = new Fixture();
        fixture.Reader.Snapshots[fixture.Target.Id] = OperatorSnapshots.Of(
            fixture.Target.Id, OperatorSnapshots.Signup(),
            providers: [new TenantProviderState(TenantProviderKeys.Zenvia, TenantModuleStatus.Active, Now)],
            lastProviderReasons: new Dictionary<string, ChangeReason> { [TenantProviderKeys.Zenvia] = ChangeReason.Contract });

        var detail = await fixture.SendAsync("zenvia", "active", "contract");

        Assert.Equal([new OperatorTenantProviderDto("zenvia", "active", Now, "contract")], detail.Providers);
    }

    private static void AssertNothingWritten(Fixture fixture)
    {
        Assert.Empty(fixture.Providers.Added);
        Assert.Empty(fixture.Changes.Added);
        Assert.Empty(fixture.Audit.Entries);
        Assert.DoesNotContain("save", fixture.Steps);
    }
}
