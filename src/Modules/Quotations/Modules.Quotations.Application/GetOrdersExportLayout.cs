using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record GetOrdersExportLayoutQuery(Guid TenantId) : IQuery<OrdersExportLayoutDto>;

/// <summary>
/// El layout efectivo del tenant (spec 2026-09-24, D8): la fila si la hay, completada con el
/// catálogo, o el catálogo tal cual con la versión implícita 1 (D9). Sin efecto colateral: no crea
/// la fila. Permiso de settings (D5) y revalidación de tenant, 403 y nunca 404: siempre hay un
/// layout que responder.
/// </summary>
public sealed class GetOrdersExportLayoutHandler(
    IOrdersExportLayoutRepository repository,
    IExecutionContext executionContext)
    : IQueryHandler<GetOrdersExportLayoutQuery, OrdersExportLayoutDto>
{
    public async Task<OrdersExportLayoutDto> HandleAsync(
        GetOrdersExportLayoutQuery query,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, TenancyPermissions.SettingsRead);

        var stored = await repository.FindAsync(query.TenantId, cancellationToken);
        return OrdersExportLayoutMappings.ToDto(stored, query.TenantId);
    }
}
