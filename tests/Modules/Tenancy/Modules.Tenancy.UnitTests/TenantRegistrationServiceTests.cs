using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.UnitTests;

/// <summary>Spec 2026-10-07, «Alta por signup»: una fila signup por clave de los defaults, en el mismo
/// commit que el tenant y la membresía. Un tenant nunca existe sin las filas que le tocan.</summary>
public sealed class TenantRegistrationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly TenantRegistrationData Data =
        new("Org", "org-test", "es-CO", "America/Bogota", "yyyy-MM-dd");

    [Fact]
    public async Task SignupAddsOneSignupRowPerDefaultKeyAndCommitsOnce()
    {
        var steps = new List<string>();
        var modules = new RecordingTenantModuleRepository(steps);
        var service = NewService(steps, modules, new FixedDefaults(TenantModuleKeys.DefaultForNewTenants));

        var tenantId = await service.RegisterOwnerTenantAsync(
            Guid.CreateVersion7(), Data, "trace", TestContext.Current.CancellationToken);

        Assert.Equal(
            ["catalog", "customers", "companies", "quotations", "orders", "reporting"],
            modules.Added.Select(module => module.ModuleKey.Value));
        Assert.All(modules.Added, module =>
        {
            Assert.Equal(new TenantId(tenantId), module.TenantId);
            Assert.Equal(TenantModuleSources.Signup, module.Source);
            Assert.Equal(Now, module.EnabledAt);
            Assert.Null(module.Note);
        });
        Assert.Equal("commit", steps[^1]);
        Assert.Single(steps, step => step == "commit");
        Assert.True(steps.IndexOf("tenant") < steps.IndexOf("module:catalog"));
    }

    [Fact]
    public async Task WithEmptyDefaultsNoRowIsAdded()
    {
        var steps = new List<string>();
        var modules = new RecordingTenantModuleRepository(steps);
        var service = NewService(steps, modules, new FixedDefaults([]));

        await service.RegisterOwnerTenantAsync(
            Guid.CreateVersion7(), Data, "trace", TestContext.Current.CancellationToken);

        Assert.Empty(modules.Added);
        Assert.Single(steps, step => step == "commit");
    }

    private static TenantRegistrationService NewService(
        List<string> steps, RecordingTenantModuleRepository modules, ITenantModuleDefaults defaults) =>
        new(
            new RecordingTenantRepository(steps),
            new InMemoryMembershipRepository(),
            modules,
            defaults,
            new RecordingTenancyUnitOfWork(steps),
            new RecordingAuditRecorder(),
            new RecordingOutboxWriter(),
            new FixedClock(Now));

    private sealed class RecordingTenantRepository(List<string> steps) : ITenantRepository
    {
        public Task<Tenant?> GetAsync(TenantId id, CancellationToken cancellationToken) =>
            Task.FromResult<Tenant?>(null);

        public void Add(Tenant tenant) => steps.Add("tenant");
    }

    private sealed class RecordingTenantModuleRepository(List<string> steps) : ITenantModuleRepository
    {
        public List<TenantModule> Added { get; } = [];

        public void Add(TenantModule tenantModule)
        {
            steps.Add($"module:{tenantModule.ModuleKey.Value}");
            Added.Add(tenantModule);
        }

        public Task<IReadOnlyList<TenantModule>> ListByTenantAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedDefaults(IReadOnlyCollection<TenantModuleKey> keys) : ITenantModuleDefaults
    {
        public IReadOnlyCollection<TenantModuleKey> ForNewTenants { get; } = keys;
    }
}
