using Modules.Geography.Domain;

namespace Modules.Geography.Application;

/// <summary>Los sinónimos del exterior. Con seguimiento: lo que se lee acá se cambia y se guarda con
/// <see cref="IGeographyUnitOfWork.SaveChangesAsync"/>.</summary>
public interface IForeignCitySynonymRepository
{
    /// <summary>Incluye los borrados.</summary>
    Task<ForeignCitySynonym?> FindAsync(ForeignCitySynonymId id, CancellationToken cancellationToken);

    /// <summary>El sinónimo vivo de <paramref name="countryCode"/> y <paramref name="normalizedText"/>, si lo hay.</summary>
    Task<ForeignCitySynonym?> FindActiveAsync(
        string countryCode, string normalizedText, CancellationToken cancellationToken);

    void Add(ForeignCitySynonym synonym);

    /// <summary>Vuelve a leer de la base un sinónimo ya seguido.</summary>
    Task RefreshAsync(ForeignCitySynonym synonym, CancellationToken cancellationToken);

    /// <summary>
    /// Una página de sinónimos vivos, por <c>updated_at</c> descendente y luego <c>id</c> descendente. La búsqueda
    /// (<paramref name="normalizedSearch"/> ya de <see cref="CityText.Normalize"/>) mira el texto normalizado del sinónimo o el
    /// <c>normalized_name</c> de su ciudad. <c>Total</c> cuenta todo el filtro.
    /// </summary>
    Task<(IReadOnlyList<ForeignCitySynonym> Items, int Total)> ListAsync(
        string? countryCode, string? normalizedSearch, int page, int pageSize, CancellationToken cancellationToken);
}
