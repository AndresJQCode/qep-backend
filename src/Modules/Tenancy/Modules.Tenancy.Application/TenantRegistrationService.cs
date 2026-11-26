using BuildingBlocks.Application;
using Modules.Audit.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed class TenantRegistrationService(
    ITenantRepository tenantRepository,
    IMembershipRepository membershipRepository,
    ITenantModuleRepository tenantModuleRepository,
    ITenantModuleDefaults tenantModuleDefaults,
    ITenancyUnitOfWork unitOfWork,
    IAuditRecorder auditRecorder,
    IOutboxWriter outboxWriter,
    IClock clock,
    ITenantSlugAvailability slugAvailability,
    IEnumerable<INewTenantProvisioning> provisioning)
    : ITenantRegistration
{
    private static readonly string[] AdminRoles = [Membership.AdminRole];
    private const string Origin = Membership.RegistrationOrigin;

    public async Task EnsureRegistrableAsync(TenantRegistrationData data, CancellationToken cancellationToken)
    {
        // Mismas reglas y códigos que Tenant.Create: el formato falla antes de tocar la base.
        var slug = Tenant.EnsureValidRegistration(
            data.Slug, data.DisplayName, data.DefaultCulture, data.TimeZone, data.DateFormat, data.Country, data.Language);

        if (await slugAvailability.IsTakenAsync(slug, cancellationToken))
        {
            throw new TenantDomainException("tenancy.slug.taken", "Tenant slug is already in use.");
        }
    }

    public async Task<Guid> RegisterOwnerTenantAsync(
        Guid ownerUserId,
        TenantRegistrationData data,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        // Los dos ids se acuñan antes que cualquiera de los dos objetos: ninguno de los dos
        // necesita que el otro exista, sólo su id. Eso es lo que deja que el tenant nazca ya
        // nombrando a su autoridad, en vez de existir un rato sin owner.
        var tenantId = TenantId.New();
        var membershipId = MembershipId.New();

        var tenant = Tenant.Create(
            tenantId,
            data.Slug,
            data.DisplayName,
            data.DefaultCulture,
            data.TimeZone,
            data.DateFormat,
            membershipId,
            now,
            data.Country,
            data.Language);
        tenantRepository.Add(tenant);

        // Spec 2026-10-07, «Alta por signup»: los módulos del tenant se commitean con el tenant y la
        // membresía en el SaveChangesAsync de abajo, así que un tenant nunca existe sin las filas que
        // le tocan. Cuáles, lo decide Entitlements:GrantDefaultModulesOnSignup.
        foreach (var key in tenantModuleDefaults.ForNewTenants)
        {
            tenantModuleRepository.Add(TenantModule.Create(tenantId, key, TenantModuleSources.Signup, now, note: null));
        }

        var membership = Membership.CreateActive(
            membershipId,
            ownerUserId,
            tenantId,
            AdminRoles,
            Origin,
            now);
        membershipRepository.Add(membership);

        auditRecorder.Record(
            tenant.Id.Value,
            ownerUserId,
            "tenancy.tenant.registered",
            "tenant",
            tenant.Id.ToString(),
            "success",
            [],
            now);

        foreach (var domainEvent in membership.PullDomainEvents())
        {
            outboxWriter.Add(domainEvent, correlationId);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await ProvisionAsync(tenant.Id, cancellationToken);
        return tenant.Id.Value;
    }

    public async Task<bool> RegisterInvitedTenantAsync(
        TenantId tenantId,
        Guid ownerUserId,
        TenantRegistrationData data,
        IReadOnlyList<TenantModuleKey> modules,
        string correlationId,
        CancellationToken cancellationToken)
    {
        EnsureClosedModuleSet(modules);

        // El candado del tenant reservado serializa dos aceptaciones simultáneas (doble clic, dos pestañas): la segunda
        // espera, ve el tenant de la primera y termina sin escribir.
        await using var scope = await unitOfWork.BeginTenantChangeScopeAsync(tenantId, cancellationToken);

        var existing = await tenantRepository.GetAsync(tenantId, cancellationToken);
        if (existing is not null)
        {
            var membership = await membershipRepository.FindByUserAndTenantAsync(ownerUserId, tenantId, cancellationToken);
            if (membership is not null && existing.IsOwner(membership.Id))
            {
                return false;
            }

            // Nunca debería pasar: el id se reserva por invitación y la invitación está atada a un correo. Si pasa, no se
            // le entrega a nadie un tenant ajeno.
            throw new InvalidOperationException($"The reserved tenant {tenantId} already exists with another owner.");
        }

        var now = clock.UtcNow;
        var membershipId = MembershipId.New();
        var tenant = Tenant.Create(
            tenantId,
            data.Slug,
            data.DisplayName,
            data.DefaultCulture,
            data.TimeZone,
            data.DateFormat,
            membershipId,
            now,
            data.Country,
            data.Language);
        tenantRepository.Add(tenant);

        foreach (var key in modules)
        {
            tenantModuleRepository.Add(TenantModule.Create(tenantId, key, TenantModuleSources.Invitation, now, note: null));
        }

        var owner = Membership.CreateActive(membershipId, ownerUserId, tenantId, AdminRoles, Origin, now);
        membershipRepository.Add(owner);

        auditRecorder.Record(
            tenant.Id.Value,
            ownerUserId,
            "tenancy.tenant.registered",
            "tenant",
            tenant.Id.ToString(),
            "success",
            [],
            now);

        foreach (var domainEvent in owner.PullDomainEvents())
        {
            outboxWriter.Add(domainEvent, correlationId);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        await ProvisionAsync(tenant.Id, cancellationToken);
        return true;
    }

    /// <summary>En orden de registro y uno por vez: lo que uno deje subir corta los que siguen.</summary>
    private async Task ProvisionAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        foreach (var step in provisioning)
        {
            await step.ProvisionAsync(tenantId, cancellationToken);
        }
    }

    /// <summary>
    /// Ruling W11 (a): la lista la arma otro módulo, así que no se confía en ella. Una clave repetida sería una violación de
    /// PK (500) y una sin sus dependencias dejaría un tenant inconsistente; las dos fallan antes del candado.
    /// </summary>
    private static void EnsureClosedModuleSet(IReadOnlyList<TenantModuleKey> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var keys = modules.ToHashSet();
        if (keys.Count != modules.Count)
        {
            throw new ArgumentException("The invited tenant modules cannot repeat a key.", nameof(modules));
        }

        var missing = keys
            .SelectMany(key => TenantModuleKeys.DependenciesOf(key).Where(dependency => !keys.Contains(dependency)))
            .Distinct()
            .ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException(
                $"The invited tenant modules miss their dependencies: {string.Join(", ", missing)}.", nameof(modules));
        }
    }
}
