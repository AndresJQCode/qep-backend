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
        ProductPricing pricing,
        DateTimeOffset occurredAt)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
        Code = code;
        Apply(details);
        ApplyPricing(pricing);
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

    // --- CAT-04: propiedades opcionales. Nacen nullable porque hay productos ya cargados: una
    // columna NOT NULL sin default los rompe. Price se retiró en CAT-09, reemplazado por el
    // precio en USD/COP de más abajo. Currency se mantiene — no es sólo la moneda de Price:
    // es un dato independiente del producto.

    public string? Description { get; private set; }

    /// <summary>Código ISO-4217 de tres letras, en mayúsculas.</summary>
    public string? Currency { get; private set; }

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
    /// Tasa de impuesto del producto. La FK de base garantiza que la fila exista, **pero no que
    /// sea del mismo tenant**: eso lo verifica el handler antes de asignarla.
    /// </summary>
    public TaxRateId? TaxRateId { get; private set; }

    // --- CAT-09: precio base y final en dos monedas fijas, más las escalas por cantidad.
    // Único precio del producto — reemplazó por completo al viejo Price/Currency, retirado.

    /// <summary>Precio base en dólares. Junto con <see cref="PriceBaseCop"/>, al menos uno de
    /// los dos es obligatorio: un producto sin precio en ninguna moneda no es válido.</summary>
    public decimal? PriceBaseUsd { get; private set; }

    /// <summary>Precio base en pesos colombianos. Ver <see cref="PriceBaseUsd"/>.</summary>
    public decimal? PriceBaseCop { get; private set; }

    /// <summary>
    /// Precio final en dólares. Lo calcula y manda el cliente — el backend no lo deriva — pero
    /// lo valida contra <c>PriceBaseUsd × (1 − Discount%)</c> con una tolerancia de un centavo.
    /// Sólo puede existir si <see cref="PriceBaseUsd"/> existe, y si éste existe aquél es
    /// obligatorio.
    /// </summary>
    public decimal? PriceFinalUsd { get; private set; }

    /// <summary>Precio final en pesos colombianos. Ver <see cref="PriceFinalUsd"/>.</summary>
    public decimal? PriceFinalCop { get; private set; }

    /// <summary>Porcentaje de descuento, 0 a 100, el mismo para ambas monedas. Null se trata
    /// como 0% al validar el precio final.</summary>
    public decimal? Discount { get; private set; }

    private readonly List<PriceScale> _priceScales = [];

    /// <summary>
    /// Los tramos de precio por cantidad del producto. Se reemplazan enteros en cada
    /// <see cref="Update"/> — mismo criterio que los cinco opcionales de <see cref="Apply"/>:
    /// el verbo PUT reemplaza el recurso completo, así que una escala que no viene en el
    /// request deja de existir.
    /// </summary>
    public IReadOnlyCollection<PriceScale> PriceScales => _priceScales;

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
        ProductPricing pricing,
        DateTimeOffset occurredAt) =>
        new(id, tenantId, NormalizeName(name), NormalizeCode(code), details, pricing, occurredAt);

    public void Update(
        string name,
        string code,
        ProductDetails details,
        ProductPricing pricing,
        DateTimeOffset occurredAt)
    {
        EnsureActive();

        Name = NormalizeName(name);
        Code = NormalizeCode(code);
        Apply(details);
        ApplyPricing(pricing);
        Version++;
        UpdatedAt = occurredAt;
    }

    // Asigna los cuatro siempre, incluidos los null. Se puede **limpiar** un campo, no sólo
    // setearlo: una implementación que ignore los null "para no pisar" deja campos imborrables y
    // pasa todas las demás pruebas. Por eso CA-CAT-04-03 existe.
    private void Apply(ProductDetails details)
    {
        var normalized = details.Normalized();

        Description = normalized.Description;
        ImageFileId = normalized.ImageFileId;
        Currency = normalized.Currency;
        TaxRateId = normalized.TaxRateId;
    }

    // CAT-09. El descuento validado primero porque el resto de las reglas lo usan para
    // comparar el precio final; validar montos negativos antes que "al menos una moneda" para
    // que un valor negativo se reporte como tal y no como si faltara.
    private void ApplyPricing(ProductPricing pricing)
    {
        var discount = pricing.Discount ?? 0m;
        if (discount < PriceScale.MinDiscount || discount > PriceScale.MaxDiscount)
        {
            throw new CatalogDomainException(
                "catalog.product.discount_out_of_range",
                $"The product discount must be between {PriceScale.MinDiscount} and {PriceScale.MaxDiscount}.");
        }

        EnsurePriceNotNegative(pricing.BaseUsd);
        EnsurePriceNotNegative(pricing.BaseCop);
        EnsurePriceNotNegative(pricing.FinalUsd);
        EnsurePriceNotNegative(pricing.FinalCop);

        // Incondicional: todo producto necesita precio en al menos una moneda, sin excepción.
        if (pricing.BaseUsd is null && pricing.BaseCop is null)
        {
            throw new CatalogDomainException(
                "catalog.product.price_base_currency_required",
                "The product requires a base price in at least one currency.");
        }

        ValidateFinalAgainstBase(
            pricing.FinalUsd,
            pricing.BaseUsd,
            discount,
            "catalog.product.price_final_without_base_usd",
            "catalog.product.price_final_required_usd",
            "catalog.product.price_final_mismatch_usd",
            "USD");
        ValidateFinalAgainstBase(
            pricing.FinalCop,
            pricing.BaseCop,
            discount,
            "catalog.product.price_final_without_base_cop",
            "catalog.product.price_final_required_cop",
            "catalog.product.price_final_mismatch_cop",
            "COP");

        PriceBaseUsd = pricing.BaseUsd;
        PriceBaseCop = pricing.BaseCop;
        PriceFinalUsd = pricing.FinalUsd;
        PriceFinalCop = pricing.FinalCop;
        Discount = discount;

        _priceScales.Clear();
        foreach (var scale in pricing.Scales)
        {
            _priceScales.Add(PriceScale.Create(Id, TenantId, scale, PriceBaseUsd, PriceBaseCop));
        }
    }

    // Pareja obligatoria en los dos sentidos: un
    // precio final sin base no dice contra qué se calculó, y una base sin su final es un
    // request a medio llenar que el front nunca debería mandar completo. Además, cuando los dos
    // existen, el final tiene que ser consistente con base × (1 − descuento%).
    private static void ValidateFinalAgainstBase(
        decimal? final,
        decimal? baseAmount,
        decimal discount,
        string withoutBaseCode,
        string requiredCode,
        string mismatchCode,
        string currencyLabel)
    {
        if (baseAmount is null)
        {
            if (final is not null)
            {
                throw new CatalogDomainException(
                    withoutBaseCode,
                    $"A final price in {currencyLabel} requires a base price in {currencyLabel}.");
            }

            return;
        }

        if (final is null)
        {
            throw new CatalogDomainException(
                requiredCode,
                $"A base price in {currencyLabel} requires its final price in {currencyLabel}.");
        }

        var expected = Math.Round(
            baseAmount.Value * (1 - discount / 100m), 2, MidpointRounding.AwayFromZero);
        if (Math.Abs(final.Value - expected) > 0.01m)
        {
            throw new CatalogDomainException(
                mismatchCode,
                $"The final price in {currencyLabel} does not match the base price and the discount.");
        }
    }

    private static void EnsurePriceNotNegative(decimal? value)
    {
        if (value < 0m)
        {
            throw new CatalogDomainException(
                "catalog.product.price_negative",
                "A product price cannot be negative.");
        }
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
