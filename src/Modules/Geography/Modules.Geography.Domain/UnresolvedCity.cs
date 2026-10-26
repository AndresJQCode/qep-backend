namespace Modules.Geography.Domain;

public readonly record struct UnresolvedCityId(Guid Value)
{
    public static UnresolvedCityId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public enum UnresolvedCityStatus
{
    Pending,
    Resolved,
    Discarded,
}

/// <summary>
/// Un texto de ciudad que no emparejó con ninguna, por departamento, a la espera de que un operador le
/// asigne una ciudad (se vuelve sinónimo) o lo descarte. Sus ocurrencias (<see cref="UnresolvedCityOccurrence"/>)
/// las escribe SQL aparte.
/// </summary>
public sealed class UnresolvedCity
{
    /// <summary>Ancho de <c>sample_address</c>.</summary>
    public const int SampleAddressMaxLength = 600;

    /// <summary>Ancho de <c>province_code</c>.</summary>
    public const int ProvinceCodeMaxLength = 10;

    private UnresolvedCity()
    {
    }

    public UnresolvedCityId Id { get; private set; }

    public DepartmentId DepartmentId { get; private set; }

    public string NormalizedText { get; private set; } = string.Empty;

    public string CityText { get; private set; } = string.Empty;

    public string SampleAddress { get; private set; } = string.Empty;

    public string? ProvinceCode { get; private set; }

    public UnresolvedCityStatus Status { get; private set; }

    public CitySynonymId? SynonymId { get; private set; }

    public DateTimeOffset FirstSeenAt { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    /// <summary>Recorta el texto, la dirección y el código de provincia sin lanzar: el pedido ya entró y esto es sólo una
    /// huella.</summary>
    public static UnresolvedCity Create(
        UnresolvedCityId id,
        DepartmentId departmentId,
        string cityText,
        string sampleAddress,
        string? provinceCode,
        DateTimeOffset seenAt)
    {
        var normalized = Domain.CityText.Normalize(cityText);
        return new UnresolvedCity
        {
            Id = id,
            DepartmentId = departmentId,
            NormalizedText = Truncate(normalized, Domain.CityText.MaxLength),
            CityText = Truncate(cityText.Trim(), Domain.CityText.MaxLength),
            SampleAddress = Truncate(sampleAddress, SampleAddressMaxLength),
            ProvinceCode = provinceCode is null ? null : Truncate(provinceCode, ProvinceCodeMaxLength),
            Status = UnresolvedCityStatus.Pending,
            FirstSeenAt = seenAt,
            LastSeenAt = seenAt,
            UpdatedAt = seenAt,
            Version = 1,
        };
    }

    public void Resolve(CitySynonymId synonymId, DateTimeOffset now)
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

    // Recorta sin partir un par sustituto y sin dejar un espacio al final: NormalizedText es la llave única
    // por departamento, y "A " y "A" no pueden ser dos llaves.
    private static string Truncate(string value, int max)
    {
        if (value.Length <= max)
        {
            return value;
        }

        var length = char.IsHighSurrogate(value[max - 1]) ? max - 1 : max;
        return value[..length].TrimEnd();
    }

    private void EnsureStatus(UnresolvedCityStatus expected, string code, string message)
    {
        if (Status != expected)
        {
            throw new GeographyDomainException(code, message);
        }
    }
}
