using BuildingBlocks.Application;
using Modules.Authorization.Application;
using Modules.Authorization.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Authorization.UnitTests;

public sealed class RoleCommandsTests
{
    private static readonly TenantId Tenant = new(Guid.CreateVersion7());

    private static RoleCatalog SystemCatalog() =>
        new(
            [
                new RoleDefinition(
                    SystemRoleKeys.Admin, "Administrador", "", "Tenancy", "high",
                    ["advisorship.manage", "advisorship.roles.manage", "catalog.product.read", "operator.tenants.read"]),
                new RoleDefinition(
                    "advisor", "Asesor", "", "Tenancy", "medium",
                    ["catalog.product.read"]),
            ],
            [
                new PermissionDefinition(
                    "advisorship.manage", "Gestionar miembros", "", "Tenancy", "high", RequiredModules: []),
                new PermissionDefinition(
                    "advisorship.roles.manage", "Definir roles", "", "Tenancy", "high", RequiredModules: []),
                new PermissionDefinition(
                    "catalog.product.read", "Ver productos", "", "Catalog", "low", RequiredModules: []),
                new PermissionDefinition(
                    "operator.tenants.read", "Ver tenants", "", "Operator", "medium", RequiredModules: []),
            ]);

    private sealed class Repo : IRoleRepository
    {
        private readonly List<Role> _roles;

        public Repo(params Role[] roles) => _roles = [.. roles];

        public List<Role> Added { get; } = [];

        public List<Role> Removed { get; } = [];

        public Task<Role?> FindByIdAsync(RoleId id, Guid tenantId, CancellationToken _) =>
            Task.FromResult(_roles.SingleOrDefault(
                role => role.Id == id && role.TenantId == tenantId));

        public Task<Role?> FindByKeyAsync(Guid tenantId, string key, CancellationToken _) =>
            Task.FromResult(_roles.SingleOrDefault(
                role => role.TenantId == tenantId && role.Key == key));

        public void Add(Role role) => Added.Add(role);

        public void Remove(Role role) => Removed.Add(role);
    }

    private sealed class Usage(params string[][] roleSets) : IMembershipRoleUsage
    {
        /// <summary>Las claves que lleva alguna membresía invitada, activa o suspendida.</summary>
        public string[] Held { get; init; } = [];

        public Task<bool> AnyHoldsRoleAsync(
            TenantId tenantId,
            string role,
            CancellationToken cancellationToken) =>
            Task.FromResult(Held.Contains(role, StringComparer.Ordinal));

        public Task<IReadOnlyCollection<IReadOnlyCollection<string>>> ActiveRoleSetsAsync(
            TenantId tenantId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<IReadOnlyCollection<string>>>(
                roleSets.Select(set => (IReadOnlyCollection<string>)set).ToArray());
    }

    private sealed class Uow : IAuthorizationUnitOfWork
    {
        public int Saves { get; private set; }

        /// <summary>El guardado y las subidas de versión del tenant, en el orden en que pasan.</summary>
        public List<string> Steps { get; } = [];

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            Saves++;
            Steps.Add("save");
            return Task.FromResult(1);
        }
    }

    /// <summary>Anota cada subida en la lista de la unidad de trabajo (spec 2026-11-13, D-5).</summary>
    private sealed class RecordingVersion(List<string> steps) : ITenantAccessVersion
    {
        public Task BumpAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            steps.Add($"bump:{tenantId}");
            return Task.CompletedTask;
        }
    }

    private sealed class Context(TenantId tenantId, params string[] permissions)
        : IExecutionContext
    {
        public Guid SubjectId { get; } = Guid.CreateVersion7();

        public TenantId TenantId => tenantId;

        public bool HasPermission(string permission) =>
            permissions.Contains(permission, StringComparer.Ordinal);
    }

    private sealed class StubClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }

    private static Role CustomRole(string key, params string[] permissions) =>
        Role.Create(
            RoleId.New(), Tenant.Value, key, key, "", permissions, DateTimeOffset.UnixEpoch);

    private static CreateRoleHandler CreateHandler(
        Repo repo,
        Uow uow,
        IExecutionContext? context = null) =>
        new(
            repo,
            uow,
            SystemCatalog(),
            context ?? new Context(Tenant, "advisorship.roles.manage"),
            new StubClock(),
            new RecordingVersion(uow.Steps));

    // ---- Versión de acceso del tenant (spec 2026-11-13, D-5) ----

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task CreateUpdateAndDeleteRoleBumpTheTenantVersionBeforeAndAfterSaving(string operation)
    {
        var role = CustomRole("ventas-junior", "catalog.product.read");
        var uow = new Uow();
        var ct = TestContext.Current.CancellationToken;

        switch (operation)
        {
            case "create":
                await CreateHandler(new Repo(), uow).HandleAsync(
                    new CreateRoleCommand(Tenant, "ventas-senior", "Ventas senior", "", ["catalog.product.read"]), ct);
                break;
            case "update":
                await UpdateHandler(new Repo(role), uow, new Usage([SystemRoleKeys.Admin])).HandleAsync(
                    new UpdateRoleCommand(Tenant, role.Id, "Ventas", "", ["catalog.product.read"], role.Version), ct);
                break;
            default:
                await DeleteHandler(new Repo(role), uow, new Usage([SystemRoleKeys.Admin])).HandleAsync(
                    new DeleteRoleCommand(Tenant, role.Id), ct);
                break;
        }

        Assert.Equal([$"bump:{Tenant.Value}", "save", $"bump:{Tenant.Value}"], uow.Steps);
    }

    // ---- Crear ----

    [Fact]
    public async Task CreateStoresTheRole()
    {
        var repo = new Repo();
        var uow = new Uow();

        var role = await CreateHandler(repo, uow).HandleAsync(
            new CreateRoleCommand(
                Tenant, "ventas-junior", "Ventas junior", "", ["catalog.product.read"]),
            TestContext.Current.CancellationToken);

        Assert.Equal("ventas-junior", role.Key);
        Assert.Single(repo.Added);
        Assert.Equal(1, uow.Saves);
    }

    [Fact]
    public async Task CreateRejectsAPermissionOutsideTheCatalog()
    {
        // La comprobacion no puede estar en el agregado: el catalogo se arma en composicion y
        // el dominio no lo conoce. Sin ella, un rol concede un permiso que nada respeta y su
        // gente descubre que "no funciona" sin ningun error a la vista.
        var error = await Assert.ThrowsAsync<AuthorizationDomainException>(() =>
            CreateHandler(new Repo(), new Uow()).HandleAsync(
                new CreateRoleCommand(
                    Tenant, "inventado", "Inventado", "", ["catalog.product.inventado"]),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.role.permission_unknown", error.Code);
    }

    [Fact]
    public async Task CreateRejectsAKeyAlreadyTakenInTheTenant()
    {
        var repo = new Repo(CustomRole("ventas-junior", "catalog.product.read"));

        var error = await Assert.ThrowsAsync<AuthorizationDomainException>(() =>
            CreateHandler(repo, new Uow()).HandleAsync(
                new CreateRoleCommand(
                    Tenant, "ventas-junior", "Otro", "", ["catalog.product.read"]),
                TestContext.Current.CancellationToken));

        // El indice unico lo impide igual, pero llegar hasta PostgreSQL para decirlo convierte
        // un dato que ya teniamos en un 500 traducido.
        Assert.Equal("authorization.role.key_taken", error.Code);
    }

    [Fact]
    public async Task CreateIsForbiddenWithoutTheRolesManagePermission()
    {
        // `advisorship.manage` NO alcanza: cambia quien tiene un rol, no que puede ese rol.
        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            CreateHandler(
                new Repo(),
                new Uow(),
                new Context(Tenant, "advisorship.manage")).HandleAsync(
                new CreateRoleCommand(
                    Tenant, "ventas-junior", "Ventas junior", "", ["catalog.product.read"]),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.denied", error.Code);
    }

    // ---- Borrar ----

    private static DeleteRoleHandler DeleteHandler(Repo repo, Uow uow, Usage usage) =>
        new(repo, uow, usage, new Context(Tenant, "advisorship.roles.manage"), new RecordingVersion(uow.Steps));

    [Fact]
    public async Task DeleteRemovesARoleNoLiveMemberHolds()
    {
        var role = CustomRole("ventas-junior", "catalog.product.read");
        var repo = new Repo(role);
        var uow = new Uow();

        await DeleteHandler(repo, uow, new Usage([SystemRoleKeys.Admin])).HandleAsync(
            new DeleteRoleCommand(Tenant, role.Id),
            TestContext.Current.CancellationToken);

        Assert.Single(repo.Removed);
        Assert.Equal(1, uow.Saves);
        Assert.Equal([$"bump:{Tenant.Value}", "save", $"bump:{Tenant.Value}"], uow.Steps);
    }

    // Spec 2026-11-25 (roles por tenant): una persona suspendida o invitada quedaría nombrando un rol que ya no existe, y
    // guardar sus roles respondería `tenancy.membership.role_unknown` sin salida.
    [Fact]
    public async Task DeleteRefusesARoleHeldOnlyByASuspendedOrInvitedMember()
    {
        var role = CustomRole("ventas-junior", "catalog.product.read");
        var repo = new Repo(role);

        var error = await Assert.ThrowsAsync<AuthorizationDomainException>(() =>
            DeleteHandler(repo, new Uow(), new Usage { Held = ["ventas-junior"] }).HandleAsync(
                new DeleteRoleCommand(Tenant, role.Id),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.role.in_use", error.Code);
        Assert.Empty(repo.Removed);
    }

    [Fact]
    public async Task DeleteRefusesARoleSomebodyStillHas()
    {
        // Sin esta guarda quedan membresias apuntando a un rol que ya no existe. No explota:
        // `PermissionsForAsync` ignora lo desconocido, asi que esa gente simplemente pierde
        // permisos en silencio y nadie relaciona una cosa con la otra.
        var role = CustomRole("ventas-junior", "catalog.product.read");
        var repo = new Repo(role);

        var error = await Assert.ThrowsAsync<AuthorizationDomainException>(() =>
            DeleteHandler(repo, new Uow(), new Usage(["ventas-junior"]) { Held = ["ventas-junior"] }).HandleAsync(
                new DeleteRoleCommand(Tenant, role.Id),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.role.in_use", error.Code);
        Assert.Empty(repo.Removed);
    }

    [Fact]
    public async Task DeleteReportsAnUnknownRoleAsNotFound()
    {
        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            DeleteHandler(new Repo(), new Uow(), new Usage()).HandleAsync(
                new DeleteRoleCommand(Tenant, RoleId.New()),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.role.not_found", error.Code);
    }

    // ---- Actualizar ----

    private static UpdateRoleHandler UpdateHandler(Repo repo, Uow uow, Usage usage) =>
        new(
            repo,
            uow,
            SystemCatalog(),
            usage,
            new Context(Tenant, "advisorship.roles.manage"),
            new StubClock(),
            new RecordingVersion(uow.Steps));

    [Fact]
    public async Task UpdateReplacesPermissionsAndName()
    {
        var role = CustomRole("ventas-junior", "catalog.product.read");
        var repo = new Repo(role);
        var uow = new Uow();

        var updated = await UpdateHandler(repo, uow, new Usage([SystemRoleKeys.Admin]))
            .HandleAsync(
                new UpdateRoleCommand(
                    Tenant, role.Id, "Ventas senior", "Con gestion", ["advisorship.manage"], 1),
                TestContext.Current.CancellationToken);

        Assert.Equal("Ventas senior", updated.DisplayName);
        Assert.Equal(["advisorship.manage"], updated.Permissions);
        Assert.Equal(1, uow.Saves);
    }

    [Fact]
    public async Task UpdateRefusesAStaleVersion()
    {
        var role = CustomRole("ventas-junior", "catalog.product.read");

        var error = await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            UpdateHandler(new Repo(role), new Uow(), new Usage([SystemRoleKeys.Admin]))
                .HandleAsync(
                    new UpdateRoleCommand(
                        Tenant, role.Id, "Otro", "", ["catalog.product.read"], 99),
                    TestContext.Current.CancellationToken));

        Assert.Equal("concurrency.conflict", error.Code);
    }

    [Fact]
    public async Task UpdateRefusesToRemoveTheLastWayToManageMembers()
    {
        // El agujero que `SuspendMember` y `RemoveMember` ya tapan por su lado: si la unica
        // persona que administra lo es por un rol custom, quitarle `advisorship.manage` a ese
        // rol deja al tenant sin nadie capaz de tocar membresias. Y a diferencia de suspender,
        // aca no hay a quien reactivar: el permiso se fue del rol.
        var role = CustomRole("jefa", "advisorship.manage");
        var repo = new Repo(role);

        var error = await Assert.ThrowsAsync<AuthorizationDomainException>(() =>
            UpdateHandler(repo, new Uow(), new Usage(["jefa"])).HandleAsync(
                new UpdateRoleCommand(
                    Tenant, role.Id, "Jefa", "", ["catalog.product.read"], 1),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.role.last_active_manager", error.Code);
    }

    [Fact]
    public async Task UpdateAllowsRemovingManageWhenSomebodyElseKeepsIt()
    {
        var role = CustomRole("jefa", "advisorship.manage");
        var repo = new Repo(role);
        var uow = new Uow();

        // Alguien mas administra, por el rol de sistema: el cambio deja de ser un lockout.
        await UpdateHandler(repo, uow, new Usage(["jefa"], [SystemRoleKeys.Admin])).HandleAsync(
            new UpdateRoleCommand(Tenant, role.Id, "Jefa", "", ["catalog.product.read"], 1),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, uow.Saves);
    }

    // ---- operator.* (spec 2026-10-08 D11) ----

    // operator.* sólo vive en roles de sistema, en cualquier tenant.
    [Fact]
    public async Task CreateRejectsAnOperatorPermission()
    {
        var error = await Assert.ThrowsAsync<AuthorizationDomainException>(() =>
            CreateHandler(new Repo(), new Uow()).HandleAsync(
                new CreateRoleCommand(Tenant, "operador", "Operador", "", ["operator.tenants.read"]),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.role.permission_operator_only", error.Code);
    }

    [Fact]
    public async Task UpdateRejectsAnOperatorPermission()
    {
        var role = CustomRole("ventas", "catalog.product.read");
        var repo = new Repo(role);

        var error = await Assert.ThrowsAsync<AuthorizationDomainException>(() =>
            UpdateHandler(repo, new Uow(), new Usage()).HandleAsync(
                new UpdateRoleCommand(Tenant, role.Id, "Ventas", "", ["catalog.product.read", " operator.modules.manage"], role.Version),
                TestContext.Current.CancellationToken));

        Assert.Equal("authorization.role.permission_operator_only", error.Code);
    }
}
