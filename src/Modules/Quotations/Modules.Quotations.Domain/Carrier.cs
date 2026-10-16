namespace Modules.Quotations.Domain;

/// <summary>Una transportadora del tenant, con los alias externos que la identifican en otros
/// sistemas.</summary>
public sealed class Carrier
{
    private readonly List<CarrierNameEntry> _names = [];

    // EF Core materializa por acá; Create es el único punto de entrada.
    private Carrier() => Name = string.Empty;

    private Carrier(
        CarrierId id,
        Guid tenantId,
        string name,
        IReadOnlyCollection<string> externalNames,
        bool supportsCashOnDelivery,
        int sortOrder,
        DateTimeOffset occurredAt)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
        ExternalNames = externalNames.ToArray();
        SupportsCashOnDelivery = supportsCashOnDelivery;
        SortOrder = sortOrder;
        IsActive = true;
        Version = 1;
        CreatedAt = occurredAt;
        UpdatedAt = occurredAt;
        RebuildNames();
    }

    public CarrierId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    public IReadOnlyList<string> ExternalNames { get; private set; } = [];

    public bool SupportsCashOnDelivery { get; private set; }

    public int SortOrder { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Token de concurrencia optimista; sube en cada mutación.</summary>
    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>El nombre y los externos, normalizados, tal como se guardan.</summary>
    public IReadOnlyCollection<CarrierNameEntry> Names => _names;

    public static Carrier Create(
        CarrierId id,
        Guid tenantId,
        string name,
        IReadOnlyCollection<string> externalNames,
        bool supportsCashOnDelivery,
        int sortOrder,
        DateTimeOffset occurredAt)
    {
        var (cleanName, cleanExternals) = Validate(name, externalNames);
        return new(id, tenantId, cleanName, cleanExternals, supportsCashOnDelivery, sortOrder, occurredAt);
    }

    public void Update(
        string name,
        IReadOnlyCollection<string> externalNames,
        bool supportsCashOnDelivery,
        DateTimeOffset occurredAt)
    {
        var (cleanName, cleanExternals) = Validate(name, externalNames);
        Name = cleanName;
        ExternalNames = cleanExternals;
        SupportsCashOnDelivery = supportsCashOnDelivery;
        RebuildNames();
        Touch(occurredAt);
    }

    public void Activate(DateTimeOffset occurredAt)
    {
        IsActive = true;
        Touch(occurredAt);
    }

    public void Deactivate(DateTimeOffset occurredAt)
    {
        IsActive = false;
        Touch(occurredAt);
    }

    public void MoveTo(int sortOrder, DateTimeOffset occurredAt)
    {
        SortOrder = sortOrder;
        Touch(occurredAt);
    }

    private void Touch(DateTimeOffset occurredAt)
    {
        Version++;
        UpdatedAt = occurredAt;
    }

    private void RebuildNames()
    {
        _names.Clear();
        _names.Add(new CarrierNameEntry(TenantId, CarrierNameKind.Name, Name));
        _names.AddRange(ExternalNames.Select(e => new CarrierNameEntry(TenantId, CarrierNameKind.External, e)));
    }

    private static (string Name, string[] Externals) Validate(
        string name,
        IReadOnlyCollection<string> externalNames)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new QuotationsDomainException(
                "quotations.carrier.name_required",
                "El nombre de la transportadora es obligatorio.");
        }

        var cleanName = name.Trim();
        if (cleanName.Length > CarrierName.MaxLength)
        {
            throw new QuotationsDomainException(
                "quotations.carrier.name_too_long",
                $"El nombre de la transportadora no puede superar {CarrierName.MaxLength} caracteres.");
        }

        if (externalNames.Count > CarrierName.MaxExternalNames)
        {
            throw new QuotationsDomainException(
                "quotations.carrier.too_many_external_names",
                $"Una transportadora admite máximo {CarrierName.MaxExternalNames} nombres externos.");
        }

        var seen = new HashSet<string> { CarrierName.Normalize(cleanName) };
        var cleanExternals = new List<string>(externalNames.Count);
        foreach (var external in externalNames)
        {
            var trimmed = (external ?? string.Empty).Trim();
            if (trimmed.Length > CarrierName.ExternalMaxLength)
            {
                throw new QuotationsDomainException(
                    "quotations.carrier.external_name_too_long",
                    $"Un nombre externo no puede superar {CarrierName.ExternalMaxLength} caracteres.");
            }

            if (trimmed.Length == 0 || !seen.Add(CarrierName.Normalize(trimmed)))
            {
                throw new QuotationsDomainException(
                    "quotations.carrier.external_name_duplicated",
                    "Los nombres externos no pueden estar vacíos ni repetirse, ni ser iguales al nombre de la transportadora.");
            }

            cleanExternals.Add(trimmed);
        }

        return (cleanName, cleanExternals.ToArray());
    }
}
