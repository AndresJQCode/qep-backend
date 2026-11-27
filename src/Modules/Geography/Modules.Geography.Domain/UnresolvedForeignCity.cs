namespace Modules.Geography.Domain;

public readonly record struct UnresolvedForeignCityId(Guid Value)
{
    public static UnresolvedForeignCityId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Un texto de ciudad de un país fuera de Colombia que no coincidió con la lista de Siigo, por país, a la espera de que un
/// operador le asigne una ciudad del catálogo (se vuelve <see cref="ForeignCitySynonym"/>) o lo descarte. Reusa
/// <see cref="UnresolvedCityStatus"/>. Sus ocurrencias (<see cref="UnresolvedForeignCityOccurrence"/>) las escribe SQL aparte.
/// </summary>
public sealed class UnresolvedForeignCity
{
    private UnresolvedForeignCity()
    {
    }

    public UnresolvedForeignCityId Id { get; private set; }

    /// <summary>ISO 3166-1 alfa-2 en mayúscula.</summary>
    public string CountryCode { get; private set; } = string.Empty;

    public string NormalizedText { get; private set; } = string.Empty;

    public string CityText { get; private set; } = string.Empty;

    public UnresolvedCityStatus Status { get; private set; }

    public ForeignCitySynonymId? SynonymId { get; private set; }

    public DateTimeOffset FirstSeenAt { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    /// <summary>La llave de una fila y de un sinónimo: el texto normalizado y recortado al ancho de la columna.</summary>
    public static string KeyFor(string cityText) =>
        Domain.CityText.Truncate(Domain.CityText.Normalize(cityText), Domain.CityText.MaxLength);

    /// <summary>Recorta sin lanzar: el pedido ya entró y esto es sólo una huella. Una llave vacía la descarta quien llama.</summary>
    public static UnresolvedForeignCity Create(
        UnresolvedForeignCityId id, string countryCode, string cityText, DateTimeOffset seenAt) =>
        new()
        {
            Id = id,
            CountryCode = countryCode.Trim().ToUpperInvariant(),
            NormalizedText = KeyFor(cityText),
            CityText = Domain.CityText.Truncate(cityText.Trim(), Domain.CityText.MaxLength),
            Status = UnresolvedCityStatus.Pending,
            FirstSeenAt = seenAt,
            LastSeenAt = seenAt,
            UpdatedAt = seenAt,
            Version = 1,
        };

    public void Resolve(ForeignCitySynonymId synonymId, DateTimeOffset now)
    {
        EnsureStatus(UnresolvedCityStatus.Pending, "geography.unresolved.not_pending", "The city is not pending.");
        Status = UnresolvedCityStatus.Resolved;
        SynonymId = synonymId;
        Touch(now);
    }

    public void Discard(DateTimeOffset now)
    {
        EnsureStatus(UnresolvedCityStatus.Pending, "geography.unresolved.not_pending", "The city is not pending.");
        Status = UnresolvedCityStatus.Discarded;
        Touch(now);
    }

    public void Restore(DateTimeOffset now)
    {
        EnsureStatus(UnresolvedCityStatus.Discarded, "geography.unresolved.not_discarded", "The city is not discarded.");
        Status = UnresolvedCityStatus.Pending;
        Touch(now);
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }

    private void EnsureStatus(UnresolvedCityStatus expected, string code, string message)
    {
        if (Status != expected)
        {
            throw new GeographyDomainException(code, message);
        }
    }
}
