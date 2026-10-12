namespace Modules.Customers.Domain;

public readonly record struct TagId(Guid Value)
{
    public static TagId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>La paleta fija de etiquetas (spec 2026-10-11 §4.1). Viaja por nombre en minúscula; el frontend la pinta.</summary>
public enum TagColor
{
    Gray,
    Red,
    Orange,
    Yellow,
    Green,
    Teal,
    Blue,
    Purple,
}

public static class TagColorParser
{
    public static string ToWireValue(this TagColor color) => color switch
    {
        TagColor.Gray => "gray",
        TagColor.Red => "red",
        TagColor.Orange => "orange",
        TagColor.Yellow => "yellow",
        TagColor.Green => "green",
        TagColor.Teal => "teal",
        TagColor.Blue => "blue",
        TagColor.Purple => "purple",
        _ => throw Invalid(),
    };

    /// <summary>Un color desconocido <b>falla</b>: caer en un default le cambiaría el color a la etiqueta sin que
    /// nadie se entere, mismo criterio que <see cref="IdentificationTypeParser"/>. Los números tampoco valen:
    /// <c>Enum.TryParse</c> aceptaría «7» como <see cref="TagColor.Purple"/>.</summary>
    public static TagColor Parse(string? value) =>
        TryParse(value, out var color) ? color : throw Invalid();

    public static bool TryParse(string? value, out TagColor color)
    {
        foreach (var candidate in Enum.GetValues<TagColor>())
        {
            if (string.Equals(candidate.ToWireValue(), value?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                color = candidate;
                return true;
            }
        }

        color = default;
        return false;
    }

    private static CustomersDomainException Invalid() => new(
        "customers.tag.color_invalid", "The tag color is not one of the supported values.");
}

/// <summary>
/// Spec 2026-10-11 §4.1: una etiqueta del catálogo de un tenant — nombre y color. Se crea en línea al etiquetar;
/// renombrar, recolorear y borrar es de quien tenga <c>customers.tag.manage</c>.
///
/// La unicidad del nombre (sin distinguir mayúsculas) no la arbitra esta clase sino
/// <c>IX_tags_tenant_name</c>, y <c>CustomersUnitOfWork</c> la traduce a <c>customers.tag.name_taken</c>.
/// </summary>
public sealed class Tag
{
    public const int NameMaxLength = 40;

    /// <summary>El tope de etiquetas por tenant. Contarlas es del caso de uso; acá vive el número y su error.</summary>
    public const int MaxPerTenant = 100;

    // EF Core materializa por acá; Create es el único punto de entrada.
    private Tag()
    {
        Name = string.Empty;
    }

    private Tag(TagId id, Guid tenantId, string name, TagColor color, DateTimeOffset occurredAt)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
        Color = color;
        Version = 1;
        CreatedAt = occurredAt;
        UpdatedAt = occurredAt;
    }

    public TagId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    public TagColor Color { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Tag Create(TagId id, Guid tenantId, string name, TagColor color, DateTimeOffset occurredAt) =>
        new(id, tenantId, NormalizeName(name), EnsureDefined(color), occurredAt);

    public void Update(string name, TagColor color, DateTimeOffset occurredAt)
    {
        // Todo se valida antes de asignar: un 422 deja la etiqueta como estaba.
        var normalized = NormalizeName(name);
        var validColor = EnsureDefined(color);

        Name = normalized;
        Color = validColor;
        Version++;
        UpdatedAt = occurredAt;
    }

    /// <summary>La que se intenta crear sería la <c>MaxPerTenant + 1</c>.</summary>
    public static void EnsureTenantCapacity(int existingCount)
    {
        if (existingCount >= MaxPerTenant)
        {
            throw new CustomersDomainException(
                "customers.tag.limit_reached", $"A tenant cannot have more than {MaxPerTenant} tags.");
        }
    }

    private static TagColor EnsureDefined(TagColor color) =>
        Enum.IsDefined(color) ? color : throw new CustomersDomainException(
            "customers.tag.color_invalid", "The tag color is not one of the supported values.");

    // Recortar es parte del invariante: el índice único trata « VIP» y «VIP» como el mismo nombre sólo si el
    // valor guardado ya viene recortado.
    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new CustomersDomainException("customers.tag.name_required", "The tag name is required.");
        }

        var trimmed = name.Trim();
        return trimmed.Length > NameMaxLength
            ? throw new CustomersDomainException(
                "customers.tag.name_too_long", $"The tag name cannot exceed {NameMaxLength} characters.")
            : trimmed;
    }
}

/// <summary>
/// Spec 2026-10-11 §4.1: la etiqueta puesta a un cliente. La tabla tiene clave <c>(customer_id, tag_id)</c>; el
/// conjunto de un cliente se reemplaza entero (<c>PUT /customers/{id}/tags</c>), así que las reglas del
/// conjunto viven acá.
/// </summary>
public sealed class CustomerTagAssignment
{
    public const int MaxPerCustomer = 20;

    private CustomerTagAssignment()
    {
    }

    private CustomerTagAssignment(
        CustomerId customerId, TagId tagId, Guid tenantId, Guid assignedByUserId, DateTimeOffset assignedAt)
    {
        CustomerId = customerId;
        TagId = tagId;
        TenantId = tenantId;
        AssignedByUserId = assignedByUserId;
        AssignedAt = assignedAt;
    }

    public CustomerId CustomerId { get; private set; }

    public TagId TagId { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid AssignedByUserId { get; private set; }

    public DateTimeOffset AssignedAt { get; private set; }

    public static CustomerTagAssignment Create(
        CustomerId customerId, TagId tagId, Guid tenantId, Guid assignedByUserId, DateTimeOffset assignedAt) =>
        new(customerId, tagId, tenantId, assignedByUserId, assignedAt);

    /// <summary>El conjunto que quedaría: a lo sumo <see cref="MaxPerCustomer"/> y sin repetidas.</summary>
    public static void EnsureValidSelection(IReadOnlyCollection<TagId> tagIds)
    {
        ArgumentNullException.ThrowIfNull(tagIds);
        if (tagIds.Distinct().Count() != tagIds.Count)
        {
            throw new CustomersDomainException(
                "customers.customer.tag_duplicated", "A tag cannot be assigned twice to the same customer.");
        }

        if (tagIds.Count > MaxPerCustomer)
        {
            throw new CustomersDomainException(
                "customers.customer.tag_limit_reached", $"A customer cannot have more than {MaxPerCustomer} tags.");
        }
    }
}
