using Microsoft.Extensions.DependencyInjection;
using Modules.Buyers.Application;
using Modules.Quotations.Application;

namespace Bootstrapper;

/// <summary>
/// Buyers → Quotations para el alta diferida del checkout (pedidos del comprador §4.2(2)). Como <see cref="BuyerOrdersAdapter"/>,
/// resuelve el gateway en un scope propio: el worker de Buyers corre en su scope, y una carrera que limpie el tracker de Quotations
/// no debe tocar nada del llamador.
/// </summary>
internal sealed class BuyerOrderLinker(IServiceScopeFactory scopes) : IBuyerOrderLinker
{
    public async Task LinkAsync(Guid tenantId, Guid orderId, Guid accountId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IBuyerOrderGateway>()
            .LinkAsync(tenantId, orderId, accountId, cancellationToken);
    }
}
