namespace Modules.Catalog.Domain;

/// <summary>
/// Un producto del catálogo de un tenant (RF-020). Guarda sólo los datos maestros vivos: las
/// listas de precio y su vigencia son de `pricing`, y un documento congela su copia de lo vendido.
/// </summary>
public sealed class Product
{
    // Espeja los anchos de columna de catalog.products. Guardar acá significa que un valor
    // demasiado largo falla como 422 con código de dominio en vez de llegar a PostgreSQL y
    // volver como 500 server.unexpected.
    public const int NameMaxLength = 200;
    public const int CodeMaxLength = 60;

    // EF Core materializa por acá. El código nunca construye el agregado así:
    // Create es el único punto de entrada, y es el que hace cumplir los invariantes.
    private Product()
    {
        Name = string.Empty;
        Code = string.Empty;
    }

    private Product(
        ProductId id,
        Guid tenantId,
        string name,
        string code,
        ProductDetails details,
        DateTimeOffset occurredAt)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
        Code = code;
        Apply(details);
        IsActive = true;
        Version = 1;
        CreatedAt = occurredAt;
        UpdatedAt = occurredAt;
    }

    public ProductId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    /// <summary>Único por tenant; la unicidad vive en IX_products_tenant_code.</summary>
    public string Code { get; private set; }

    public bool IsActive { get; private set; }

    // --- CAT-04: propiedades opcionales. Los cinco nacen nullable porque hay productos ya
    // cargados: una columna NOT NULL sin default los rompe, y un default inventado sería peor
    // —un precio 0 es un dato falso que se ve igual que uno real.

    public string? Description { get; private set; }

    /// <summary>
    /// Cuál de los archivos del producto es su imagen principal. **Referencia blanda, sin FK.**
    ///
    /// `Storage` ya modela la propiedad al revés —`CreateFileRequest` lleva `OwnerId` y
    /// `OwnerType`— pero eso responde *qué archivos pertenecen a este producto*, que pueden ser
    /// varios. *Cuál es la portada* es dato del catálogo, no del almacenamiento.
    ///
    /// Sin foreign key a propósito: `catalog` no puede referenciar `storage.file_resources` sin
    /// romper "ningún módulo lee las tablas de otro".
    /// </summary>
    public Guid? ImageFileId { get; private set; }

    /// <summary>
    /// Precio **base** de lista. `pricing` lo sobrescribe cuando exista
    /// (`DECISIÓN-PENDIENTE-CAT-06`); éste es el fallback cuando ninguna lista resuelve.
    /// </summary>
    public decimal? Price { get; private set; }

    /// <summary>Código ISO-4217 de tres letras, en mayúsculas. Va con <see cref="Price"/>.</summary>
    public string? Currency { get; private set; }

    /// <summary>
    /// Tasa de impuesto del producto. La FK de base garantiza que la fila exista, **pero no que
    /// sea del mismo tenant**: eso lo verifica el handler antes de asignarla.
    /// </summary>
    public TaxRateId? TaxRateId { get; private set; }

    /// <summary>
    /// Token de concurrencia optimista, como en <c>Tenant</c> y <c>Membership</c>. Cada mutación
    /// lo incrementa, y la infraestructura lo mapea con <c>IsConcurrencyToken()</c>, de modo que
    /// el <c>UPDATE</c> lleve la versión leída en su <c>WHERE</c>.
    ///
    /// Sin él, dos escrituras que se solapan se pisaban en silencio: la segunda no sólo perdía
    /// la primera, sino que podía dejar el producto editado **después** de inactivarse, porque
    /// <see cref="EnsureActive"/> se evalúa contra la copia en memoria del que escribe y no
    /// contra el estado real al momento del commit. Lo encontraron los lentes de fiabilidad y
    /// resiliencia en la revisión de CAT-02.
    /// </summary>
    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Product Create(
        ProductId id,
        Guid tenantId,
        string name,
        string code,
        ProductDetails details,
        DateTimeOffset occurredAt) =>
        new(id, tenantId, NormalizeName(name), NormalizeCode(code), details, occurredAt);

    public void Update(
        string name,
        string code,
        ProductDetails details,
        DateTimeOffset occurredAt)
    {
        EnsureActive();

        Name = NormalizeName(name);
        Code = NormalizeCode(code);
        Apply(details);
        Version++;
        UpdatedAt = occurredAt;
    }

    // Asigna los cinco siempre, incluidos los null. Se puede **limpiar** un campo, no sólo
    // setearlo: una implementación que ignore los null "para no pisar" deja campos imborrables y
    // pasa todas las demás pruebas. Por eso CA-CAT-04-03 existe.
    private void Apply(ProductDetails details)
    {
        var normalized = details.Normalized();

        Description = normalized.Description;
        ImageFileId = normalized.ImageFileId;
        Price = normalized.Price;
        Currency = normalized.Currency;
        TaxRateId = normalized.TaxRateId;
    }

    public void Deactivate(DateTimeOffset occurredAt)
    {
        if (!IsActive)
        {
            throw new CatalogDomainException(
                "catalog.product.already_inactive",
                "The product is already inactive.");
        }

        IsActive = false;
        Version++;
        UpdatedAt = occurredAt;
    }

    // La vuelta de Deactivate, que hasta CAT-07 no existia: un producto inactivo era terminal,
    // porque Update abre con EnsureActive() y ningun metodo devolvia IsActive a true.
    //
    // No revalida la unicidad del codigo a proposito. IX_products_tenant_code es unico **sin
    // filtro parcial**, asi que desactivar nunca libero el codigo y reactivar no puede colisionar
    // con nadie. Si alguien le agrega un filtro parcial al indice, CA-CAT-07-09 se cae y avisa.
    public void Activate(DateTimeOffset occurredAt)
    {
        if (IsActive)
        {
            throw new CatalogDomainException(
                "catalog.product.already_active",
                "The product is already active.");
        }

        IsActive = true;
        Version++;
        UpdatedAt = occurredAt;
    }

    private void EnsureActive()
    {
        if (!IsActive)
        {
            throw new CatalogDomainException(
                "catalog.product.inactive",
                "An inactive product cannot be edited.");
        }
    }

    // Recortar espacios es parte del invariante, no higiene del llamador: el índice único trata
    // " VS-001" y "VS-001" como dos códigos distintos, cosa que nadie leyendo la lista haría.
    private static string NormalizeName(string name) =>
        Normalize(
            name,
            NameMaxLength,
            "catalog.product.name_required",
            "The product name is required.",
            "catalog.product.name_too_long",
            $"The product name cannot exceed {NameMaxLength} characters.");

    private static string NormalizeCode(string code) =>
        Normalize(
            code,
            CodeMaxLength,
            "catalog.product.code_required",
            "The product code is required.",
            "catalog.product.code_too_long",
            $"The product code cannot exceed {CodeMaxLength} characters.");

    private static string Normalize(
        string value,
        int maxLength,
        string requiredCode,
        string requiredMessage,
        string tooLongCode,
        string tooLongMessage)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CatalogDomainException(requiredCode, requiredMessage);
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength
            ? throw new CatalogDomainException(tooLongCode, tooLongMessage)
            : trimmed;
    }
}
