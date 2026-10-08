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
    IClock clock)
    : ITenantRegistration
{
    private static readonly string[] AdminRoles = [Membership.AdminRole];
    private const string Origin = Membership.RegistrationOrigin;

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
            now);
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
        return tenant.Id.Value;
    }
}
