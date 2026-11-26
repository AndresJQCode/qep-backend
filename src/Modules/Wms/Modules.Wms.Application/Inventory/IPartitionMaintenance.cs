namespace Modules.Wms.Application;

/// <summary>Particiones mensuales de <c>wms.stock_movements</c> (RNF-08, ADR-10). A propósito sólo crea y consulta: desprender una
/// partición es una decisión operativa fuera de la ventana de <c>occurredAt</c>, nunca de un worker.</summary>
public interface IPartitionMaintenance
{
    /// <summary>Crea la partición del mes de <paramref name="month"/> (UTC) si no existe. Falla si la partición <c>DEFAULT</c> ya tiene
    /// filas de ese mes.</summary>
    Task EnsureAsync(DateOnly month, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(DateOnly month, CancellationToken cancellationToken);
}
