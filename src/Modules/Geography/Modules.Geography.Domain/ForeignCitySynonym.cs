namespace Modules.Geography.Domain;

public readonly record struct ForeignCitySynonymId(Guid Value)
{
    public static ForeignCitySynonymId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Un texto que la gente escribe fuera de Colombia ("Miamy") y la ciudad de la lista de Siigo a la que equivale, por país.
/// Dato de referencia global, sin tenant. Borrado lógico: un sinónimo borrado ya no empareja pero conserva su historia. Mismo
/// patrón que <see cref="CitySynonym"/>.
/// </summary>
public sealed class ForeignCitySynonym
{
    private ForeignCitySynonym()
    {
    }

    public ForeignCitySynonymId Id { get; private set; }

    public string CountryCode { get; private set; } = string.Empty;

    public string NormalizedText { get; private set; } = string.Empty;

    public string DisplayText { get; private set; } = string.Empty;

    public BillingCityId BillingCityId { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid UpdatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public Guid? DeletedBy { get; private set; }

    public long Version { get; private set; }

    public bool IsDeleted => DeletedAt is not null;

    /// <summary>
    /// Confirmar una fila de la bandeja: la llave es el <see cref="UnresolvedForeignCity.NormalizedText"/> de la fila, tal cual,
    /// y el texto visible su <see cref="UnresolvedForeignCity.CityText"/>. No se vuelve a normalizar: en un texto largo los
    /// dos ya vienen recortados, y normalizar el visible daría otra llave que la que el reporte busca.
    /// </summary>
    public static ForeignCitySynonym Create(
        ForeignCitySynonymId id,
        UnresolvedForeignCity row,
        BillingCityId billingCityId,
        Guid createdBy,
        DateTimeOffset now)
    {
        if (row.NormalizedText.Length == 0)
        {
            throw new GeographyDomainException(
                "geography.synonym.text_required",
                "The synonym text is required.");
        }

        return new ForeignCitySynonym
        {
            Id = id,
            CountryCode = row.CountryCode,
            NormalizedText = row.NormalizedText,
            DisplayText = row.CityText,
            BillingCityId = billingCityId,
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedBy = createdBy,
            UpdatedAt = now,
            Version = 1,
        };
    }

    /// <summary>Apunta el sinónimo a otra ciudad del catálogo y devuelve la anterior.</summary>
    public BillingCityId ChangeCity(BillingCityId billingCityId, Guid by, DateTimeOffset now)
    {
        EnsureNotDeleted();
        if (billingCityId == BillingCityId)
        {
            throw new GeographyDomainException(
                "geography.synonym.same_city",
                "The synonym already points to that city.");
        }

        var previous = BillingCityId;
        BillingCityId = billingCityId;
        Touch(by, now);
        return previous;
    }

    public void Delete(Guid by, DateTimeOffset now)
    {
        EnsureNotDeleted();
        DeletedAt = now;
        DeletedBy = by;
        Touch(by, now);
    }

    private void EnsureNotDeleted()
    {
        if (IsDeleted)
        {
            throw new GeographyDomainException(
                "geography.synonym.deleted",
                "The synonym was deleted.");
        }
    }

    private void Touch(Guid by, DateTimeOffset now)
    {
        UpdatedBy = by;
        UpdatedAt = now;
        Version++;
    }
}
