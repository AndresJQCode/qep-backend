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
            ["catalog", "customers", "companies", "quotations", "orders", "reporting", "shopify"],
            modules.Added.Select(module => module.ModuleKey.Value));
        Assert.All(modules.Added, module =>
        {
            Assert.Equal(new TenantId(tenantId), module.TenantId);
            Assert.Equal(TenantModuleSources.Signup, module.Source);
            Assert.Equal(Now, module.EnabledAt);
            Assert.Null(module.Note);
        });
        // El aprovisionamiento de otros módulos va después del commit, nunca antes.
        Assert.Equal(["commit", "provision"], steps.TakeLast(2));
        Assert.Single(steps, step => step == "commit");
        Assert.True(steps.IndexOf("tenant") < steps.IndexOf("module:catalog"));
    }

    [Fact]
    public async Task RegisterOwnerTenantGrantsShopify()
    {
        var steps = new List<string>();
        var modules = new RecordingTenantModuleRepository(steps);
        var service = NewService(steps, modules, new FixedDefaults(TenantModuleKeys.DefaultForNewTenants));

        await service.RegisterOwnerTenantAsync(
            Guid.CreateVersion7(), Data, "trace", TestContext.Current.CancellationToken);

        Assert.Contains(modules.Added, module => module.ModuleKey.Value == "shopify");
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

    [Fact]
    public async Task EnsureRegistrableThrowsSlugTakenWhenTheSlugExists()
    {
        var (service, availability) = NewSlugService("acme");
        var error = await Assert.ThrowsAsync<TenantDomainException>(
            () => service.EnsureRegistrableAsync(DataWithSlug("  ACME "), TestContext.Current.CancellationToken));
        Assert.Equal("tenancy.slug.taken", error.Code);
        Assert.Equal(["acme"], availability.Asked);
    }

    [Fact]
    public async Task EnsureRegistrableValidatesBeforeAskingForTheSlug()
    {
        var (service, availability) = NewSlugService();
        var error = await Assert.ThrowsAsync<TenantDomainException>(
            () => service.EnsureRegistrableAsync(DataWithSlug("a"), TestContext.Current.CancellationToken));
        Assert.Equal("tenancy.slug.invalid", error.Code);
        Assert.Empty(availability.Asked);
    }

    // Spec 2026-11-17 (mayorista), D-3 y D-17: la aceptación de la invitación crea el tenant con el id reservado al invitar,
    // bajo el candado de ese tenant, con los módulos que pide Mayorista y no con los del signup.
    private static readonly IReadOnlyList<TenantModuleKey> InvitedModules =
    [
        TenantModuleKeys.Catalog, TenantModuleKeys.Customers, TenantModuleKeys.Companies, TenantModuleKeys.Quotations,
        TenantModuleKeys.Orders, TenantModuleKeys.Reporting, TenantModuleKeys.Suppliers, TenantModuleKeys.Purchases,
    ];

    [Fact]
    public async Task RegisterInvitedTenantCreatesTheTenantWithTheReservedIdAndTheGivenModules()
    {
        var invited = new InvitedFixture();
        var reserved = TenantId.New();
        var owner = Guid.CreateVersion7();

        var created = await invited.Service.RegisterInvitedTenantAsync(
            reserved, owner, Data, InvitedModules, "trace", TestContext.Current.CancellationToken);

        Assert.True(created);
        var tenant = Assert.Single(invited.Tenants.Added);
        Assert.Equal(reserved, tenant.Id);
        Assert.Equal("Org", tenant.DisplayName);
        Assert.Equal(
            ["catalog", "customers", "companies", "quotations", "orders", "reporting", "suppliers", "purchases"],
            invited.Modules.Added.Select(module => module.ModuleKey.Value));
        Assert.All(invited.Modules.Added, module =>
            Assert.Equal((reserved, TenantModuleSources.Invitation, Now), (module.TenantId, module.Source, module.EnabledAt)));
        Assert.Equal("invitation", TenantModuleSources.Invitation);
        var membership = Assert.Single(invited.Memberships.All);
        Assert.Equal((owner, reserved, Membership.RegistrationOrigin), (membership.UserId, membership.TenantId, membership.Origin));
        Assert.Equal([Membership.AdminRole], membership.Roles);
        Assert.True(tenant.IsOwner(membership.Id));
        var audit = Assert.Single(invited.Audit.Entries);
        Assert.Equal((reserved.Value, owner, "tenancy.tenant.registered"), (audit.TenantId, audit.ActorId, audit.Action));
        // El candado del tenant reservado va antes de leer, y el commit es el último paso.
        Assert.Equal($"lock:{reserved}", invited.Steps[0]);
        Assert.Equal(["save", "commit", "provision"], invited.Steps.TakeLast(3));
    }

    [Fact]
    public async Task RegisterInvitedTenantTwiceForTheSameOwnerReturnsFalseAndWritesNothing()
    {
        var invited = new InvitedFixture();
        var reserved = TenantId.New();
        var owner = Guid.CreateVersion7();
        await invited.Service.RegisterInvitedTenantAsync(
            reserved, owner, Data, InvitedModules, "trace", TestContext.Current.CancellationToken);
        invited.Steps.Clear();

        var created = await invited.Service.RegisterInvitedTenantAsync(
            reserved, owner, Data, InvitedModules, "trace", TestContext.Current.CancellationToken);

        Assert.False(created);
        Assert.Single(invited.Tenants.Added);
        Assert.Equal(InvitedModules.Count, invited.Modules.Added.Count);
        Assert.Single(invited.Memberships.All);
        Assert.Single(invited.Audit.Entries);
        Assert.DoesNotContain("save", invited.Steps);
        Assert.DoesNotContain("commit", invited.Steps);
    }

    // Spec 2026-11-22: el país viaja en el Data y el tenant invitado nace con él; el signup, sin país, sigue en CO.
    [Fact]
    public async Task RegisterInvitedTenantBornWithTheCountryOfTheData()
    {
        var invited = new InvitedFixture();

        await invited.Service.RegisterInvitedTenantAsync(
            TenantId.New(), Guid.CreateVersion7(), Data with { Country = "mx" }, InvitedModules, "trace",
            TestContext.Current.CancellationToken);

        Assert.Equal("MX", Assert.Single(invited.Tenants.Added).Country);
    }

    [Fact]
    public async Task RegisterInvitedTenantWithoutCountryStaysInColombia()
    {
        var invited = new InvitedFixture();

        await invited.Service.RegisterInvitedTenantAsync(
            TenantId.New(), Guid.CreateVersion7(), Data, InvitedModules, "trace", TestContext.Current.CancellationToken);

        Assert.Equal("CO", Assert.Single(invited.Tenants.Added).Country);
    }

    [Fact]
    public async Task RegisterInvitedTenantWithAnInvalidCountryWritesNothing()
    {
        var invited = new InvitedFixture();

        var error = await Assert.ThrowsAsync<TenantDomainException>(() => invited.Service.RegisterInvitedTenantAsync(
            TenantId.New(), Guid.CreateVersion7(), Data with { Country = "MEX" }, InvitedModules, "trace",
            TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.settings.country.invalid", error.Code);
        Assert.Empty(invited.Tenants.Added);
        Assert.Empty(invited.Modules.Added);
        Assert.Empty(invited.Memberships.All);
        Assert.DoesNotContain("commit", invited.Steps);
    }

    [Fact]
    public async Task RegisterInvitedTenantRetryWithAnotherCountryReturnsFalseAndKeepsTheCountry()
    {
        var invited = new InvitedFixture();
        var reserved = TenantId.New();
        var owner = Guid.CreateVersion7();
        await invited.Service.RegisterInvitedTenantAsync(
            reserved, owner, Data with { Country = "MX" }, InvitedModules, "trace", TestContext.Current.CancellationToken);
        invited.Steps.Clear();

        var created = await invited.Service.RegisterInvitedTenantAsync(
            reserved, owner, Data with { Country = "ES" }, InvitedModules, "trace", TestContext.Current.CancellationToken);

        Assert.False(created);
        Assert.Equal("MX", Assert.Single(invited.Tenants.Added).Country);
        Assert.DoesNotContain("commit", invited.Steps);
    }

    [Fact]
    public async Task EnsureRegistrableWithAnInvalidCountryFailsBeforeAskingForTheSlug()
    {
        var (service, availability) = NewSlugService();

        var error = await Assert.ThrowsAsync<TenantDomainException>(
            () => service.EnsureRegistrableAsync(Data with { Country = "MEX" }, TestContext.Current.CancellationToken));

        Assert.Equal("tenancy.settings.country.invalid", error.Code);
        Assert.Empty(availability.Asked);
    }

    [Fact]
    public async Task SignupBornInColombia()
    {
        var steps = new List<string>();
        var tenants = new StoringTenantRepository(steps);
        var service = new TenantRegistrationService(
            tenants,
            new InMemoryMembershipRepository(),
            new RecordingTenantModuleRepository(steps),
            new FixedDefaults([]),
            new StepsUnitOfWork(steps),
            new RecordingAuditRecorder(),
            new RecordingOutboxWriter(),
            new FixedClock(Now),
            new RecordingSlugAvailability(),
            [new RecordingProvisioning(steps)]);

        await service.RegisterOwnerTenantAsync(Guid.CreateVersion7(), Data, "trace", TestContext.Current.CancellationToken);

        Assert.Equal("CO", Assert.Single(tenants.Added).Country);
    }

    // Spec 2026-11-23: el signup no trae país, así que no se deriva moneda: el tenant queda con la de la columna.
    [Fact]
    public async Task SignupWithoutCountryKeepsTheColumnCurrency()
    {
        var steps = new List<string>();
        var tenants = new StoringTenantRepository(steps);
        var service = new TenantRegistrationService(
            tenants,
            new InMemoryMembershipRepository(),
            new RecordingTenantModuleRepository(steps),
            new FixedDefaults([]),
            new StepsUnitOfWork(steps),
            new RecordingAuditRecorder(),
            new RecordingOutboxWriter(),
            new FixedClock(Now),
            new RecordingSlugAvailability(),
            [new RecordingProvisioning(steps)]);

        await service.RegisterOwnerTenantAsync(Guid.CreateVersion7(), Data, "trace", TestContext.Current.CancellationToken);

        Assert.Equal("COP", Assert.Single(tenants.Added).DefaultCurrency);
    }

    // Spec 2026-11-23: el tenant que nace en el signup público recibe sus ajustes de cotizaciones después del commit.
    [Fact]
    public async Task SignupProvisionsTheNewTenantAfterTheCommit()
    {
        var steps = new List<string>();
        var provisioning = new RecordingProvisioning(steps);
        var service = NewService(
            steps, new RecordingTenantModuleRepository(steps), new FixedDefaults([]), provisioning: [provisioning]);

        var tenantId = await service.RegisterOwnerTenantAsync(
            Guid.CreateVersion7(), Data, "trace", TestContext.Current.CancellationToken);

        Assert.Equal([new TenantId(tenantId)], provisioning.Provisioned);
        Assert.Equal(["commit", "provision"], steps.TakeLast(2));
    }

    // Spec 2026-11-25 (roles por tenant): hay más de un aprovisionamiento y corren en el orden en que se registraron,
    // porque un fallo de uno que sube no puede saltarse el que va antes.
    [Fact]
    public async Task SignupRunsEveryProvisioningInRegistrationOrder()
    {
        var steps = new List<string>();
        var service = NewService(
            steps,
            new RecordingTenantModuleRepository(steps),
            new FixedDefaults([]),
            provisioning: [new RecordingProvisioning(steps, "provision:a"), new RecordingProvisioning(steps, "provision:b")]);

        await service.RegisterOwnerTenantAsync(Guid.CreateVersion7(), Data, "trace", TestContext.Current.CancellationToken);

        Assert.Equal(["commit", "provision:a", "provision:b"], steps.TakeLast(3));
    }

    [Fact]
    public async Task RegisterInvitedTenantRunsEveryProvisioningInRegistrationOrder()
    {
        var invited = new InvitedFixture(provisioningSteps: ["provision:a", "provision:b"]);

        await invited.Service.RegisterInvitedTenantAsync(
            TenantId.New(), Guid.CreateVersion7(), Data, InvitedModules, "trace", TestContext.Current.CancellationToken);

        Assert.Equal(["commit", "provision:a", "provision:b"], invited.Steps.TakeLast(3));
    }

    [Fact]
    public async Task RegisterInvitedTenantProvisionsTheNewTenantAfterTheCommit()
    {
        var invited = new InvitedFixture();
        var reserved = TenantId.New();

        await invited.Service.RegisterInvitedTenantAsync(
            reserved, Guid.CreateVersion7(), Data, InvitedModules, "trace", TestContext.Current.CancellationToken);

        Assert.Equal([reserved], invited.Provisioning.Provisioned);
        Assert.Equal("provision", invited.Steps[^1]);
    }

    [Fact]
    public async Task RegisterInvitedTenantRetryDoesNotProvisionAgain()
    {
        var invited = new InvitedFixture();
        var reserved = TenantId.New();
        var owner = Guid.CreateVersion7();
        await invited.Service.RegisterInvitedTenantAsync(
            reserved, owner, Data, InvitedModules, "trace", TestContext.Current.CancellationToken);

        var again = await invited.Service.RegisterInvitedTenantAsync(
            reserved, owner, Data, InvitedModules, "trace", TestContext.Current.CancellationToken);

        Assert.False(again);
        Assert.Single(invited.Provisioning.Provisioned);
    }

    [Fact]
    public async Task RegisterInvitedTenantOwnedByAnotherUserThrows()
    {
        var invited = new InvitedFixture();
        var reserved = TenantId.New();
        await invited.Service.RegisterInvitedTenantAsync(
            reserved, Guid.CreateVersion7(), Data, InvitedModules, "trace", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => invited.Service.RegisterInvitedTenantAsync(
            reserved, Guid.CreateVersion7(), Data, InvitedModules, "trace", TestContext.Current.CancellationToken));

        Assert.Single(invited.Tenants.Added);
        Assert.Single(invited.Memberships.All);
    }

    // Ruling W7: el alta invitada no depende de Registration:PublicTenantSignupEnabled. El servicio no recibe
    // IConfiguration (el interruptor lo lee RegistrationEndpoints), y los defaults del signup —lo único que sale de
    // configuración aquí— lanzan si se leen: con el signup público apagado la invitación igual crea el tenant.
    [Fact]
    public async Task RegisterInvitedTenantDoesNotReadTheSignupFlag()
    {
        var invited = new InvitedFixture(new ThrowingDefaults());

        var created = await invited.Service.RegisterInvitedTenantAsync(
            TenantId.New(), Guid.CreateVersion7(), Data, InvitedModules, "trace", TestContext.Current.CancellationToken);

        Assert.True(created);
        Assert.Single(invited.Tenants.Added);
        Assert.Equal(InvitedModules.Count, invited.Modules.Added.Count);
    }

    // Ruling W11 (a): la lista de módulos la arma otro módulo, así que el alta no confía en ella. Una clave repetida sería
    // una violación de PK (500) y un conjunto sin sus dependencias dejaría un tenant inconsistente: las dos fallan antes del
    // candado, sin escribir nada.
    [Fact]
    public async Task RegisterInvitedTenantRejectsARepeatedModuleWithoutWriting()
    {
        var invited = new InvitedFixture();

        await Assert.ThrowsAsync<ArgumentException>(() => invited.Service.RegisterInvitedTenantAsync(
            TenantId.New(),
            Guid.CreateVersion7(),
            Data,
            [TenantModuleKeys.Catalog, TenantModuleKeys.Suppliers, TenantModuleKeys.Catalog],
            "trace",
            TestContext.Current.CancellationToken));

        Assert.Empty(invited.Steps);
        Assert.Empty(invited.Tenants.Added);
    }

    [Fact]
    public async Task RegisterInvitedTenantRejectsAModuleWithoutItsDependenciesWithoutWriting()
    {
        var invited = new InvitedFixture();

        // purchases depende de suppliers y catalog; falta catalog.
        await Assert.ThrowsAsync<ArgumentException>(() => invited.Service.RegisterInvitedTenantAsync(
            TenantId.New(),
            Guid.CreateVersion7(),
            Data,
            [TenantModuleKeys.Suppliers, TenantModuleKeys.Purchases],
            "trace",
            TestContext.Current.CancellationToken));

        Assert.Empty(invited.Steps);
        Assert.Empty(invited.Tenants.Added);
    }

    private sealed class InvitedFixture
    {
        /// <param name="provisioningSteps">Un doble por nombre, en ese orden; sin ellos, uno solo llamado <c>provision</c>.
        /// <see cref="Provisioning"/> es el primero.</param>
        public InvitedFixture(ITenantModuleDefaults? defaults = null, string[]? provisioningSteps = null)
        {
            Tenants = new StoringTenantRepository(Steps);
            Modules = new RecordingTenantModuleRepository(Steps);
            var provisionings = (provisioningSteps ?? ["provision"])
                .Select(step => new RecordingProvisioning(Steps, step))
                .ToArray();
            Provisioning = provisionings[0];
            Service = new TenantRegistrationService(
                Tenants,
                Memberships,
                Modules,
                defaults ?? new FixedDefaults(TenantModuleKeys.DefaultForNewTenants),
                new StepsUnitOfWork(Steps),
                Audit,
                new RecordingOutboxWriter(),
                new FixedClock(Now),
                new RecordingSlugAvailability(),
                provisionings);
        }

        public RecordingProvisioning Provisioning { get; }

        public List<string> Steps { get; } = [];
        public StoringTenantRepository Tenants { get; }
        public InMemoryMembershipRepository Memberships { get; } = new();
        public RecordingTenantModuleRepository Modules { get; }
        public RecordingAuditRecorder Audit { get; } = new();
        public TenantRegistrationService Service { get; }
    }

    /// <summary>Guarda lo agregado y lo devuelve al leer: el segundo intento ve el tenant del primero.</summary>
    private sealed class StoringTenantRepository(List<string> steps) : ITenantRepository
    {
        public List<Tenant> Added { get; } = [];

        public Task<Tenant?> GetAsync(TenantId id, CancellationToken cancellationToken)
        {
            steps.Add("read-tenant");
            return Task.FromResult(Added.SingleOrDefault(tenant => tenant.Id == id));
        }

        public void Add(Tenant tenant)
        {
            steps.Add("tenant");
            Added.Add(tenant);
        }
    }

    private sealed class ThrowingDefaults : ITenantModuleDefaults
    {
        public IReadOnlyCollection<TenantModuleKey> ForNewTenants =>
            throw new InvalidOperationException("The invited registration must not read the signup defaults.");
    }

    private static TenantRegistrationData DataWithSlug(string slug) => Data with { Slug = slug };

    private static (TenantRegistrationService Service, RecordingSlugAvailability Availability) NewSlugService(
        params string[] takenSlugs)
    {
        var availability = new RecordingSlugAvailability(takenSlugs);
        return (NewService([], new RecordingTenantModuleRepository([]), new FixedDefaults([]), availability),
            availability);
    }

    private static TenantRegistrationService NewService(
        List<string> steps,
        RecordingTenantModuleRepository modules,
        ITenantModuleDefaults defaults,
        ITenantSlugAvailability? slugAvailability = null,
        INewTenantProvisioning[]? provisioning = null) =>
        new(
            new RecordingTenantRepository(steps),
            new InMemoryMembershipRepository(),
            modules,
            defaults,
            new RecordingTenancyUnitOfWork(steps),
            new RecordingAuditRecorder(),
            new RecordingOutboxWriter(),
            new FixedClock(Now),
            slugAvailability ?? new RecordingSlugAvailability(),
            provisioning ?? [new RecordingProvisioning(steps)]);

    private sealed class RecordingProvisioning(List<string> steps, string step = "provision") : INewTenantProvisioning
    {
        public List<TenantId> Provisioned { get; } = [];

        public Task ProvisionAsync(TenantId tenantId, CancellationToken cancellationToken)
        {
            steps.Add(step);
            Provisioned.Add(tenantId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSlugAvailability(params string[] takenSlugs) : ITenantSlugAvailability
    {
        public List<string> Asked { get; } = [];

        public Task<bool> IsTakenAsync(string normalizedSlug, CancellationToken cancellationToken)
        {
            Asked.Add(normalizedSlug);
            return Task.FromResult(takenSlugs.Contains(normalizedSlug));
        }
    }

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
