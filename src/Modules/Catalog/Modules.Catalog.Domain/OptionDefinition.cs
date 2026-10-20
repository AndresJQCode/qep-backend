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

    /// <summary>
    /// Los ids de los valores guardados que <paramref name="values"/> deja afuera: los que
    /// <see cref="ReplaceValues"/> borraría. El handler pregunta antes si alguno está en uso.
    /// </summary>
    public IReadOnlyList<Guid> RemovedValueIds(IReadOnlyList<OptionValueInput> values)
    {
        var kept = values.Where(v => v.Id is not null).Select(v => v.Id!.Value).ToHashSet();
        return _values.Where(v => !kept.Contains(v.Id)).Select(v => v.Id).ToArray();
    }

    /// <summary>
    /// Reemplaza la lista de valores entera, en el orden del cuerpo (el PUT de la biblioteca): un
    /// valor con id existente es ése —se renombra, se activa o desactiva y cambia de posición—,
    /// uno sin id es nuevo, y uno guardado que no viene se quita. La unicidad se comprueba sobre la
    /// lista final, así que intercambiar dos nombres en el mismo guardado vale.
    /// </summary>
    public void ReplaceValues(IReadOnlyList<OptionValueInput> values)
    {
        var normalized = values.Select(v => NormalizeValue(v.Value)).ToArray();
        for (var i = 0; i < normalized.Length; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (string.Equals(normalized[i], normalized[j], StringComparison.OrdinalIgnoreCase))
                {
                    throw new CatalogDomainException(
                        "catalog.option.value_duplicate",
                        "The option already has that value.");
                }
            }
        }

        // Todo se comprueba antes de tocar un valor: un error a mitad de camino no deja el
        // agregado a medias. Un id repetido en el cuerpo no corresponde a una segunda fila.
        var seen = new HashSet<Guid>();
        foreach (var id in values.Where(v => v.Id is not null).Select(v => v.Id!.Value))
        {
            if (!seen.Add(id) || _values.TrueForAll(v => v.Id != id))
            {
                throw ValueNotFound();
            }
        }

        var replacement = new List<OptionValue>(values.Count);
        for (var position = 0; position < values.Count; position++)
        {
            var input = values[position];
            OptionValue value;
            if (input.Id is { } id)
            {
                value = Find(id);
                value.Value = normalized[position];
                value.Position = position;
            }
            else
            {
                value = new OptionValue(Guid.CreateVersion7(), normalized[position], position);
            }

            value.IsActive = input.IsActive;
            replacement.Add(value);
        }

        _values.Clear();
        _values.AddRange(replacement);
        Version++;
    }

    private OptionValue Find(Guid valueId) =>
        _values.Find(v => v.Id == valueId) ?? throw ValueNotFound();

    private static CatalogDomainException ValueNotFound() =>
        new("catalog.option.value_not_found", "The option value does not exist.");

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

/// <summary>Un valor tal como lo manda el PUT de la biblioteca: con id, uno guardado; sin id, nuevo.</summary>
public sealed record OptionValueInput(Guid? Id, string Value, bool IsActive);
