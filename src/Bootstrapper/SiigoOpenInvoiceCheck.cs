using Modules.Quotations.Application;

namespace Bootstrapper;

/// <summary>
/// El adaptador de <see cref="IExternalInvoiceStatus"/> (spec 2026-11-18 §5.5): ¿hay una factura de Siigo <c>Pending</c> o
/// <c>Unknown</c> para el pedido? Mientras la tabla <c>siigo.invoices</c> no exista (Tarea 10), nunca hay una abierta.
/// </summary>
internal sealed class SiigoOpenInvoiceCheck : IExternalInvoiceStatus
{
    public Task<bool> HasOpenInvoiceAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken) => Task.FromResult(false);
}
