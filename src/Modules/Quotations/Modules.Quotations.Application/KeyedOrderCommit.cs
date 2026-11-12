using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Guarda un pedido con su consecutivo en una sola transacción y cierra la carrera de la clave externa. Lo
/// comparten el pedido de una integración (<see cref="ExternalOrderService"/>) y el de la tienda pública
/// (<see cref="StoreOrderGateway"/>): los dos son idempotentes por <c>ExternalKey</c> y no deben gastar un
/// número de pedido si pierden la carrera.
/// </summary>
internal static class KeyedOrderCommit
{
    /// <summary>
    /// El contador y el pedido se confirman juntos. <paramref name="build"/> arma el pedido con su número y la hora del
    /// calendario del tenant; <paramref name="stage"/> deja en el unit of work lo demás (historial, auditoría,
    /// evento). Devuelve <c>null</c> sólo si otra llamada con la misma clave guardó primero y el índice cerró la
    /// carrera: la transacción ya se deshizo (el consecutivo vuelve atrás, sin huecos) y quien llama lee al
    /// ganador. Sin <paramref name="externalKey"/> el choque no se espera y se propaga.
    /// </summary>
    public static async Task<Order?> CommitAsync(
        IOrderRepository orderRepository,
        IQuotationsUnitOfWork unitOfWork,
        IOrderNumberGenerator numberGenerator,
        IDocumentNumberingFormatLookup numberingFormats,
        ITenantClock tenantClock,
        Guid tenantId,
        string? externalKey,
        Func<string, DateTimeOffset, Task<Order>> build,
        Action<Order, DateTimeOffset> stage,
        CancellationToken cancellationToken)
    {
        // Mismo consecutivo que los demás pedidos; el contador y el pedido se confirman juntos.
        var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);
        var year = calendar.Today.Year;
        var format = await numberingFormats.GetAsync(tenantId, DocumentNumberType.Order, cancellationToken);
        var counterYear = format.IncludeYear ? year : 0;
        var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        try
        {
            var sequence = await numberGenerator.NextAsync(tenantId, counterYear, cancellationToken);
            var orderNumber = DocumentNumberFormatter.Format(format, year, sequence);
            var order = await build(orderNumber, calendar.UtcNow);
            orderRepository.Add(order);
            stage(order, calendar.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return order;
        }
        catch (QuotationsDomainException error)
            when (externalKey is not null && error.Code == ExternalOrderErrors.ExternalKeyTaken)
        {
            // Otra llamada con la misma clave guardó primero y el índice cerró la carrera. La transacción se
            // deshace en el finally antes de que quien llama lea al ganador.
        }
        finally
        {
            await transaction.DisposeAsync();
        }

        // Lo que quedó pendiente del intento fallido (el pedido, su historial, el evento y la auditoría) no debe
        // volver a intentarse en el próximo guardado de este scope.
        unitOfWork.DiscardPendingChanges();
        return null;
    }
}
