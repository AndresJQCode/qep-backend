using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// SALE-04: el detalle de un pedido, direccionado por su propio id.
///
/// Existe aparte de <see cref="GetOrderQuery"/> —que entra por la cotización— porque el listado
/// muestra pedidos: desde una fila no hay por dónde entrar si la única ruta cuelga de otro
/// recurso.
/// </summary>
public sealed record GetOrderByIdQuery(Guid TenantId, Guid OrderId) : IQuery<OrderDetailDto>;

/// <summary>
/// El pedido junto a su cotización. Viajan las dos porque la pantalla necesita las dos: cliente,
/// partes, productos y totales viven en la cotización y el pedido no los repite.
///
/// Una sola respuesta y no dos llamadas encadenadas: pedir primero el pedido para enterarse del
/// `quotationId` y recién entonces la cotización es una cascada que el backend puede evitar, y
/// este módulo ya tiene las dos a mano.
/// </summary>
public sealed record OrderDetailDto(OrderDto Order, QuotationDto Quotation);

public sealed class GetOrderByIdHandler(
    IOrderRepository orderRepository,
    IQuotationRepository quotationRepository,
    IExecutionContext executionContext)
    : IQueryHandler<GetOrderByIdQuery, OrderDetailDto>
{
    public async Task<OrderDetailDto> HandleAsync(
        GetOrderByIdQuery query,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, OrdersPermissions.SaleRead);

        var order = await orderRepository.FindByIdAsync(
            query.TenantId, new OrderId(query.OrderId), cancellationToken)
            ?? throw OrderNotFound.ById(query.OrderId);

        // Sin la cotización no hay detalle que dibujar: el pedido no guarda ni el cliente ni las
        // líneas. Que falte seria un pedido huérfano --imposible por la FK-- asi que se trata
        // como "no encontrada" y no como un detalle a medias.
        var quotation = await quotationRepository.FindAsync(
            query.TenantId, order.QuotationId, cancellationToken)
            ?? throw OrderNotFound.ById(query.OrderId);

        return new OrderDetailDto(order.ToDto(), quotation.ToDto());
    }
}
