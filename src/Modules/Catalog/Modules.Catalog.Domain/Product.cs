namespace Modules.Catalog.Domain;

/// <summary>
/// Un producto del catálogo de un tenant (RF-020). Guarda sólo los datos maestros vivos: un
/// documento congela su propia copia de lo vendido.
/// </summary>
public sealed class Product
{
    // Espeja los anchos de columna de catalog.products. Guardar acá significa que un valor
    // demasiado largo falla como 422 con código de dominio en vez de llegar a PostgreSQL y
    // volver como 500 server.unexpected.
    public const int NameMaxLength = 200;
    public const int CodeMaxLength = 60;

    // Límites de PackagingUnits. Públicos para que quien dependa de ellos los cite por nombre.
    // Quotations no puede referenciar Catalog, así que su regla de empaque repite el número en
    // un comentario: si cambias éstos, revisa QuotationScaleRestrictionRule.
    public const int MaxPackagingUnits = 10;
    public const int MaxPackagingUnitValue = 100_000;

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
    // columna NOT NULL sin default los rompe. Price se retiró en CAT-09, reemplazado por los
    // precios por moneda de más abajo.

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
    /// Tasa de impuesto del producto. La FK de base garantiza que la fila exista, **pero no que
    /// sea del mismo tenant**: eso lo verifica el handler antes de asignarla.
    /// </summary>
    public TaxRateId? TaxRateId { get; private set; }

    // --- Prices per currency (spec 2026-10-08, D3). They replaced the two fixed-currency base
    // prices: the legacy price_base_usd/price_base_cop columns stay in catalog.products until
    // DropLegacyProductPriceColumns, unmapped.

    private readonly List<ProductPrice> _prices = [];

    public IReadOnlyCollection<ProductPrice> Prices => _prices;

    /// <summary>The currencies this product can be quoted and sold in, in ordinal order.</summary>
    public IReadOnlyCollection<string> PricedCurrencies =>
        _prices.Select(price => price.Currency).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Null when the product has no price in that currency. Never a conversion.</summary>
    public decimal? PriceIn(string currency) =>
        _prices.Find(price => price.Currency == currency)?.Amount;

    private readonly List<PriceScale> _priceScales = [];

    /// <summary>
    /// Los tramos de precio por cantidad del producto. Se reemplazan enteros en cada
    /// <see cref="Update"/> — mismo criterio que los cinco opcionales de <see cref="Apply"/>:
    /// el verbo PUT reemplaza el recurso completo, así que una escala que no viene en el
    /// request deja de existir.
    /// </summary>
    public IReadOnlyCollection<PriceScale> PriceScales => _priceScales;

    /// <summary>
    /// Los empaques en que viene el producto, en unidades: una keratina que llega en cajas de 100
    /// y de 150 guarda <c>[100, 150]</c>. Entre 1 y <see cref="MaxPackagingUnitValue"/>, a lo sumo
    /// <see cref="MaxPackagingUnits"/>, sin repetidos y en orden ascendente.
    ///
    /// Son del producto y no de la escala desde el 2026-10-01: antes cada escala guardaba un solo
    /// número, y un producto con dos empaques no tenía cómo decirlo. La escala conserva la
    /// restricción <see cref="PriceScaleRestriction.PackagingUnit"/>, que ahora significa "usa
    /// estos empaques". Por eso no pueden estar vacíos mientras una escala la use; al revés sí
    /// vale — un producto puede declarar sus empaques sin que ninguna escala los exija.
    /// </summary>
    public IReadOnlyList<int> PackagingUnits { get; private set; } = [];

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
        TaxRateId = normalized.TaxRateId;
    }

    private void ApplyPricing(ProductPricing pricing)
    {
        // Unconditional: every product needs a price in at least one currency (CAT-09, spec D3).
        if (pricing.Prices.Count == 0)
        {
            throw new CatalogDomainException(
                "catalog.product.price_required",
                "The product requires a price in at least one currency.");
        }

        foreach (var (currency, amount) in pricing.Prices)
        {
            EnsureCurrencyCodeShape(currency);
            EnsurePriceNotNegative(amount);
        }

        ReplacePrices(pricing.Prices);
        PackagingUnits = NormalizePackagingUnits(pricing.PackagingUnits);

        ReplaceScales(pricing.Scales);

        // Después de las escalas y no antes: un error propio de una escala (rango, descuento,
        // múltiplo) dice más que éste, que sólo aplica si la escala ya es válida por su cuenta.
        if (PackagingUnits.Count == 0
            && _priceScales.Any(scale => scale.Restriction == PriceScaleRestriction.PackagingUnit))
        {
            throw new CatalogDomainException(
                "catalog.product.packaging_units_required",
                "A price scale restricted to the packaging unit requires the product to have at least one packaging unit.");
        }
    }

    // In place and not Clear()+Add: the owned rows are keyed (product_id, currency), and EF would
    // track a deleted and an added row with the same key in the same SaveChanges.
    private void ReplacePrices(IReadOnlyDictionary<string, decimal> prices)
    {
        _prices.RemoveAll(price => !prices.ContainsKey(price.Currency));
        foreach (var (currency, amount) in prices)
        {
            var existing = _prices.Find(price => price.Currency == currency);
            if (existing is null)
            {
                _prices.Add(new ProductPrice(currency, amount));
            }
            else
            {
                existing.ChangeAmount(amount);
            }
        }
    }

    // Shape only — the catalogue check is Currencies.Normalize in the application. A code that
    // reaches the aggregate unnormalised is a programming error, so it is not a 422.
    private static void EnsureCurrencyCodeShape(string currency)
    {
        if (currency is not { Length: 3 } || !currency.All(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException(
                $"Currency '{currency}' must arrive normalised (three upper-case letters).",
                nameof(currency));
        }
    }

    // Rechaza en vez de corregir: un cero, un negativo o un repetido es un error de tipeo, y
    // descartarlo en silencio le guardaría al usuario algo distinto de lo que cree haber cargado.
    // Ordenar sí se hace aquí — el orden no cambia el significado del conjunto.
    //
    // Los topes (MaxPackagingUnits, MaxPackagingUnitValue) no salen de ningún empaque real: la
    // regla de Quotations cuenta por restos módulo el empaque menor y recorre cada empaque, así que
    // sin ellos un dato absurdo aquí se volvería memoria y tiempo en cada cotización.
    private static int[] NormalizePackagingUnits(IReadOnlyCollection<int> packagingUnits)
    {
        if (packagingUnits.Count > MaxPackagingUnits)
        {
            throw new CatalogDomainException(
                "catalog.product.packaging_units.too_many",
                $"A product cannot have more than {MaxPackagingUnits} packaging units.");
        }

        if (packagingUnits.Any(unit => unit is <= 0 or > MaxPackagingUnitValue))
        {
            throw new CatalogDomainException(
                "catalog.product.packaging_units.invalid",
                $"Every packaging unit must be between 1 and {MaxPackagingUnitValue}.");
        }

        var sorted = packagingUnits.Order().ToArray();
        for (var index = 1; index < sorted.Length; index++)
        {
            if (sorted[index] == sorted[index - 1])
            {
                throw new CatalogDomainException(
                    "catalog.product.packaging_units.duplicated",
                    "The packaging units cannot repeat a value.");
            }
        }

        return sorted;
    }

    /// <summary>
    /// Reemplaza **sólo** las escalas: los precios base y el resto de los datos maestros
    /// quedan como estaban. Es lo que necesita copiar las escalas de otro producto.
    ///
    /// No pasa por <see cref="Update"/> a propósito. Ese verbo reemplaza el producto entero,
    /// así que el caso de uso tendría que reenviarle nombre, código y los tres opcionales
    /// para no borrarlos — y un lote que arrastre mal cualquiera de esos campos los limpia en
    /// todos los destinos de una vez. Acá no hay nada que arrastrar.
    ///
    /// Y quedan **incompletas**: sin restricción, múltiplo, empaque ni agrupación
    /// (<see cref="PriceScale.CreateIncomplete"/>). Es el único camino que las produce, y por eso
    /// el nombre dice "copiadas": el formulario pasa por <see cref="Update"/>, que sigue exigiendo
    /// la restricción.
    /// </summary>
    public void ApplyCopiedPriceScales(
        IReadOnlyCollection<PriceScaleInput> scales,
        DateTimeOffset occurredAt)
    {
        EnsureActive();

        _priceScales.Clear();
        foreach (var scale in scales)
        {
            _priceScales.Add(
                PriceScale.CreateIncomplete(Id, TenantId, scale));
        }

        Version++;
        UpdatedAt = occurredAt;
    }

    private void ReplaceScales(IReadOnlyCollection<PriceScaleInput> scales)
    {
        _priceScales.Clear();
        foreach (var scale in scales)
        {
            _priceScales.Add(PriceScale.Create(Id, TenantId, scale));
        }
    }

    private static void EnsurePriceNotNegative(decimal value)
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
