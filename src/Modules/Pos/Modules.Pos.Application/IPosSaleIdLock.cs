using Modules.Pos.Domain;

namespace Modules.Pos.Application;

/// <summary>
/// Serializa los requests que traen el mismo id de venta (spec, «Crear venta», paso 2, decisión 55).
/// Se toma adentro de la transacción de la venta y se suelta con su commit o su rollback: un
/// reintento que llega mientras el primer envío sigue en vuelo espera a que termine y lo encuentra.
/// </summary>
public interface IPosSaleIdLock
{
    /// <summary>Exige una transacción abierta; si no la hay, lanza <see cref="InvalidOperationException"/>.</summary>
    Task AcquireAsync(Guid tenantId, PosSaleId saleId, CancellationToken cancellationToken);
}
