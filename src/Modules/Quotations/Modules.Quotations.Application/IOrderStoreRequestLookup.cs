namespace Modules.Quotations.Application;

/// <summary>
/// La solicitud de la tienda pública de la que salió un pedido. Quotations no conoce el módulo Store: el composition root cablea
/// el adaptador. El detalle la muestra para que el asesor tenga a mano el teléfono y el total que vio el visitante.
/// </summary>
public interface IOrderStoreRequestLookup
{
    Task<OrderStoreRequestRef?> FindAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);
}

/// <summary>
/// Nombre, teléfono y correo son los del bloque de facturación. <paramref name="TotalDifference"/> es el total del pedido al
/// convertir menos <paramref name="DisplayedTotal"/>; null cuando coinciden.
/// </summary>
public sealed record OrderStoreRequestRef(
    Guid Id,
    string Number,
    Guid CustomerId,
    string CustomerName,
    string Phone,
    string PhoneE164,
    string Email,
    decimal DisplayedTotal,
    string Currency,
    decimal? TotalDifference);
