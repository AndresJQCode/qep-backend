using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.ServiceContracts.Infrastructure.Persistence;
using Modules.Tenancy.Application;

namespace Bootstrapper;

/// <summary>
/// ServiceContracts retiene a un usuario mientras alguna de sus membresías sea la encargada de un perfil de cobro
/// (<c>customer_profiles.assignee_member_id</c>) o haya marcado un pago (<c>charges.paid_by_member_id</c>); spec
/// cobros-recurrentes §3.1, precedente <c>QuotationUserReferenceProbe</c>. Vive en el composition root porque necesita
/// <see cref="IMembershipDirectory"/> (Tenancy) y el <see cref="ServiceContractsDbContext"/> a la vez.
/// </summary>
/// <remarks>
/// Una columna nueva con un id de membresía en el esquema <c>billing</c> tiene que sumarse acá, o el usuario que la referencia
/// se borra igual. <c>customer_messages</c> y <c>daily_digests</c> guardan el destinatario como foto y no retienen.
/// </remarks>
internal sealed class ServiceContractsUserReferenceProbe(
    ServiceContractsDbContext dbContext,
    IMembershipDirectory membershipDirectory) : IUserReferenceProbe
{
    public string Source => "billing";

    public async Task<bool> HasReferencesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var membershipIds = await membershipDirectory.ListMembershipIdsByUserAsync(userId, cancellationToken);

        // Una consulta por membresía, como la sonda de Quotations: un usuario tiene una por tenant.
        foreach (var membershipId in membershipIds)
        {
            if (await dbContext.Profiles.AnyAsync(profile => profile.AssigneeMemberId == membershipId, cancellationToken) ||
                await dbContext.Charges.AnyAsync(charge => charge.PaidByMemberId == membershipId, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }
}
