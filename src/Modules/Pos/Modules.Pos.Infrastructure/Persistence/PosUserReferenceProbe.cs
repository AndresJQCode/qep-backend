using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Infrastructure.Persistence;

/// <summary>
/// Una venta es historia: el cajero no se borra mientras tenga una (spec, «Retención de
/// usuarios»). Copia de QuotationUserReferenceProbe.
/// </summary>
internal sealed class PosUserReferenceProbe(
    PosDbContext dbContext,
    IMembershipDirectory membershipDirectory) : IUserReferenceProbe
{
    public string Source => "pos";

    public async Task<bool> HasReferencesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var membershipIds = await membershipDirectory.ListMembershipIdsByUserAsync(userId, cancellationToken);

        foreach (var membershipId in membershipIds)
        {
            var member = new MemberId(membershipId);
            if (await dbContext.CashSessions.AnyAsync(session => session.CashierId == member, cancellationToken)
                || await dbContext.Sales.IgnoreAutoIncludes().AnyAsync(
                    sale => sale.CashierId == member || sale.VoidedBy == member, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }
}
