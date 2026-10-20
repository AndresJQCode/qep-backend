using BuildingBlocks.Application;
using FluentValidation;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

public sealed record UpdateProductCommand(
    Guid TenantId,
    Guid ProductId,
    long ExpectedVersion,
    string Name,
    string? Description,
    Guid? TaxRateId,
    IReadOnlyList<Guid>? OptionIds,
    IReadOnlyList<ProductVariantRequest>? Variants,
    IReadOnlyList<Guid>? Gallery,
    IReadOnlyList<Guid>? DeletedVariantIds = null) : ICommand<ProductDto>, IProductWriteCommand;

// Mismas reglas que el POST, por inclusión y no por copia. Ver ProductWriteRules. El precio puede
// faltar en una variante: ProductVariantChanges lo hereda (la suya, o la de la variante de la que
// nace).
public sealed class UpdateProductValidator : AbstractValidator<UpdateProductCommand>
{
    public UpdateProductValidator()
    {
        Include(new ProductWriteRules(pricingRequired: false));
    }
}

public sealed class UpdateProductHandler(
    IProductRepository repository,
    ITaxRateRepository taxRateRepository,
    IOptionDefinitionRepository optionRepository,
    IProductImageLookup imageLookup,
    IVariantUsageChecker usageChecker,
    ICatalogUnitOfWork unitOfWork,
    ICatalogAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateProductCommand> validator)
    : ICommandHandler<UpdateProductCommand, ProductDto>
{
    public async Task<ProductDto> HandleAsync(
        UpdateProductCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de validar. Ver la razón en CreateProductHandler.
        CatalogAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CatalogPermissions.ProductManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var product = await repository.FindAsync(
            command.TenantId, new ProductId(command.ProductId), cancellationToken)
            ?? throw ProductNotFound.For(command.ProductId);

        // El PUT reemplaza producto, variantes y galería enteros: con «gana el último que guarda»,
        // quien editó a la vez perdería en silencio sus variantes o sus fotos (Enfoque de revisión
        // 2). 412 concurrency.conflict, el mismo contrato que cotizaciones, notas y etiquetas; el
        // token de concurrencia de la fila cubre la carrera que queda entre esta lectura y el commit.
        if (product.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The product changed after it was loaded.");
        }

        var taxRateId = await ProductTaxRateResolver.ResolveAsync(
            taxRateRepository, command.TenantId, command.TaxRateId, cancellationToken);

        // CAT-05, igual que en el POST: sin esto un PUT puede meter en la galería un archivo de
        // otro tenant, que es la mitad del criterio CA-CAT-05-01.
        var gallery = command.Gallery ?? [];
        await ProductImageResolver.ResolveGalleryAsync(
            imageLookup, command.TenantId, gallery, cancellationToken);

        // Spec §3.3 y §6: borrar una variante es pedirlo por nombre en DeletedVariantIds. Una
        // guardada que sólo falta del cuerpo no se toca: el agregado la rechaza con
        // missing_in_request (con 2.048 filas paginadas, una página que no se mandó no es una orden
        // de borrar). La pedida se borra si ninguna línea la vende y se desactiva si alguna sí.
        var body = command.Variants!;
        var requestedIds = body
            .Where(variant => variant.Id is not null)
            .Select(variant => variant.Id!.Value)
            .ToHashSet();
        var listed = command.DeletedVariantIds ?? [];
        EnsureDeletable(product, listed, requestedIds);
        var listedIds = listed.ToHashSet();

        // Primera comprobación, antes de abrir la transacción.
        var usedBefore = await usageChecker.FindUsedAsync(command.TenantId, listedIds, cancellationToken);
        var deletedIds = listedIds.Where(id => !usedBefore.Contains(id)).ToHashSet();

        // La segunda comprobación y el borrado van en la misma transacción (Enfoque de revisión 3):
        // el adaptador corre sobre la conexión de Catalog, así que su consulta queda adentro. Una
        // línea que apareció entre las dos comprobaciones convierte el borrado en desactivación:
        // nunca una línea con variant_id huérfano. Lo que queda abierto —una línea que se guarda
        // después de esta consulta y antes del commit— está en el reporte de la Tarea 6.
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        if (deletedIds.Count > 0)
        {
            var usedNow = await usageChecker.FindUsedAsync(command.TenantId, deletedIds, cancellationToken);
            deletedIds.ExceptWith(usedNow);
        }

        // Las pedidas que están en uso siguen, inactivas. Van detrás del cuerpo mientras se validan
        // —así Variants[i] es la fila i del cuerpo— y vuelven a su posición antes de aplicar.
        var kept = product.Variants
            .Where(variant => listedIds.Contains(variant.Id.Value) && !deletedIds.Contains(variant.Id.Value))
            .ToArray();
        var requests = body.Concat(kept.Select(variant => KeptInactive(product, variant, gallery))).ToArray();

        var optionIds = command.OptionIds ?? [];
        // Las de antes también: proyectar los valores guardados necesita saber qué opción era cada
        // posición, y una opción que se quita no viene en el cuerpo.
        var library = await ProductOptionLibrary.LoadAsync(
            optionRepository, command.TenantId, optionIds.Concat(product.OptionIds), cancellationToken);

        IReadOnlyList<ProductVariantInput> variants;
        try
        {
            variants = ProductVariantChanges.Resolve(product, optionIds, requests, library);

            ProductVariantReferences.EnsureValid(product, optionIds, variants, library);
            // Un intercambio de códigos entre dos variantes del mismo producto (A toma el de B y B el
            // de A) pasa esta comprobación —los dos códigos son del propio producto— pero choca con
            // IX_product_variants_tenant_code al guardar, porque PostgreSQL revisa el índice fila por
            // fila y el índice no es diferible. Sale 422 catalog.product.code_taken aunque el estado
            // final sea válido. No se resuelve acá (Tarea 3): el usuario lo hace en dos guardados.
            await ProductVariantReferences.EnsureCodesAvailableAsync(
                repository, command.TenantId, product, variants, cancellationToken);
        }
        catch (CatalogDomainException exception) when (kept.Length > 0 && exception.FieldErrors.Count > 0)
        {
            throw PointAtDeletedVariantIds(exception, body.Count, kept, listed);
        }

        var now = clock.UtcNow;

        // Before Update, not after: the variants' pricing is overwritten there, so once applied the
        // old value exists nowhere to recover it from. The rows join the change tracker and travel
        // in the same SaveChangesAsync below — the history and the product are saved together or
        // not at all. Por variante: cada fila lleva la variante cuyo precio cambió.
        repository.AddPriceChanges(ProductPriceChangeDetector.Detect(
            product, variants, executionContext.SubjectId, now));

        // Los campos opcionales se mandan siempre, incluidos los null: el PUT reemplaza el recurso
        // entero, así que un campo ausente se limpia. Es lo que verifica CA-CAT-04-03.
        product.Update(
            command.Name,
            new ProductDetails
            {
                Description = command.Description,
                TaxRateId = taxRateId
            },
            optionIds,
            InOriginalPositions(variants, body.Count, kept),
            gallery,
            now,
            deletedIds);

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "catalog.product.updated",
            product.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var dtos = await new[] { product }.ToDtosAsync(
            imageLookup, optionRepository, usageChecker, command.TenantId, cancellationToken);
        return dtos[0];
    }

    // DeletedVariantIds sólo nombra variantes guardadas de este producto que no vienen en el cuerpo.
    // El agregado repite las dos reglas; acá van antes porque un id que no es del producto nunca
    // llegaría al agregado (no hay variante que borrar ni que desactivar) y se perdería en silencio.
    private static void EnsureDeletable(Product product, IReadOnlyList<Guid> listed, HashSet<Guid> requestedIds)
    {
        var saved = product.Variants.Select(variant => variant.Id.Value).ToHashSet();
        var seen = new HashSet<Guid>();
        foreach (var id in listed)
        {
            if (!saved.Contains(id))
            {
                throw new CatalogDomainException(
                    "catalog.variant.not_found",
                    "The variant does not belong to this product.");
            }

            if (requestedIds.Contains(id) || !seen.Add(id))
            {
                throw new CatalogDomainException(
                    "catalog.variant.duplicated_in_request",
                    "The same variant cannot come twice in the request.");
            }
        }
    }

    // Una pedida para borrar que está en uso: se queda, inactiva, con todo lo suyo. Sus valores y su
    // precio van en null para que ProductVariantChanges los resuelva igual que en una guardada que
    // vino (proyectados a las opciones nuevas, el precio como está). La foto se conserva si sigue en
    // la galería del cuerpo; si no, se limpia, como cuando se quita la foto de la galería.
    private static ProductVariantRequest KeptInactive(
        Product product, ProductVariant variant, IReadOnlyList<Guid> gallery)
    {
        var fileId = variant.GalleryImageId is { } imageId
            ? product.Gallery.FirstOrDefault(image => image.Id == imageId)?.FileId
            : null;

        return new ProductVariantRequest(
            variant.Id.Value,
            variant.Code,
            variant.Barcode,
            OptionValueIds: null,
            Pricing: null,
            GalleryFileId: fileId is { } id && gallery.Contains(id) ? id : null,
            IsActive: false);
    }

    // Las desactivadas vuelven a la posición que tenían (si el cuerpo es más corto, al final): el
    // formulario no las mandó y no tiene por qué verlas saltar. El agregado numera por el orden de
    // esta lista.
    private static List<ProductVariantInput> InOriginalPositions(
        IReadOnlyList<ProductVariantInput> variants, int bodyCount, ProductVariant[] kept)
    {
        var ordered = variants.Take(bodyCount).ToList();
        foreach (var (variant, input) in kept
            .Select((variant, index) => (variant, variants[bodyCount + index]))
            .OrderBy(pair => pair.variant.Position))
        {
            ordered.Insert(Math.Min(variant.Position, ordered.Count), input);
        }

        return ordered;
    }

    // Un error de una desactivada (p. ej. una opción nueva sin valores activos para proyectarle)
    // saldría en Variants[i] con i fuera del cuerpo: la pantalla no tendría fila que marcar. Se
    // marca sobre DeletedVariantIds[k], la entrada que el formulario sí mandó.
    private static CatalogDomainException PointAtDeletedVariantIds(
        CatalogDomainException exception,
        int bodyCount,
        ProductVariant[] kept,
        IReadOnlyList<Guid> listed)
    {
        var prefix = $"{nameof(IProductWriteCommand.Variants)}[";
        var fieldErrors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (field, messages) in exception.FieldErrors)
        {
            var key = field;
            if (field.StartsWith(prefix, StringComparison.Ordinal)
                && field.IndexOf(']', prefix.Length) is var close and > 0
                && int.TryParse(field.AsSpan(prefix.Length, close - prefix.Length), out var index)
                && index >= bodyCount
                && index - bodyCount < kept.Length)
            {
                var position = IndexOf(listed, kept[index - bodyCount].Id.Value);
                key = $"{nameof(UpdateProductCommand.DeletedVariantIds)}[{position}]";
            }

            fieldErrors[key] = fieldErrors.TryGetValue(key, out var previous) ? [.. previous, .. messages] : messages;
        }

        return new CatalogDomainException(exception.Code, exception.Message, fieldErrors);
    }

    private static int IndexOf(IReadOnlyList<Guid> ids, Guid id)
    {
        for (var index = 0; index < ids.Count; index++)
        {
            if (ids[index] == id)
            {
                return index;
            }
        }

        return -1;
    }
}
