using Microsoft.EntityFrameworkCore;
using Modules.Quotations.Application;
using Modules.Siigo.Infrastructure.Persistence;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// El adaptador de <see cref="IExternalInvoiceStatus"/> (spec 2026-11-18 §5.5): ¿hay una factura de Siigo viva (todo menos
/// <c>Rejected</c>, <c>Failed</c> y <c>Annulled</c>: saliendo, sin resolver o ya creada) para el pedido? Bloquea la marca manual y la
/// anulación. Es SQL crudo sobre <c>siigo.invoices</c> por nombre (Quotations no referencia a Siigo): renombrar
/// <c>status</c>, <c>order_id</c> o <c>tenant_id</c> compila y deja la marca manual del pedido en 500 (<c>42703</c>); lo amarra
/// <c>SiigoInvoiceApiTests.TheRealStatusAdapterSeesOpenInvoices</c>. Una <c>Pending</c> de más de 24 horas sin leerse
/// también cuenta: la transición a <c>Unknown</c> es perezosa. <b>Con el módulo <c>siigo</c> apagado no bloquea nada</b>: sin
/// módulo los endpoints de Siigo responden 403 y no hay pantalla para resolver una factura <c>Pending</c> o <c>Unknown</c>, así que
/// seguiría bloqueando la marca manual y la anulación para siempre. Un tenant que nunca tuvo Siigo no tiene filas y sigue igual.
/// </summary>
internal sealed class SiigoOpenInvoiceCheck(SiigoDbContext dbContext, ITenantModules tenantModules) : IExternalInvoiceStatus
{
    public async Task<bool> HasOpenInvoiceAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken)
    {
        var modules = await tenantModules.FindAsync(tenantId, cancellationToken);
        if (modules is not null && !modules.IsEnabled(TenantModuleKeys.Siigo))
        {
            return false;
        }

        return await dbContext.Database
            .SqlQuery<int>(
                $"""
                 SELECT 1 AS "Value"
                 FROM siigo.invoices
                 WHERE tenant_id = {tenantId} AND order_id = {orderId} AND status NOT IN ('Rejected', 'Failed', 'Annulled')
                 LIMIT 1
                 """)
            .AnyAsync(cancellationToken);
    }
}
