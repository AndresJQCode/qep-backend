using Modules.Geography.Domain;

namespace Modules.Geography.Application;

/// <summary>Los sinónimos de ciudad (spec 2026-10-26 §5). Dato global, sin tenant. Las lecturas siguen la entidad: lo
/// que se cambia se guarda con <see cref="IGeographyUnitOfWork.SaveChangesAsync"/>.</summary>
public interface ICitySynonymRepository
{
    /// <summary>Por id, borrados incluidos: un sinónimo borrado conserva su historia.</summary>
    Task<CitySynonym?> FindAsync(CitySynonymId id, CancellationToken cancellationToken);

    /// <summary>El sinónimo vivo del texto en el departamento; <paramref name="normalizedText"/> ya viene de
    /// <see cref="CityText.Normalize"/>.</summary>
    Task<CitySynonym?> FindActiveAsync(DepartmentId departmentId, string normalizedText, CancellationToken cancellationToken);

    void Add(CitySynonym synonym);

    /// <summary>
    /// Una página de sinónimos vivos (los borrados no se listan), por <c>updated_at</c> descendente y luego <c>id</c> descendente.
    /// <paramref name="normalizedSearch"/> (ya de <see cref="CityText.Normalize"/>) se busca dentro del texto normalizado del
    /// sinónimo o del nombre de su ciudad (el que se muestra o el oficial). <c>Total</c> cuenta todo el filtro. Sin seguimiento.
    /// </summary>
    Task<(IReadOnlyList<CitySynonym> Items, int Total)> ListAsync(
        DepartmentId? departmentId,
        string? normalizedSearch,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}
