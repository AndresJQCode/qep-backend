namespace Modules.Wms.Domain.Warehousing;

/// <summary>El dueño de la mercancía (RF-005). Un <c>Self</c> por tenant, que solo escribe el provisioner (P-2).</summary>
public sealed class Owner
{
    public const int NameMaxLength = 120;
    public const int IdentificationMaxLength = 40;

    private Owner()
    {
    }

    public OwnerId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public OwnerKind Kind { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Identification { get; private set; }

    public string? ContactEmail { get; private set; }

    public bool IsActive { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Owner CreateSelf(OwnerId id, Guid tenantId, string name, DateTimeOffset now) =>
        Build(id, tenantId, OwnerKind.Self, name, null, null, now);

    public static Owner CreateClient(OwnerId id, Guid tenantId, string name, string? identification, string? contactEmail, DateTimeOffset now) =>
        Build(id, tenantId, OwnerKind.Client, name, identification, contactEmail, now);

    public void Update(string name, string? identification, string? contactEmail, bool isActive, DateTimeOffset now)
    {
        // El Self es el dueño por defecto de todo el stock propio: desactivarlo dejaría sin dueño el alta de cualquier saldo.
        if (Kind == OwnerKind.Self && !isActive)
        {
            throw new WmsDomainException(WmsErrorCodes.OwnerSelfImmutable, "The self owner cannot be deactivated.");
        }

        Name = NormalizeName(name);
        Identification = NormalizeOptional(identification, IdentificationMaxLength);
        ContactEmail = NormalizeOptional(contactEmail, null);
        IsActive = isActive;
        Version++;
        UpdatedAt = now;
    }

    private static Owner Build(OwnerId id, Guid tenantId, OwnerKind kind, string name, string? identification, string? contactEmail, DateTimeOffset now) =>
        new()
        {
            Id = id,
            TenantId = tenantId,
            Kind = kind,
            Name = NormalizeName(name),
            Identification = NormalizeOptional(identification, IdentificationMaxLength),
            ContactEmail = NormalizeOptional(contactEmail, null),
            IsActive = true,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };

    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(trimmed.Length, NameMaxLength);
        return trimmed;
    }

    private static string? NormalizeOptional(string? value, int? maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (maxLength is { } max)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(trimmed.Length, max);
        }

        return trimmed;
    }
}
