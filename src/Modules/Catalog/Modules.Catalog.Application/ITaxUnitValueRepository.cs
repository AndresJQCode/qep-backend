using Modules.Catalog.Domain;

namespace Modules.Catalog.Application;

// Sin tenantId: el valor de la unidad tributaria es un dato de la plataforma.
public interface ITaxUnitValueRepository
{
    /// <summary>Los valores cargados, del año más reciente al más antiguo; todos los países si <paramref name="country"/> es null.</summary>
    Task<IReadOnlyList<TaxUnitValue>> ListAsync(string? country, CancellationToken cancellationToken);

    /// <summary>Con tracking: quien lo halla lo reemplaza y la unidad de trabajo lo persiste.</summary>
    Task<TaxUnitValue?> FindAsync(string country, int year, CancellationToken cancellationToken);

    Task<bool> AnyAsync(string country, CancellationToken cancellationToken);

    void Add(TaxUnitValue value);
}
