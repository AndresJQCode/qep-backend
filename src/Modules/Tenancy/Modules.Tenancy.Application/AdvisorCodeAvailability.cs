using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// El chequeo previo de unicidad del código de asesor (spec 2026-09-24, D3), compartido por
/// invitar y por PUT .../profile. Existe para responder claro sin depender de la base; ante una
/// carrera la autoridad sigue siendo el índice único parcial, que TenancyUnitOfWork traduce al
/// mismo código.
/// </summary>
internal static class AdvisorCodeAvailability
{
    public static async Task EnsureAvailableAsync(
        IMembershipRepository memberships,
        TenantId tenantId,
        int? advisorCode,
        MembershipId? exceptMembershipId,
        CancellationToken cancellationToken)
    {
        // Sin código no hay nada que chocar (D1): el índice deja convivir a todas las nulas.
        if (advisorCode is not { } code)
        {
            return;
        }

        if (await memberships.IsAdvisorCodeTakenAsync(
                tenantId, code, exceptMembershipId, cancellationToken))
        {
            throw new TenantDomainException(
                "tenancy.membership.advisor_code_taken",
                "The advisor code is already in use in this tenant.");
        }
    }
}
