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

    /// <summary>Con seguimiento: descartar, restaurar y confirmar cambian la fila y la guardan con
    /// <see cref="IGeographyUnitOfWork.SaveChangesAsync"/>.</summary>
    Task<UnresolvedCity?> FindAsync(UnresolvedCityId id, CancellationToken cancellationToken);

    /// <summary>
    /// Una página de filas en <paramref name="status"/>, por <c>last_seen_at</c> descendente y luego <c>id</c> descendente, con
    /// sus conteos. <paramref name="normalizedSearch"/> (ya de <see cref="CityText.Normalize"/>) se busca dentro del texto
    /// normalizado. <c>Total</c> cuenta todas las filas del filtro, no sólo las de la página.
    /// </summary>
    Task<(IReadOnlyList<UnresolvedCityRow> Rows, int Total)> ListAsync(
        UnresolvedCityStatus status,
        DepartmentId? departmentId,
        string? normalizedSearch,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Todas las ocurrencias de la fila, sin seguimiento.</summary>
    Task<IReadOnlyList<UnresolvedCityOccurrence>> ListOccurrencesAsync(UnresolvedCityId id, CancellationToken cancellationToken);

    /// <summary>Los conteos del resumen: filas <c>Pending</c> y <c>Discarded</c>, sinónimos vivos y ocurrencias de filas
    /// <c>Pending</c>.</summary>
    Task<CitySummaryCounts> CountAsync(CancellationToken cancellationToken);
}

/// <summary>Una fila de la bandeja con sus pedidos (ocurrencias) y los tenants distintos de esas ocurrencias.</summary>
public sealed record UnresolvedCityRow(UnresolvedCity City, int OrderCount, int TenantCount);

public sealed record CitySummaryCounts(int Pending, int Discarded, int Synonyms, int OrdersWaiting);
