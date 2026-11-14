namespace Modules.Shopify.Domain;

/// <summary>
/// Spec 2026-11-13: la variante de Catalog que una tienda asocia al nombre exacto de una línea personalizada de Shopify (la que
/// el comercio escribe a mano, sin producto). Una fila por <c>(connection_id, title)</c>: el índice único lo cierra, y el título
/// se guarda y se compara recortado, ordinal (sensible a mayúsculas y emojis, sin otra normalización). La variante es una
/// referencia blanda a <c>catalog</c> (sin FK): el módulo no ve Catalog, y una variante borrada deja la fila a la vista para que
/// se pueda quitar.
/// </summary>
public sealed class ShopifyCustomLineMapping
{
    public const int TitleMaxLength = 255;

    private ShopifyCustomLineMapping()
    {
        Title = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConnectionId { get; private set; }

    /// <summary>Recortado.</summary>
    public string Title { get; private set; }

    public Guid VariantId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>El id de la membresía activa de quien la creó, como <c>stores.updated_by</c>.</summary>
    public Guid CreatedBy { get; private set; }

    /// <summary>Recorta; título vacío, de más de <see cref="TitleMaxLength"/> o un id en <see cref="Guid.Empty"/> lanzan
    /// <see cref="ArgumentException"/>: el validador del caso de uso los frena antes.</summary>
    public static ShopifyCustomLineMapping Create(
        Guid tenantId, Guid connectionId, string title, Guid variantId, Guid createdBy, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(tenantId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(connectionId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(variantId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(createdBy, Guid.Empty);

        var normalized = NormalizeTitle(title)
            ?? throw new ArgumentException("The custom line title is required.", nameof(title));
        if (normalized.Length > TitleMaxLength)
        {
            throw new ArgumentException($"The custom line title cannot exceed {TitleMaxLength} characters.", nameof(title));
        }

        return new ShopifyCustomLineMapping
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            Title = normalized,
            VariantId = variantId,
            CreatedAt = now,
            CreatedBy = createdBy,
        };
    }

    /// <summary><c>Trim</c>; <c>null</c> o en blanco → <c>null</c>. La única regla de comparación: la usan el dominio, el
    /// validador y el importador.</summary>
    public static string? NormalizeTitle(string? title)
    {
        var trimmed = title?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
