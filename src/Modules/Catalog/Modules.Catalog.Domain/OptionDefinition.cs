namespace Modules.Catalog.Domain;

/// <summary>
/// Una opción de la biblioteca de un tenant (por ejemplo «Talla» con «S», «M», «L»), que los
/// productos reutilizan para armar sus variantes. Los valores son únicos dentro de la opción,
/// sin distinguir mayúsculas ni espacios en los bordes.
/// </summary>
public sealed class OptionDefinition
{
    public const int NameMaxLength = 60;
    public const int ValueMaxLength = 60;

    private readonly List<OptionValue> _values = [];

    // EF Core materializa por acá; Create es el único punto de entrada del código.
    private OptionDefinition() => Name = string.Empty;

    public OptionDefinitionId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Token de concurrencia optimista, como en los demás agregados de Catalog.</summary>
    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Valores ordenados por posición.</summary>
    public IReadOnlyList<OptionValue> Values => _values;

    public static OptionDefinition Create(
        Guid tenantId,
        string name,
        IReadOnlyList<string> values,
        bool isActive,
        DateTimeOffset at)
    {
        var option = new OptionDefinition
        {
            Id = OptionDefinitionId.New(),
            TenantId = tenantId,
            Name = NormalizeName(name),
            IsActive = isActive,
            Version = 1,
            CreatedAt = at,
            UpdatedAt = at,
        };

        foreach (var value in values)
        {
            option.AddValue(value);
        }

        return option;
    }

    public void Rename(string name)
    {
        Name = NormalizeName(name);
        Version++;
    }

    public void SetActive(bool isActive)
    {
        IsActive = isActive;
        Version++;
    }

    public void AddValue(string value)
    {
        var normalized = NormalizeValue(value);
        EnsureNotDuplicate(normalized, exceptValueId: null);

        _values.Add(new OptionValue(Guid.CreateVersion7(), normalized, _values.Count));
        Version++;
    }

    public void RenameValue(Guid valueId, string value)
    {
        var target = Find(valueId);
        var normalized = NormalizeValue(value);
        EnsureNotDuplicate(normalized, exceptValueId: valueId);

        target.Value = normalized;
        Version++;
    }

    public void SetValueActive(Guid valueId, bool isActive)
    {
        Find(valueId).IsActive = isActive;
        Version++;
    }

    private OptionValue Find(Guid valueId) =>
        _values.Find(v => v.Id == valueId)
        ?? throw new CatalogDomainException(
            "catalog.option.value_not_found",
            "The option value does not exist.");

    private void EnsureNotDuplicate(string normalized, Guid? exceptValueId)
    {
        if (_values.Any(v => v.Id != exceptValueId
            && string.Equals(v.Value, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            throw new CatalogDomainException(
                "catalog.option.value_duplicate",
                "The option already has that value.");
        }
    }

    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new CatalogDomainException(
                "catalog.option.name_required",
                "The option name is required.");
        }

        var trimmed = name.Trim();
        return trimmed.Length > NameMaxLength
            ? throw new CatalogDomainException(
                "catalog.option.name_too_long",
                $"The option name cannot exceed {NameMaxLength} characters.")
            : trimmed;
    }

    private static string NormalizeValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CatalogDomainException(
                "catalog.option.value_required",
                "The option value is required.");
        }

        var trimmed = value.Trim();
        return trimmed.Length > ValueMaxLength
            ? throw new CatalogDomainException(
                "catalog.option.value_too_long",
                $"The option value cannot exceed {ValueMaxLength} characters.")
            : trimmed;
    }
}
