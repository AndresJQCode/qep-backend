using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Loans.Infrastructure.Persistence;
using Modules.Tenancy.Application;

namespace Bootstrapper;

/// <summary>
/// Loans retiene a un usuario mientras alguna de sus membresías sea la encargada de un préstamo
/// (<c>loans.assignee_member_id</c>) o haya marcado el pago de una cuota (<c>installments.paid_by_member_id</c>), precedente
/// <see cref="ServiceContractsUserReferenceProbe"/>. Vive en el composition root porque necesita
/// <see cref="IMembershipDirectory"/> (Tenancy) y el <see cref="LoansDbContext"/> a la vez.
/// </summary>
/// <remarks>
/// Una columna nueva con un id de membresía en el esquema <c>loans</c> tiene que sumarse acá, o el usuario que la referencia
/// se borra igual.
/// </remarks>
internal sealed class LoansUserReferenceProbe(
    LoansDbContext dbContext,
    IMembershipDirectory membershipDirectory) : IUserReferenceProbe
{
    public string Source => "loans";

    public async Task<bool> HasReferencesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var membershipIds = await membershipDirectory.ListMembershipIdsByUserAsync(userId, cancellationToken);

        // Una consulta por membresía, como las demás sondas: un usuario tiene una por tenant.
        foreach (var membershipId in membershipIds)
        {
            if (await dbContext.Loans.AnyAsync(loan => loan.AssigneeMemberId == membershipId, cancellationToken) ||
                await dbContext.Installments.AnyAsync(installment => installment.PaidByMemberId == membershipId, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }
}
