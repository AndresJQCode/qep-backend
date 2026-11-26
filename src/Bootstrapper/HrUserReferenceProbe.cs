using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Hr.Infrastructure.Persistence;
using Modules.Tenancy.Application;

namespace Bootstrapper;

/// <summary>
/// Hr retiene a un usuario mientras alguna de sus membresías esté vinculada a un empleado (<c>hr.employees.member_id</c>),
/// precedente <see cref="LoansUserReferenceProbe"/>. Vive en el composition root porque necesita
/// <see cref="IMembershipDirectory"/> (Tenancy) y el <see cref="HrDbContext"/> a la vez.
/// </summary>
/// <remarks>
/// Una columna nueva con un id de membresía en el esquema <c>hr</c> tiene que sumarse acá, o el usuario que la referencia se
/// borra igual.
/// </remarks>
internal sealed class HrUserReferenceProbe(
    HrDbContext dbContext,
    IMembershipDirectory membershipDirectory) : IUserReferenceProbe
{
    public string Source => "hr";

    public async Task<bool> HasReferencesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var membershipIds = await membershipDirectory.ListMembershipIdsByUserAsync(userId, cancellationToken);

        // Una consulta por membresía, como las demás sondas: un usuario tiene una por tenant.
        foreach (var membershipId in membershipIds)
        {
            if (await dbContext.Employees.AnyAsync(employee => employee.MemberId == membershipId, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }
}
