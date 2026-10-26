using Modules.Geography.Domain;

namespace Modules.Geography.Application;

/// <summary>
/// Guarda los sinónimos, la bandeja de ciudades sin resolver y su auditoría en una sola transacción. La implementación
/// traduce los índices únicos por nombre y la concurrencia, en Infrastructure (Application no referencia EF Core ni
/// Npgsql).
/// </summary>
public interface IGeographyUnitOfWork
{
    /// <summary>
    /// <c>DbUpdateConcurrencyException</c> → <c>RequestConcurrencyException("concurrency.conflict")</c> (412);
    /// <c>IX_city_synonyms_department_text_active</c> → <c>ResourceConflictException("geography.synonym.already_exists")</c> (409).
    /// </summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>Abre una transacción explícita sobre la conexión del módulo; <see cref="SaveChangesAsync"/> se suma a ella
    /// sin abrir otra. Hace falta para <see cref="LockCityTextAsync"/>.</summary>
    Task<IGeographyTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// <c>pg_advisory_xact_lock(hashtextextended(department_id || ':' || normalized_text, 0))</c>. Exige una transacción
    /// abierta con <see cref="BeginTransactionAsync"/>: el candado se suelta con ella. Serializa reportar un texto sin
    /// resolver y confirmarlo como sinónimo (SC-03, SC-11), que si no podrían cruzarse y dejar una ocurrencia nueva en una
    /// fila ya resuelta.
    /// </summary>
    Task LockCityTextAsync(DepartmentId departmentId, string normalizedText, CancellationToken cancellationToken);
}

/// <summary>Se confirma con <see cref="CommitAsync"/>; disponerla sin commit hace rollback, que además suelta los candados
/// que haya tomado.</summary>
public interface IGeographyTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}
