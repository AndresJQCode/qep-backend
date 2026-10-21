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
    private readonly List<ProductOption> _options = [];

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
    public IReadOnlyList<Guid> OptionIds =>
        _options.OrderBy(option => option.Position).Select(option => option.OptionDefinitionId.Value).ToArray();

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

    // Código, código de barras, precios, escalas y empaques son de cada variante (spec 2026-10-20
    // §3.2): el producto no los expone. Un producto sin opciones los lee de Variants[0], su variante
    // por defecto, que lleva el mismo id que el producto.

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

        ArgumentNullException.ThrowIfNull(galleryFileIds);
        EnsureGallery(galleryFileIds);

        var product = new Product(id, tenantId, NormalizeName(name), details, occurredAt);
        product.ReplaceGallery(galleryFileIds);
        product.ReplaceVariants([defaultVariant with { Id = id.Value }], optionCount: 0, isCreate: true);
        return product;
    }

    /// <summary>
    /// Crea un producto con sus opciones y todas sus variantes de una vez (el POST del
    /// formulario), con <c>Version = 1</c> como cualquier producto nuevo. La primera variante toma el
    /// id del producto, igual que la variante por defecto de <see cref="Create(ProductId, Guid, string, ProductDetails, ProductVariantInput, IReadOnlyList{Guid}, DateTimeOffset)"/>;
    /// el resto nace con id nuevo. Los <c>Id</c> que traigan las variantes se ignoran: en un
    /// producto nuevo no hay ninguna guardada con la cual emparejar.
    /// </summary>
    public static Product Create(
        ProductId id,
        Guid tenantId,
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

        var normalizedName = NormalizeName(name);
        EnsureOptions(optionIds);
        EnsureGallery(galleryFileIds);
        EnsureVariantCount(variants);

        var product = new Product(id, tenantId, normalizedName, details, occurredAt);
        product.ReplaceOptions(optionIds);
        product.ReplaceGallery(galleryFileIds);
        product.ReplaceVariants(
            variants.Select((variant, index) => variant with { Id = index == 0 ? id.Value : null }).ToArray(),
            optionIds.Count,
            isCreate: true);
        return product;
    }

    /// <summary>
    /// Reemplaza el recurso entero, como el PUT: nombre, datos opcionales, opciones, variantes y
    /// galería. Las variantes se emparejan por id (spec §3.3.1); una variante guardada que no
    /// viene se rechaza con <c>catalog.variant.missing_in_request</c> —no se borra en silencio—,
    /// salvo que esté en <paramref name="deletedVariantIds"/>.
    /// Los topes y los ids se validan antes de tocar nada; las reglas entre variantes (combinación,
    /// código, valores por opción), antes de aplicar la primera. Lo que falla después —precios,
    /// escalas, empaques de una variante— deja el agregado a medias, como antes de las variantes:
    /// la excepción aborta la unidad de trabajo y nada se guarda.
    /// </summary>
    /// <param name="deletedVariantIds">Variantes guardadas que se borran (spec §3.3). El
    /// agregado no sabe si una variante está en uso —las líneas viven en otros módulos—, así que
    /// sólo borra las que el llamador le nombra después de comprobarlo; una ausente que no está
    /// acá sigue siendo <c>missing_in_request</c>. Una variante en uso no llega por este camino:
    /// el llamador la manda en <paramref name="variants"/>, inactiva.</param>
    public void Update(
        string name,
        ProductDetails details,
        IReadOnlyList<Guid> optionIds,
        IReadOnlyList<ProductVariantInput> variants,
        IReadOnlyList<Guid> galleryFileIds,
        DateTimeOffset occurredAt,
        IReadOnlyCollection<Guid>? deletedVariantIds = null)
    {
        ArgumentNullException.ThrowIfNull(optionIds);
        ArgumentNullException.ThrowIfNull(variants);
        ArgumentNullException.ThrowIfNull(galleryFileIds);
        var deleted = (deletedVariantIds ?? []).ToHashSet();

        EnsureActive();

        var normalizedName = NormalizeName(name);
        EnsureOptions(optionIds);
        EnsureGallery(galleryFileIds);
        EnsureVariantCount(variants);
        EnsureSavedVariantsArePresent(variants, deleted);

        Name = normalizedName;
        Apply(details);
        ReplaceOptions(optionIds);
        ReplaceGallery(galleryFileIds);
        // Antes de ReplaceVariants: una variante borrada libera su código y su combinación para las
        // del cuerpo. EF borra la fila (y en cascada sus precios, escalas, valores e historial) en el
        // mismo guardado.
        _variants.RemoveAll(variant => deleted.Contains(variant.Id.Value));
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

    private void EnsureSavedVariantsArePresent(
        IReadOnlyList<ProductVariantInput> variants,
        IReadOnlyCollection<Guid> deletedVariantIds)
    {
        // Con conjuntos: hasta 2.048 variantes por lado, y Exists por cada id sería cuadrático.
        var savedIds = _variants.Select(saved => saved.Id.Value).ToHashSet();
        foreach (var id in deletedVariantIds)
        {
            if (!savedIds.Contains(id))
            {
                throw new CatalogDomainException(
                    "catalog.variant.not_found",
                    "The variant does not belong to this product.");
            }
        }

        var requested = new HashSet<Guid>(deletedVariantIds);
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

            if (!savedIds.Contains(id))
            {
                throw new CatalogDomainException(
                    "catalog.variant.not_found",
                    "The variant does not belong to this product.");
            }
        }

        if (savedIds.Any(id => !requested.Contains(id)))
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
                && !activeCombinations.Add(ProductVariant.CombinationKeyOf(variant.OptionValueIds)))
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

    // En el lugar, por posición: la fila se identifica por (producto, posición), y EF seguiría una
    // borrada y una agregada con la misma clave en el mismo guardado.
    private void ReplaceOptions(IReadOnlyList<Guid> optionIds)
    {
        _options.RemoveAll(option => option.Position >= optionIds.Count);
        for (var position = 0; position < optionIds.Count; position++)
        {
            var existing = _options.Find(option => option.Position == position);
            if (existing is null)
            {
                _options.Add(new ProductOption(new OptionDefinitionId(optionIds[position]), position));
            }
            else
            {
                existing.OptionDefinitionId = new OptionDefinitionId(optionIds[position]);
            }
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
    /// Reemplaza **sólo** las escalas de las variantes indicadas: los precios base y el resto de los
    /// datos maestros quedan como estaban. Es lo que necesita copiar las escalas de otra variante
    /// (spec 2026-10-20 §4).
    ///
    /// No pasa por <see cref="Update"/> a propósito. Ese verbo reemplaza el producto entero,
    /// así que el caso de uso tendría que reenviarle nombre, variantes y galería para no
    /// borrarlos — y un lote que arrastre mal cualquiera de esos campos los limpia en todos los
    /// destinos de una vez. Acá no hay nada que arrastrar.
    ///
    /// Recibe **todas** las variantes del producto que la copia toca de una vez, y no una por
    /// llamada, para que el producto cuente un solo cambio (una sola subida de <see cref="Version"/>)
    /// por más variantes que reciban escalas. Una variante que no es de este producto da
    /// <c>catalog.variant.not_found</c>.
    ///
    /// Y quedan **incompletas**: sin restricción, múltiplo, empaque ni agrupación
    /// (<see cref="PriceScale.CreateIncomplete"/>). Es el único camino que las produce.
    /// </summary>
    public void ApplyCopiedPriceScales(
        IReadOnlyDictionary<VariantId, IReadOnlyCollection<PriceScaleInput>> scalesByVariant,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(scalesByVariant);
        EnsureActive();

        // Primero resolver todas, después mutar: una variante ajena no deja a las anteriores
        // copiadas a medias en un agregado que el llamador aún podría guardar.
        var targets = new List<(ProductVariant Variant, IReadOnlyCollection<PriceScaleInput> Scales)>(
            scalesByVariant.Count);
        foreach (var (variantId, scales) in scalesByVariant)
        {
            var variant = _variants.Find(saved => saved.Id == variantId)
                ?? throw new CatalogDomainException(
                    "catalog.variant.not_found",
                    "The variant does not belong to this product.");
            targets.Add((variant, scales));
        }

        foreach (var (variant, scales) in targets)
        {
            variant.ApplyCopiedPriceScales(scales);
        }

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
    // No revalida la unicidad del codigo a proposito. IX_product_variants_tenant_code (antes
    // IX_products_tenant_code) es unico **sin filtro parcial**, asi que desactivar nunca libera el
    // codigo y reactivar no puede colisionar con nadie. Si alguien le agrega un filtro parcial al
    // indice, CA-CAT-07-09 se cae y avisa.
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
