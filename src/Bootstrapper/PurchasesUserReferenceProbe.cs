using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Purchases.Infrastructure.Persistence;
using Modules.Tenancy.Application;

namespace Bootstrapper;

/// <summary>
/// Purchases retiene a un usuario mientras alguna de sus membresías haya creado una orden de compra
/// (<c>purchase_orders.created_by_member_id</c>) o dejado un paso en su historial (<c>purchase_order_history.member_id</c>),
/// precedente <see cref="LoansUserReferenceProbe"/>. Vive en el composition root porque necesita
/// <see cref="IMembershipDirectory"/> (Tenancy) y el <see cref="PurchasesDbContext"/> a la vez.
/// </summary>
/// <remarks>
/// Una columna nueva con un id de membresía en el esquema <c>purchases</c> tiene que sumarse acá, o el usuario que la referencia
/// se borra igual.
/// </remarks>
internal sealed class PurchasesUserReferenceProbe(
    PurchasesDbContext dbContext,
    IMembershipDirectory membershipDirectory) : IUserReferenceProbe
{
    public string Source => "purchases";

    public async Task<bool> HasReferencesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var membershipIds = await membershipDirectory.ListMembershipIdsByUserAsync(userId, cancellationToken);

        // Una consulta por membresía, como las demás sondas: un usuario tiene una por tenant.
        foreach (var membershipId in membershipIds)
        {
            if (await dbContext.PurchaseOrders.AnyAsync(order => order.CreatedByMemberId == membershipId, cancellationToken) ||
                await dbContext.PurchaseOrders.AnyAsync(
                    order => order.History.Any(entry => entry.MemberId == membershipId), cancellationToken))
            {
                return true;
            }
        }

        return false;
    }
}
