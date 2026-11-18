using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>Spec 2026-11-17 (mayorista), D-5: vincular un tenant existente prende los módulos que le falten, con el mismo
/// cuerpo que la consola (candado, plan, historial, auditoría y versión de acceso) pero sin el permiso de operador.</summary>
public sealed class TenantModuleGrantServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 15, 0, 0, TimeSpan.Zero);
    private const string Note = "Invitación de mayorista: Origen Botánico";

    private sealed class Fixture
    {
        public Fixture(params (TenantModuleKey Key, TenantModuleStatus Status)[] rows)
        {
            Modules = new StepsTenantModuleRepository(Steps, rows
                .Select(row =>
                {
                    var module = TenantModule.Create(Target.Id, row.Key, TenantModuleSources.Signup, Now.AddDays(-30), null);
                    if (row.Status == TenantModuleStatus.Inactive)
                    {
                        module.Deactivate(Now.AddDays(-10));
                    }

                    return module;
                })
                .ToArray());
        }

        public List<string> Steps { get; } = [];
        public Guid Actor { get; } = Guid.CreateVersion7();
        public Tenant Target { get; } = Tenant.Create(TenantId.New(), "distri-norte", "Distri Norte", "es-CO",
            "America/Bogota", "yyyy-MM-dd", MembershipId.New(), Now.AddDays(-30));
        public StepsTenantModuleRepository Modules { get; }
        public RecordingTenantChangeRepository Changes { get; } = new();
        public RecordingAuditRecorder Audit { get; } = new();

        public Task<IReadOnlyList<TenantModuleKey>> EnsureAsync(params TenantModuleKey[] keys) =>
            new TenantModuleGrantService(
                    new StepsTenantRepository(Steps, Target), Modules, Changes, new StepsUnitOfWork(Steps), Audit,
                    new FixedClock(Now), new RecordingTenantAccessVersion(Steps))
                .EnsureActiveAsync(Target.Id, keys, Actor, Note, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EnsureActiveCreatesMissingRowsWithInvitationSourceAndContractReason()
    {
        var fixture = new Fixture((TenantModuleKeys.Catalog, TenantModuleStatus.Active));

        // Pedidas fuera de orden: la respuesta sale en el orden de All.
        var enabled = await fixture.EnsureAsync(TenantModuleKeys.Purchases, TenantModuleKeys.Suppliers, TenantModuleKeys.Catalog);

        Assert.Equal([TenantModuleKeys.Suppliers, TenantModuleKeys.Purchases], enabled);
        Assert.Equal(["suppliers", "purchases"], fixture.Modules.Added.Select(module => module.ModuleKey.Value));
        Assert.All(fixture.Modules.Added, module =>
            Assert.Equal((fixture.Target.Id, TenantModuleSources.Invitation, Now, TenantModuleStatus.Active),
                (module.TenantId, module.Source, module.EnabledAt, module.Status)));
        Assert.Equal(2, fixture.Changes.Added.Count);
        Assert.Single(fixture.Changes.Added.Select(change => change.BatchId).Distinct());
        Assert.All(fixture.Changes.Added, change =>
            Assert.Equal(((string?)null, "active", ChangeReason.Contract, Note, fixture.Actor, Now),
                (change.FromStatus, change.ToStatus, change.Reason, change.Note, change.ActorUserId, change.OccurredAt)));
        var audit = Assert.Single(fixture.Audit.Entries);
        Assert.Equal(
            (fixture.Target.Id.Value, fixture.Actor, "tenancy.tenant_modules.changed", "tenant", fixture.Target.Id.Value.ToString()),
            (audit.TenantId, audit.ActorId, audit.Action, audit.ResourceType, audit.ResourceId));
        Assert.Equal(["suppliers:none->active", "purchases:none->active", "reason:contract"], audit.ChangedFields);
        Assert.Equal($"lock:{fixture.Target.Id}", fixture.Steps[0]);
    }

    [Fact]
    public async Task EnsureActiveReactivatesAnInactiveRow()
    {
        var fixture = new Fixture(
            (TenantModuleKeys.Catalog, TenantModuleStatus.Active), (TenantModuleKeys.Suppliers, TenantModuleStatus.Inactive));

        var enabled = await fixture.EnsureAsync(TenantModuleKeys.Catalog, TenantModuleKeys.Suppliers);

        Assert.Equal([TenantModuleKeys.Suppliers], enabled);
        Assert.Empty(fixture.Modules.Added);
        var suppliers = fixture.Modules.Rows.Single(row => row.ModuleKey == TenantModuleKeys.Suppliers);
        Assert.Equal((TenantModuleStatus.Active, Now), (suppliers.Status, suppliers.StatusChangedAt));
        var change = Assert.Single(fixture.Changes.Added);
        Assert.Equal(("inactive", "active"), (change.FromStatus, change.ToStatus));
    }

    [Fact]
    public async Task EnsureActiveWithEverythingActiveWritesNothing()
    {
        var fixture = new Fixture(
            (TenantModuleKeys.Catalog, TenantModuleStatus.Active), (TenantModuleKeys.Suppliers, TenantModuleStatus.Active));

        var enabled = await fixture.EnsureAsync(TenantModuleKeys.Suppliers, TenantModuleKeys.Catalog);

        Assert.Empty(enabled);
        Assert.Empty(fixture.Modules.Added);
        Assert.Empty(fixture.Changes.Added);
        Assert.Empty(fixture.Audit.Entries);
        Assert.DoesNotContain("save", fixture.Steps);
        Assert.DoesNotContain("commit", fixture.Steps);
        Assert.DoesNotContain(fixture.Steps, step => step.StartsWith("bump:", StringComparison.Ordinal));
    }

    // Spec 2026-11-13, D-5: igual que la consola, la caché de permisos se invalida antes del commit y otra vez después.
    [Fact]
    public async Task EnsureActiveBumpsTheAccessVersionBeforeAndAfterTheCommit()
    {
        var fixture = new Fixture((TenantModuleKeys.Catalog, TenantModuleStatus.Active));

        await fixture.EnsureAsync(TenantModuleKeys.Suppliers);

        var target = fixture.Target.Id.Value;
        Assert.Equal(
            [$"bump:{target}", "commit", $"bump:{target}"],
            fixture.Steps.Where(step => step == "commit" || step.StartsWith("bump:", StringComparison.Ordinal)));
    }
}
