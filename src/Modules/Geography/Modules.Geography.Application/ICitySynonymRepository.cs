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
}
