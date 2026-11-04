using Modules.Quotations.Application;
using Modules.Store.Application;

namespace Bootstrapper;

/// <summary>
/// Store → Quotations: la solicitud de la tienda de la que salió un pedido, para su detalle. Nombre, teléfono y correo son los del
/// bloque de facturación, que es lo que escribió el visitante; el asesor los necesita para contactarlo aunque el cliente elegido al
/// «Tomar» tenga otros.
/// </summary>
internal sealed class OrderStoreRequestLookup(IStoreOrderRequestRepository requests) : IOrderStoreRequestLookup
{
    public async Task<OrderStoreRequestRef?> FindAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken)
    {
        var request = await requests.FindByOrderAsync(tenantId, orderId, cancellationToken);
        // Convertir fija el pedido y el cliente a la vez: una solicitud con pedido y sin cliente no existe.
        if (request?.CustomerId is not { } customerId)
        {
            return null;
        }

        return new OrderStoreRequestRef(
            request.Id,
            request.DisplayNumber,
            customerId,
            request.Billing.Name,
            request.Billing.Phone,
            request.Billing.PhoneE164,
            request.Billing.Email,
            request.Total,
            request.Currency,
            request.TotalDifference);
    }
}
