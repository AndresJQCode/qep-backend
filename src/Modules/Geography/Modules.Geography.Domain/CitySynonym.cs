namespace Modules.Geography.Domain;

public readonly record struct CitySynonymId(Guid Value)
{
    public static CitySynonymId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Un texto que la gente escribe ("Calli") y la ciudad de un departamento a la que equivale. Dato de
/// referencia global, sin tenant. Borrado lógico: un sinónimo borrado ya no empareja pero conserva su
/// historia.
/// </summary>
public sealed class CitySynonym
{
    private CitySynonym()
    {
    }

    public CitySynonymId Id { get; private set; }

    public DepartmentId DepartmentId { get; private set; }

    public string NormalizedText { get; private set; } = string.Empty;

    public string DisplayText { get; private set; } = string.Empty;

    public CityId CityId { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid UpdatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public Guid? DeletedBy { get; private set; }

    public long Version { get; private set; }

    public bool IsDeleted => DeletedAt is not null;

    /// <summary>
    /// Normaliza <paramref name="displayText"/> y rechaza lo que no cabe en <see cref="CityText.MaxLength"/>
    /// en vez de recortarlo: un texto recortado y vuelto a normalizar sería otro texto.
    /// </summary>
    public static CitySynonym Create(
        CitySynonymId id,
        DepartmentId departmentId,
        string displayText,
        CityId cityId,
        Guid createdBy,
        DateTimeOffset now)
    {
        return New(id, departmentId, CityText.Normalize(displayText), displayText.Trim(), cityId, createdBy, now);
    }

    /// <summary>
    /// Confirmar una fila de la bandeja (SC-11): la llave es el <see cref="UnresolvedCity.NormalizedText"/> de la fila, tal
    /// cual, y el texto visible su <see cref="UnresolvedCity.CityText"/>. No se vuelve a normalizar: en un texto de más de
    /// <see cref="CityText.MaxLength"/> los dos ya vienen recortados, y normalizar el visible recortado daría otra llave que la
    /// que el reporte busca (preflight F17, ruling P17).
    /// </summary>
    public static CitySynonym Create(
        CitySynonymId id,
        UnresolvedCity row,
        CityId cityId,
        Guid createdBy,
        DateTimeOffset now) =>
        New(id, row.DepartmentId, row.NormalizedText, row.CityText, cityId, createdBy, now);

    private static CitySynonym New(
        CitySynonymId id,
        DepartmentId departmentId,
        string normalized,
        string display,
        CityId cityId,
        Guid createdBy,
        DateTimeOffset now)
    {
        if (normalized.Length == 0)
        {
            throw new GeographyDomainException(
                "geography.synonym.text_required",
                "The synonym text is required.");
        }

        if (normalized.Length > CityText.MaxLength || display.Length > CityText.MaxLength)
        {
            throw new GeographyDomainException(
                "geography.synonym.text_too_long",
                $"The synonym text cannot exceed {CityText.MaxLength} characters.");
        }

        return new CitySynonym
        {
            Id = id,
            DepartmentId = departmentId,
            NormalizedText = normalized,
            DisplayText = display,
            CityId = cityId,
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedBy = createdBy,
            UpdatedAt = now,
            Version = 1,
        };
    }

    /// <summary>Apunta el sinónimo a otra ciudad y devuelve la anterior.</summary>
    public CityId ChangeCity(CityId cityId, Guid by, DateTimeOffset now)
    {
        EnsureNotDeleted();
        if (cityId == CityId)
        {
            throw new GeographyDomainException(
                "geography.synonym.same_city",
                "The synonym already points to that city.");
        }

        var previous = CityId;
        CityId = cityId;
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
