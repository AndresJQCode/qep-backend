using Modules.Geography.Domain;

namespace Modules.Geography.Application;

/// <summary>Una fila de la bandeja del exterior con sus pedidos (ocurrencias) y los tenants distintos de esas ocurrencias.</summary>
public sealed record UnresolvedForeignCityRow(UnresolvedForeignCity City, int OrderCount, int TenantCount);

/// <summary>La bandeja de ciudades del exterior sin resolver. La escritura es SQL y no la entidad: el reporte es un upsert
/// sobre un índice único parcial, que EF no sabe expresar.</summary>
public interface IUnresolvedForeignCityRepository
{
    /// <summary>
    /// Dentro de la transacción y con el candado del texto ya tomado: upsert de la fila abierta del país y el texto, y alta
    /// idempotente de la ocurrencia del pedido. <paramref name="candidate"/> llega ya recortado y trae la llave. Si el pedido
    /// ya tenía ocurrencia en esa fila devuelve <c>false</c>; quien llama no confirma y el rollback deshace lo que se movió.
    /// </summary>
    Task<(UnresolvedForeignCityId Id, bool OccurrenceInserted)> UpsertWithOccurrenceAsync(
        UnresolvedForeignCity candidate, Guid tenantId, Guid orderId, CancellationToken cancellationToken);

    /// <summary>Con seguimiento: descartar, restaurar y confirmar cambian la fila y la guardan con
    /// <see cref="IGeographyUnitOfWork.SaveChangesAsync"/>.</summary>
    Task<UnresolvedForeignCity?> FindAsync(UnresolvedForeignCityId id, CancellationToken cancellationToken);

    Task RefreshAsync(UnresolvedForeignCity city, CancellationToken cancellationToken);

    /// <summary>
    /// Una página de filas en <paramref name="status"/>, por <c>last_seen_at</c> descendente y luego <c>id</c> descendente, con
    /// sus conteos. <paramref name="normalizedSearch"/> (ya de <see cref="CityText.Normalize"/>) se busca dentro del texto
    /// normalizado. <c>Total</c> cuenta todo el filtro.
    /// </summary>
    Task<(IReadOnlyList<UnresolvedForeignCityRow> Rows, int Total)> ListAsync(
        UnresolvedCityStatus status,
        string? countryCode,
        string? normalizedSearch,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>Todas las ocurrencias de la fila, sin seguimiento.</summary>
    Task<IReadOnlyList<UnresolvedForeignCityOccurrence>> ListOccurrencesAsync(
        UnresolvedForeignCityId id, CancellationToken cancellationToken);

    /// <summary>Las filas <c>Pending</c> por país (<c>country_code</c> en mayúscula).</summary>
    Task<IReadOnlyDictionary<string, int>> CountPendingByCountryAsync(CancellationToken cancellationToken);
}
