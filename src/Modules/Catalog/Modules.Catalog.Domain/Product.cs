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

    // Topes de variantes (spec 2026-10-20 §3.2-§3.3, decisiones 3 y 5 del owner): los de Shopify
    // para opciones y variantes, así un producto de Shopify siempre cabe; la galería, del owner.
    public const int MaxOptions = 3;
    public const int MaxVariants = 2048;
    public const int MaxGalleryImages = 250;

    private readonly List<ProductVariant> _variants = [];
    private readonly List<ProductGalleryImage> _gallery = [];

    // EF Core materializa por acá. El código nunca construye el agregado así:
    // Create es el único punto de entrada, y es el que hace cumplir los invariantes.
    private Product() => Name = string.Empty;

    private Product(
        ProductId id,
        Guid tenantId,
        string name,
        ProductDetails details,
        DateTimeOffset occurredAt)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
        Apply(details);
        IsActive = true;
        Version = 1;
        CreatedAt = occurredAt;
        UpdatedAt = occurredAt;
    }

    public ProductId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    public bool IsActive { get; private set; }

    // --- CAT-04: propiedades opcionales. Nacen nullable porque hay productos ya cargados: una
    // columna NOT NULL sin default los rompe. Price se retiró en CAT-09, reemplazado por los
    // precios por moneda, que desde 2026-10-20 son de cada variante.

    public string? Description { get; private set; }

    /// <summary>
    /// Tasa de impuesto del producto. La FK de base garantiza que la fila exista, **pero no que
    /// sea del mismo tenant**: eso lo verifica el handler antes de asignarla.
    /// </summary>
    public TaxRateId? TaxRateId { get; private set; }

    /// <summary>
    /// Las opciones de la biblioteca del tenant (<see cref="OptionDefinition"/>) que arman las
    /// variantes, en orden; a lo sumo <see cref="MaxOptions"/>. Vacía en un producto sin opciones,
    /// que tiene exactamente una variante: la variante por defecto.
    /// </summary>
    public IReadOnlyList<Guid> OptionIds { get; private set; } = [];

    /// <summary>Las variantes en orden de presentación (<see cref="ProductVariant.Position"/>).
    /// Nunca vacía.</summary>
    public IReadOnlyList<ProductVariant> Variants =>
        _variants.OrderBy(variant => variant.Position).ToArray();

    /// <summary>Las fotos del producto en orden; la primera es la principal.</summary>
    public IReadOnlyList<ProductGalleryImage> Gallery =>
        _gallery.OrderBy(image => image.Position).ToArray();

    /// <summary>
    /// La imagen principal: el archivo de la primera foto de la <see cref="Gallery"/>. Hasta las
    /// variantes era un campo propio; ahora se deriva, así que no puede contradecir a la galería.
    /// Sigue siendo **referencia blanda, sin FK**: <c>catalog</c> no referencia
    /// <c>storage.file_resources</c>.
    /// </summary>
    public Guid? ImageFileId =>
        _gallery.Count == 0 ? null : _gallery.MinBy(image => image.Position)!.FileId;

    // --- Transitorio (variantes, Tarea 2). Code, precios, escalas y empaques son de la variante;
    // estos accesos leen la primera variante para que los llamadores que todavía piensan en "un
    // producto, un código" (persistencia, adaptadores de Bootstrapper, Excel, historial y copia de
    // escalas) compilen hasta que sus tareas los pasen a variantes (T3, T5, T7, T8, T9). En un
    // producto sin opciones es exactamente lo de antes. Código nuevo no los usa: lee Variants.

    /// <summary>Transitorio: el <see cref="ProductVariant.Code"/> de la primera variante.</summary>
    public string Code => PrimaryVariant.Code;

    /// <summary>Transitorio: los precios de la primera variante.</summary>
    public IReadOnlyCollection<ProductPrice> Prices => PrimaryVariant.Prices;

    /// <summary>Transitorio: las monedas de la primera variante.</summary>
    public IReadOnlyCollection<string> PricedCurrencies => PrimaryVariant.PricedCurrencies;

    /// <summary>Transitorio: el precio de la primera variante en esa moneda.</summary>
    public decimal? PriceIn(string currency) => PrimaryVariant.PriceIn(currency);

    /// <summary>Transitorio: las escalas de la primera variante.</summary>
    public IReadOnlyCollection<PriceScale> PriceScales => PrimaryVariant.PriceScales;

    /// <summary>Transitorio: los empaques de la primera variante.</summary>
    public IReadOnlyList<int> PackagingUnits => PrimaryVariant.PackagingUnits;

    private ProductVariant PrimaryVariant =>
        _variants.MinBy(variant => variant.Position)
        ?? throw new InvalidOperationException("A product always has at least one variant.");

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

    /// <summary>
    /// Crea un producto sin opciones, con su variante por defecto. La variante toma el mismo id
    /// que el producto (<c>defaultVariant.Id</c> se ignora): es lo que hace la migración con los
    /// productos de antes, y así un producto nuevo y uno migrado se ven igual.
    /// </summary>
    public static Product Create(
        ProductId id,
        Guid tenantId,
        string name,
        ProductDetails details,
        ProductVariantInput defaultVariant,
        IReadOnlyList<Guid> galleryFileIds,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(defaultVariant);

        var product = new Product(id, tenantId, NormalizeName(name), details, occurredAt);
        product.ReplaceGallery(galleryFileIds);
        product.ReplaceVariants([defaultVariant with { Id = id.Value }], optionCount: 0, isCreate: true);
        return product;
    }

    /// <summary>
    /// Reemplaza el recurso entero, como el PUT: nombre, datos opcionales, opciones, variantes y
    /// galería. Las variantes se emparejan por id (spec §3.3.1); una variante guardada que no
    /// viene se rechaza con <c>catalog.variant.missing_in_request</c> —no se borra en silencio—.
    /// Los topes y los ids se validan antes de tocar nada; las reglas entre variantes (combinación,
    /// código, valores por opción), antes de aplicar la primera. Lo que falla después —precios,
    /// escalas, empaques de una variante— deja el agregado a medias, como antes de las variantes:
    /// la excepción aborta la unidad de trabajo y nada se guarda.
    /// </summary>
    public void Update(
        string name,
        ProductDetails details,
        IReadOnlyList<Guid> optionIds,
        IReadOnlyList<ProductVariantInput> variants,
        IReadOnlyList<Guid> galleryFileIds,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(optionIds);
        ArgumentNullException.ThrowIfNull(variants);
        ArgumentNullException.ThrowIfNull(galleryFileIds);

        EnsureActive();

        var normalizedName = NormalizeName(name);
        EnsureOptions(optionIds);
        EnsureGallery(galleryFileIds);
        EnsureVariantCount(variants);
        EnsureSavedVariantsArePresent(variants);

        Name = normalizedName;
        Apply(details);
        OptionIds = optionIds.ToArray();
        ReplaceGallery(galleryFileIds);
        ReplaceVariants(variants, optionIds.Count, isCreate: false);
        Version++;
        UpdatedAt = occurredAt;
    }

    // Asigna los dos siempre, incluidos los null. Se puede **limpiar** un campo, no sólo
    // setearlo: una implementación que ignore los null "para no pisar" deja campos imborrables y
    // pasa todas las demás pruebas. Por eso CA-CAT-04-03 existe.
    private void Apply(ProductDetails details)
    {
        var normalized = details.Normalized();

        Description = normalized.Description;
        TaxRateId = normalized.TaxRateId;
    }

    private static void EnsureOptions(IReadOnlyList<Guid> optionIds)
    {
        if (optionIds.Count > MaxOptions)
        {
            throw new CatalogDomainException(
                "catalog.product.option_limit",
                $"A product cannot have more than {MaxOptions} options.");
        }

        if (optionIds.Distinct().Count() != optionIds.Count)
        {
            throw new CatalogDomainException(
                "catalog.product.option_duplicate",
                "A product cannot use the same option twice.");
        }
    }

    private static void EnsureGallery(IReadOnlyList<Guid> galleryFileIds)
    {
        if (galleryFileIds.Count > MaxGalleryImages)
        {
            throw new CatalogDomainException(
                "catalog.product.gallery_limit",
                $"A product cannot have more than {MaxGalleryImages} images.");
        }

        // Rechaza en vez de deduplicar, como los empaques: la misma foto dos veces es un error del
        // que la mandó, y descartarla en silencio le cambiaría el orden que eligió.
        if (galleryFileIds.Distinct().Count() != galleryFileIds.Count)
        {
            throw new CatalogDomainException(
                "catalog.product.gallery_duplicate",
                "The product gallery cannot repeat an image.");
        }
    }

    private static void EnsureVariantCount(IReadOnlyList<ProductVariantInput> variants)
    {
        if (variants.Count == 0)
        {
            throw new CatalogDomainException(
                "catalog.variant.required",
                "A product requires at least one variant.");
        }

        if (variants.Count > MaxVariants)
        {
            throw new CatalogDomainException(
                "catalog.variant.limit_exceeded",
                $"A product cannot have more than {MaxVariants} variants.");
        }
    }

    private void EnsureSavedVariantsArePresent(IReadOnlyList<ProductVariantInput> variants)
    {
        var requested = new HashSet<Guid>();
        foreach (var variant in variants)
        {
            if (variant.Id is not { } id)
            {
                continue;
            }

            if (!requested.Add(id))
            {
                throw new CatalogDomainException(
                    "catalog.variant.duplicated_in_request",
                    "The same variant cannot come twice in the request.");
            }

            if (!_variants.Exists(saved => saved.Id.Value == id))
            {
                throw new CatalogDomainException(
                    "catalog.variant.not_found",
                    "The variant does not belong to this product.");
            }
        }

        if (_variants.Exists(saved => !requested.Contains(saved.Id.Value)))
        {
            throw new CatalogDomainException(
                "catalog.variant.missing_in_request",
                "Every saved variant must come in the request; a missing one is neither deleted nor kept.");
        }
    }

    /// <summary>
    /// Valida las variantes entre sí y después las aplica. Lo de cada variante por su cuenta
    /// (precios, escalas, empaques) lo valida la variante al aplicarse.
    /// </summary>
    private void ReplaceVariants(
        IReadOnlyList<ProductVariantInput> variants,
        int optionCount,
        bool isCreate)
    {
        var codes = new HashSet<string>(StringComparer.Ordinal);
        var barcodes = new HashSet<string>(StringComparer.Ordinal);
        var activeCombinations = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<(ProductVariantInput Input, string Code, string? Barcode, Guid? ImageId)>(variants.Count);

        foreach (var variant in variants)
        {
            if (variant.OptionValueIds.Count != optionCount)
            {
                throw new CatalogDomainException(
                    "catalog.variant.value_count_mismatch",
                    "Every variant needs exactly one value per product option.");
            }

            // Sólo entre activas: quitar una opción puede dejar dos variantes con la misma
            // combinación, y la que sobra queda inactiva (spec §3.3.1).
            if (variant.IsActive
                && !activeCombinations.Add(string.Join(',', variant.OptionValueIds)))
            {
                throw new CatalogDomainException(
                    "catalog.variant.combination_duplicate",
                    "Two active variants of a product cannot have the same combination of values.");
            }

            var code = ProductVariant.NormalizeCode(variant.Code);
            // Mismo código que la colisión entre productos (IX de base): para quien llena el
            // formulario es el mismo error, un código que otro ya usa.
            if (!codes.Add(code))
            {
                throw new CatalogDomainException(
                    "catalog.product.code_taken",
                    "Another variant of this product already uses that code.");
            }

            var barcode = ProductVariant.NormalizeBarcode(variant.Barcode);
            if (barcode is not null && !barcodes.Add(barcode))
            {
                throw new CatalogDomainException(
                    "catalog.variant.barcode_taken",
                    "Another variant of this product already uses that barcode.");
            }

            normalized.Add((variant, code, barcode, ResolveGalleryImage(variant.GalleryFileId)));
        }

        for (var position = 0; position < normalized.Count; position++)
        {
            var (input, code, barcode, imageId) = normalized[position];
            var target = isCreate || input.Id is null
                ? null
                : _variants.Find(saved => saved.Id.Value == input.Id.Value);

            if (target is null)
            {
                var variantId = input.Id is { } fixedId && isCreate
                    ? new VariantId(fixedId)
                    : VariantId.New();
                target = ProductVariant.Create(variantId, Id, TenantId);
                _variants.Add(target);
            }

            target.Apply(input, code, barcode, position, imageId);
        }
    }

    private Guid? ResolveGalleryImage(Guid? fileId)
    {
        if (fileId is not { } value)
        {
            return null;
        }

        return _gallery.Find(image => image.FileId == value)?.Id
            ?? throw new CatalogDomainException(
                "catalog.variant.image_not_in_gallery",
                "The variant image must be one of the product gallery images.");
    }

    // En el lugar: una foto que sigue en la galería conserva su fila (y su id, al que apuntan las
    // variantes); sólo cambia de posición.
    private void ReplaceGallery(IReadOnlyList<Guid> galleryFileIds)
    {
        _gallery.RemoveAll(image => !galleryFileIds.Contains(image.FileId));
        for (var position = 0; position < galleryFileIds.Count; position++)
        {
            var fileId = galleryFileIds[position];
            var existing = _gallery.Find(image => image.FileId == fileId);
            if (existing is null)
            {
                _gallery.Add(new ProductGalleryImage(Guid.CreateVersion7(), fileId, position));
            }
            else
            {
                existing.Position = position;
            }
        }
    }

    /// <summary>
    /// Reemplaza **sólo** las escalas: los precios base y el resto de los datos maestros
    /// quedan como estaban. Es lo que necesita copiar las escalas de otro producto.
    ///
    /// No pasa por <see cref="Update"/> a propósito. Ese verbo reemplaza el producto entero,
    /// así que el caso de uso tendría que reenviarle nombre, variantes y galería para no
    /// borrarlos — y un lote que arrastre mal cualquiera de esos campos los limpia en todos los
    /// destinos de una vez. Acá no hay nada que arrastrar.
    ///
    /// Y quedan **incompletas**: sin restricción, múltiplo, empaque ni agrupación
    /// (<see cref="PriceScale.CreateIncomplete"/>). Es el único camino que las produce.
    ///
    /// Transitorio (variantes, Tarea 2): copia sobre la primera variante, que en un producto sin
    /// opciones es la única. La copia por variante es de la Tarea 7.
    /// </summary>
    public void ApplyCopiedPriceScales(
        IReadOnlyCollection<PriceScaleInput> scales,
        DateTimeOffset occurredAt)
    {
        EnsureActive();

        PrimaryVariant.ApplyCopiedPriceScales(scales);

        Version++;
        UpdatedAt = occurredAt;
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
