using Modules.Geography.Domain;

namespace Modules.Geography.Application;

/// <summary>La bandeja de ciudades sin resolver (spec 2026-10-26 §5). La escritura es SQL y no la entidad: el reporte es
/// un upsert sobre un índice único parcial, que EF no sabe expresar.</summary>
public interface IUnresolvedCityRepository
{
    /// <summary>
    /// Dentro de la transacción y con el candado del texto ya tomado: upsert de la fila abierta y alta idempotente de la
    /// ocurrencia. <paramref name="report"/> llega ya recortado y <paramref name="normalizedText"/> es la llave de la fila.
    /// Si la fila de origen ya tiene ocurrencia devuelve la fila de esa ocurrencia y <c>false</c>, y lo que haya escrito
    /// se descarta con el rollback.
    /// </summary>
    Task<(UnresolvedCityId Id, bool OccurrenceInserted)> UpsertWithOccurrenceAsync(
        UnresolvedCityReport report, string normalizedText, CancellationToken cancellationToken);

    // SC-10/SC-11 agregan las lecturas.
}
