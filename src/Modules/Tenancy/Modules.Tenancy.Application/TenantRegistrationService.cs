using BuildingBlocks.Application;
using Modules.Audit.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed class TenantRegistrationService(
    ITenantRepository tenantRepository,
    IMembershipRepository membershipRepository,
    ITenancyUnitOfWork unitOfWork,
    IAuditRecorder auditRecorder,
    IOutboxWriter outboxWriter,
    IClock clock)
    : ITenantRegistration
{
    private static readonly string[] OwnerRoles = ["tenancy.owner"];
    private const string Origin = "registration";

    public async Task<Guid> RegisterOwnerTenantAsync(
        Guid ownerUserId,
        TenantRegistrationData data,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var tenant = Tenant.Create(
            TenantId.New(),
            data.Slug,
            data.DisplayName,
            data.DefaultCulture,
            data.TimeZone,
            data.DateFormat,
            now);
        tenantRepository.Add(tenant);

        var membership = Membership.CreateActive(
            MembershipId.New(),
            ownerUserId,
            tenant.Id,
            OwnerRoles,
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
