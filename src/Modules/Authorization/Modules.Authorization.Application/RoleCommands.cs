using BuildingBlocks.Application;
using Modules.Authorization.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Authorization.Application;

public sealed record CreateRoleCommand(
    TenantId TenantId,
    string Key,
    string DisplayName,
    string Description,
    IReadOnlyCollection<string> Permissions) : ICommand<Role>;

public sealed record UpdateRoleCommand(
    TenantId TenantId,
    RoleId RoleId,
    string DisplayName,
    string Description,
    IReadOnlyCollection<string> Permissions,
    long ExpectedVersion) : ICommand<Role>;

public sealed record DeleteRoleCommand(TenantId TenantId, RoleId RoleId) : ICommand<bool>;

/// <summary>
/// Las comprobaciones que los tres comandos comparten.
/// </summary>
/// <remarks>
/// Ninguna vive en <see cref="Role"/>, y no por comodidad: el agregado no conoce el catálogo
/// de permisos —se arma en composición— ni las membresías del tenant. Es el mismo reparto que
/// ya usa <c>InviteMemberHandler</c>, que valida los roles contra un validador inyectado en
/// vez de hacerlo dentro de <c>Membership</c>.
/// </remarks>
internal static class RoleWriteRules
{
    public const string Permission = TenancyPermissions.AdvisorshipRolesManage;

    /// <summary>
    /// Exige <c>advisorship.roles.manage</c>, que NO es <c>advisorship.manage</c>.
    /// </summary>
    /// <remarks>
    /// La separación es la que evita una escalada trivial: quien administra miembros puede
    /// nombrar asesoras, y si eso alcanzara para reescribir lo que una asesora puede hacer,
    /// podría concederse cualquier permiso del sistema sin pasar por nadie.
    /// </remarks>
    public static void EnsureAuthorized(IExecutionContext context, TenantId tenantId)
    {
        if (context.TenantId != tenantId || !context.HasPermission(Permission))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot define roles for this tenant.");
        }
    }

    /// <summary>
    /// Un rol sólo puede conceder permisos que el catálogo declara.
    /// </summary>
    /// <remarks>
    /// Sin esto un rol concede un permiso que ningún endpoint respeta: no falla en ningún
    /// lado, simplemente su gente descubre que "no le funciona" sin un solo error a la vista.
    /// </remarks>
    public static void EnsureKnownPermissions(
        IRoleCatalog catalog,
        IReadOnlyCollection<string> permissions)
    {
        var known = catalog.ListPermissions()
            .Select(permission => permission.Permission)
            .ToHashSet(StringComparer.Ordinal);

        var unknown = permissions.FirstOrDefault(
            permission => !known.Contains(permission.Trim()));
        if (unknown is not null)
        {
            throw new AuthorizationDomainException(
                "authorization.role.permission_unknown",
                $"The permission '{unknown}' is not part of the authorization catalog.");
        }
    }

    public static async Task<Role> LoadAsync(
        IRoleRepository repository,
        RoleId id,
        TenantId tenantId,
        CancellationToken cancellationToken) =>
        await repository.FindByIdAsync(id, tenantId.Value, cancellationToken)
            // Un id de otro tenant responde lo mismo que uno inexistente: decir "existe pero
            // no es tuyo" filtra que existe.
            ?? throw new ResourceNotFoundException(
                "authorization.role.not_found",
                "The role was not found.");
}

public sealed class CreateRoleHandler(
    IRoleRepository repository,
    IAuthorizationUnitOfWork unitOfWork,
    IRoleCatalog systemCatalog,
    IExecutionContext executionContext,
    IClock clock) : ICommandHandler<CreateRoleCommand, Role>
{
    public async Task<Role> HandleAsync(
        CreateRoleCommand command,
        CancellationToken cancellationToken)
    {
        RoleWriteRules.EnsureAuthorized(executionContext, command.TenantId);
        RoleWriteRules.EnsureKnownPermissions(systemCatalog, command.Permissions);

        // `Role.Create` normaliza la clave, así que se busca la normalizada: preguntar por
        // "Ventas-Junior" no encontraría la fila "ventas-junior" que sí colisiona.
        var role = Role.Create(
            RoleId.New(),
            command.TenantId.Value,
            command.Key,
            command.DisplayName,
            command.Description,
            command.Permissions,
            clock.UtcNow);

        // El índice único lo impediría igual, pero llegar hasta PostgreSQL para decir algo que
        // ya sabemos convierte un dato en un 500 traducido. La traducción del 23505 se queda
        // igual: cubre la carrera entre dos requests, que esta consulta no puede cubrir.
        var existing = await repository.FindByKeyAsync(
            command.TenantId.Value, role.Key, cancellationToken);
        if (existing is not null)
        {
            throw new AuthorizationDomainException(
                "authorization.role.key_taken",
                $"The key '{role.Key}' is already in use in this organization.");
        }

        repository.Add(role);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return role;
    }
}

public sealed class UpdateRoleHandler(
    IRoleRepository repository,
    IAuthorizationUnitOfWork unitOfWork,
    IRoleCatalog systemCatalog,
    IMembershipRoleUsage roleUsage,
    IExecutionContext executionContext,
    IClock clock) : ICommandHandler<UpdateRoleCommand, Role>
{
    public async Task<Role> HandleAsync(
        UpdateRoleCommand command,
        CancellationToken cancellationToken)
    {
        RoleWriteRules.EnsureAuthorized(executionContext, command.TenantId);
        RoleWriteRules.EnsureKnownPermissions(systemCatalog, command.Permissions);

        var role = await RoleWriteRules.LoadAsync(
            repository, command.RoleId, command.TenantId, cancellationToken);

        if (role.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The role changed after it was loaded.");
        }

        await EnsureSomebodyKeepsManagingAsync(role, command.Permissions, cancellationToken);

        role.Rename(command.DisplayName, command.Description, clock.UtcNow);
        role.ChangePermissions(command.Permissions, clock.UtcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return role;
    }

    /// <summary>
    /// Impide que quitarle <c>advisorship.manage</c> a un rol deje al tenant sin nadie capaz
    /// de administrar miembros.
    /// </summary>
    /// <remarks>
    /// <c>SuspendMember</c>, <c>RemoveMember</c> y <c>UpdateMemberRoles</c> ya protegen ese
    /// invariante desde el lado de la persona. Este es el mismo lockout por la otra puerta: no
    /// se toca a nadie, se vacía el rol que les daba la capacidad.
    ///
    /// Y es peor que suspender, porque no hay a quién reactivar — el permiso se fue del rol, y
    /// recuperarlo exige justamente el permiso que ya nadie tiene.
    /// </remarks>
    private async Task EnsureSomebodyKeepsManagingAsync(
        Role role,
        IReadOnlyCollection<string> nextPermissions,
        CancellationToken cancellationToken)
    {
        const string Manage = TenancyPermissions.AdvisorshipManage;

        var grantsToday = role.Permissions.Contains(Manage, StringComparer.Ordinal);
        var willGrant = nextPermissions.Contains(Manage, StringComparer.Ordinal);
        if (!grantsToday || willGrant)
        {
            return;
        }

        var roleSets = await roleUsage.ActiveRoleSetsAsync(
            new TenantId(role.TenantId), cancellationToken);

        // Se pregunta por CADA persona con sus roles juntos: alguien puede tener el rol que se
        // está vaciando y además otro que administra, y sigue administrando después del cambio.
        var somebodyKeepsIt = false;
        foreach (var roles in roleSets)
        {
            if (await GrantsManageAfterTheChangeAsync(roles, role, cancellationToken))
            {
                somebodyKeepsIt = true;
                break;
            }
        }

        if (!somebodyKeepsIt)
        {
            throw new AuthorizationDomainException(
                "authorization.role.last_active_manager",
                "The tenant must retain at least one member who can manage memberships.");
        }
    }

    private Task<bool> GrantsManageAfterTheChangeAsync(
        IReadOnlyCollection<string> roles,
        Role changing,
        CancellationToken cancellationToken)
    {
        const string Manage = TenancyPermissions.AdvisorshipManage;

        foreach (var key in roles)
        {
            // El rol que se está editando se evalúa por lo que va a quedar, no por lo que es:
            // preguntarle al catálogo devolvería los permisos de antes del cambio.
            if (StringComparer.Ordinal.Equals(key, changing.Key))
            {
                continue;
            }

            if (systemCatalog.PermissionsFor(key).Contains(Manage, StringComparer.Ordinal))
            {
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }
}

public sealed class DeleteRoleHandler(
    IRoleRepository repository,
    IAuthorizationUnitOfWork unitOfWork,
    IMembershipRoleUsage roleUsage,
    IExecutionContext executionContext) : ICommandHandler<DeleteRoleCommand, bool>
{
    public async Task<bool> HandleAsync(
        DeleteRoleCommand command,
        CancellationToken cancellationToken)
    {
        RoleWriteRules.EnsureAuthorized(executionContext, command.TenantId);

        var role = await RoleWriteRules.LoadAsync(
            repository, command.RoleId, command.TenantId, cancellationToken);

        // Borrar un rol que alguien tiene puesto no explota en ningún lado: `PermissionsForAsync`
        // ignora lo que no conoce, así que esa gente pierde permisos en silencio y nadie
        // relaciona una cosa con la otra. Se rechaza y se pide reasignar primero.
        var roleSets = await roleUsage.ActiveRoleSetsAsync(command.TenantId, cancellationToken);
        var inUse = roleSets.Any(
            roles => roles.Contains(role.Key, StringComparer.Ordinal));
        if (inUse)
        {
            throw new AuthorizationDomainException(
                "authorization.role.in_use",
                "The role is assigned to at least one member. Reassign them first.");
        }

        repository.Remove(role);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
