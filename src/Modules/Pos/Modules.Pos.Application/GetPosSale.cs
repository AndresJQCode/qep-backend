using BuildingBlocks.Application;
using Modules.Pos.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <summary>Detalle, reimpresión y "Verificar" del cobro incierto.</summary>
public sealed record GetPosSaleQuery(Guid TenantId, Guid SaleId) : IQuery<PosSaleResponse>;

public sealed class GetPosSaleHandler(
    IPosSaleRepository sales,
    ICashSessionRepository sessions,
    IPosCashierLookup cashiers,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    ITenantClock tenantClock)
    : IQueryHandler<GetPosSaleQuery, PosSaleResponse>
{
    public async Task<PosSaleResponse> HandleAsync(GetPosSaleQuery query, CancellationToken cancellationToken)
    {
        PosAuthorization.EnsureAuthorized(executionContext, query.TenantId, PosPermissions.SaleRead);
        // El id vacío de la ruta es un id que no existe: 404, no el 422 del value object.
        if (query.SaleId == Guid.Empty)
        {
            throw PosNotFound.Sale(query.SaleId);
        }

        var sale = await sales.FindAsync(query.TenantId, new PosSaleId(query.SaleId), cancellationToken)
            ?? throw PosNotFound.Sale(query.SaleId);
        await PosScope.EnsureCanSeeAsync(sale.CashierId, membershipDirectory, executionContext, query.TenantId, cancellationToken);
        return await PosSaleResponses.BuildAsync(sale, sessions, cashiers, tenantClock, cancellationToken);
    }
}
