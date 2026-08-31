using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Infrastructure.Persistence;

/// <summary>
/// Quotations retiene a un usuario mientras alguna cotización, venta o comprobante siga
/// apuntando a una de sus membresías. El módulo nunca guarda el id de usuario: toda
/// referencia es un <see cref="MemberId"/> hacia <c>tenancy.memberships</c> (documento §1.4),
/// así que la sonda traduce primero usuario → membresías por <see cref="IMembershipDirectory"/>
/// —el mismo contrato que ya consume <c>QuotationAdvisorResolver</c>— y recién después busca.
/// </summary>
/// <remarks>
/// Cubre cada columna mapeada con <see cref="MemberId"/> en <see cref="QuotationsDbContext"/>:
/// <c>quotations.advisor_id</c>, <c>created_by</c> y <c>updated_by</c>;
/// <c>quotation_history.member_id</c>; <c>sales.converted_by</c>; y
/// <c>sale_payment_proofs.uploaded_by</c>. Una columna nueva con <see cref="MemberId"/> tiene
/// que sumarse acá, o el usuario que la referencia se borra igual.
/// </remarks>
internal sealed class QuotationUserReferenceProbe(
    QuotationsDbContext dbContext,
    IMembershipDirectory membershipDirectory) : IUserReferenceProbe
{
    public string Source => "quotations";

    public async Task<bool> HasReferencesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var membershipIds = await membershipDirectory.ListMembershipIdsByUserAsync(
            userId,
            cancellationToken);

        // Una consulta por membresía y no un Contains sobre la lista: un usuario tiene una
        // membresía por tenant, así que son una o dos, y la igualdad sobre el tipo convertido
        // se traduce sin sorpresas.
        foreach (var membershipId in membershipIds)
        {
            var member = new MemberId(membershipId);
            if (await dbContext.Quotations.AnyAsync(
                    quotation => quotation.AdvisorId == member ||
                        quotation.CreatedBy == member ||
                        quotation.UpdatedBy == member,
                    cancellationToken) ||
                await dbContext.QuotationHistoryEntries.AnyAsync(
                    entry => entry.MemberId == member,
                    cancellationToken) ||
                await dbContext.Sales.AnyAsync(
                    sale => sale.ConvertedBy == member,
                    cancellationToken) ||
                await dbContext.SalePaymentProofs.AnyAsync(
                    proof => proof.UploadedBy == member,
                    cancellationToken))
            {
                return true;
            }
        }

        return false;
    }
}
