namespace Modules.Pos.Application;

/// <summary>
/// Contador atómico por tenant. Se llama adentro de la transacción de la venta: si la venta no
/// commitea, el número no se gasta.
/// </summary>
public interface IPosSaleNumberGenerator
{
    Task<long> NextAsync(Guid tenantId, CancellationToken cancellationToken);
}
