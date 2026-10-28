using BuildingBlocks.Application;
using FluentValidation;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

/// <summary>
/// Copia el juego completo de escalas de **una variante** a varias otras variantes, de uno o de
/// varios productos, en una sola operación (spec 2026-10-20 §4).
///
/// Reemplaza al lote que armaba el cliente, que por cada destino hacía un `GET` y un
/// `PUT /products/{id}` con el producto entero. Eso tenía tres problemas que no se arreglan del
/// lado del navegador:
///
/// <list type="number">
/// <item>**No era atómico.** Cada destino era su propia transacción, así que el lote podía
/// terminar a mitad de camino y no había forma de reintentarlo sin volver a escribir los que ya
/// habían salido bien.</item>
/// <item>**Arrastraba el precio final del origen**, que el dominio valida contra el precio base
/// de la variante dueña: todo destino con otro precio base rechazaba la copia. Ver
/// <see cref="PriceScaleCopy"/>.</item>
/// <item>**Mandaba el producto completo** para cambiarle las escalas, así que cualquier campo
/// que el cliente reconstruyera mal se perdía en todos los destinos a la vez.</item>
/// </list>
///
/// El reemplazo es total y no aditivo: cada destino termina con las escalas del origen y ninguna
/// otra. Es lo que la pantalla viene avisando ("las escalas que copies reemplazan a las
/// actuales").
/// </summary>
public sealed record CopyPriceScalesCommand(
    Guid TenantId,
    Guid SourceVariantId,
    IReadOnlyList<Guid> TargetVariantIds) : ICommand<IReadOnlyList<ProductDto>>;

public static class PriceScaleCopyLimits
{
    /// <summary>
    /// Tope de destinos por llamada, **contados en variantes** (un producto con 30 tallas son 30).
    /// Mismo número que <c>ProductPaging.MaxPageSize</c> y por la misma razón: es lo máximo que la
    /// pantalla puede tener marcado a la vez sin paginar dos veces, y un lote sin tope convierte un
    /// `POST` en una transacción arbitrariamente larga.
    /// </summary>
    public const int MaxTargets = 200;
}

public sealed class CopyPriceScalesValidator : AbstractValidator<CopyPriceScalesCommand>
{
    public CopyPriceScalesValidator()
    {
        RuleFor(command => command.SourceVariantId)
            .NotEmpty().WithMessage("Elige la variante de la que se copian las escalas.");

        RuleFor(command => command.TargetVariantIds)
            // Cascade.Stop: sin esto las reglas de abajo corren igual sobre una lista nula y el
            // 422 sale con un error real y una NullReferenceException al lado.
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Elige al menos una variante de destino.")
            .Must(ids => ids.Count <= PriceScaleCopyLimits.MaxTargets)
                .WithMessage(
                    $"Puedes copiar a lo sumo a {PriceScaleCopyLimits.MaxTargets} variantes a la vez.")
            .Must(ids => ids.All(id => id != Guid.Empty))
                .WithMessage("Una variante de destino no puede venir vacía.")
            // Un id repetido copiaría dos veces sobre la misma variante: inofensivo en el
            // resultado, pero delata que el cliente armó mal la lista y el conteo que devuelve la
            // respuesta no coincidiría con lo que pidió.
            .Must(ids => ids.Distinct().Count() == ids.Count)
                .WithMessage("Las variantes de destino no pueden repetirse.")
            .Must((command, ids) => !ids.Contains(command.SourceVariantId))
                .WithMessage("La variante de origen no puede ser también un destino.");
    }
}

public sealed class CopyPriceScalesHandler(
    IProductRepository repository,
    IOptionDefinitionRepository optionRepository,
    IProductImageLookup imageLookup,
    IVariantUsageChecker usageChecker,
    ICatalogUnitOfWork unitOfWork,
    ICatalogAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<CopyPriceScalesCommand> validator)
    : ICommandHandler<CopyPriceScalesCommand, IReadOnlyList<ProductDto>>
{
    public async Task<IReadOnlyList<ProductDto>> HandleAsync(
        CopyPriceScalesCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de validar. Ver la razón en CreateProductHandler.
        CatalogAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CatalogPermissions.ProductManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var sourceId = new VariantId(command.SourceVariantId);
        var targetIds = command.TargetVariantIds.Select(id => new VariantId(id)).ToArray();

        // Una sola consulta con seguimiento para el origen y todos los destinos: los productos que
        // los contienen, cada uno con todas sus variantes. El origen y un destino pueden ser del
        // mismo producto, y entonces es el mismo objeto del mismo grafo.
        var products = await repository.FindVariantsByIdsForUpdateAsync(
            command.TenantId, [sourceId, .. targetIds], cancellationToken);

        var variantsById = products
            .SelectMany(product => product.Variants.Select(variant => (Product: product, Variant: variant)))
            .ToDictionary(pair => pair.Variant.Id);

        var source = variantsById.TryGetValue(sourceId, out var sourcePair)
            ? sourcePair.Variant
            : throw VariantNotFound.For(command.SourceVariantId);

        // Un origen sin escalas no es una copia vacía: es un borrado masivo disfrazado, porque el
        // reemplazo dejaría a cada destino sin las suyas. Nadie abre esta pantalla para eso.
        if (source.PriceScales.Count == 0)
        {
            throw new CatalogDomainException(
                "catalog.product.price_scale_copy.source_without_scales",
                "The source variant has no price scales to copy.");
        }

        // Un id que no está tumba el lote entero en vez de saltearse en silencio: el cliente
        // marcó N variantes y espera N copias, y un destino que desapareció es exactamente el
        // caso donde seguir de largo deja a alguien creyendo que copió algo que no copió.
        var targets = new List<(Product Product, ProductVariant Variant)>(targetIds.Length);
        foreach (var targetId in targetIds)
        {
            if (!variantsById.TryGetValue(targetId, out var target))
            {
                throw VariantNotFound.For(targetId.Value);
            }

            targets.Add((target.Product, target.Variant));
        }

        var now = clock.UtcNow;

        // Por producto y en el orden en que aparece su primera variante de destino: el agregado
        // cuenta un solo cambio por producto por más variantes que reciban escalas.
        var scalesByProduct = new Dictionary<Product, Dictionary<VariantId, IReadOnlyCollection<PriceScaleInput>>>();
        var productsInOrder = new List<Product>();
        foreach (var (product, variant) in targets)
        {
            var pricing = PriceScaleCopy.ToPricingFor(source, variant);

            // Antes de aplicar, igual que en UpdateProductHandler: después del reemplazo el
            // descuento viejo no existe en ningún lado desde donde recuperarlo. Las filas viajan
            // en el mismo SaveChangesAsync de más abajo, una por variante que cambió; una variante
            // cuyas escalas ya eran las del origen no deja ninguna.
            repository.AddPriceChanges(ProductPriceChangeDetector.DetectForVariant(
                product, variant, pricing, executionContext.SubjectId, now));

            if (!scalesByProduct.TryGetValue(product, out var scalesByVariant))
            {
                scalesByVariant = [];
                scalesByProduct.Add(product, scalesByVariant);
                productsInOrder.Add(product);
            }

            scalesByVariant.Add(variant.Id, pricing.Scales);
        }

        foreach (var product in productsInOrder)
        {
            product.ApplyCopiedPriceScales(scalesByProduct[product], now);

            // Una entrada por producto tocado y no una por lote: la auditoría se lee por recurso,
            // y un solo evento con N ids adentro no aparece cuando alguien busca qué le pasó a un
            // producto. El recurso es el producto, como en product.updated, y no la variante: la
            // pantalla de auditoría no sabe de variantes y el histórico de precios ya dice cuál.
            // Acción propia, distinta de product.updated, porque el disparador es distinto y el
            // reporte de cambios de precio ya distingue los dos.
            auditPublisher.Publish(
                command.TenantId,
                executionContext.SubjectId,
                "catalog.product.price_scales_copied",
                product.Id.ToString(),
                "success",
                now);
        }

        // Un solo commit para el lote entero: o copia a todos o no copia a ninguno. Es la
        // diferencia con el lote del cliente, que dejaba mitades escritas.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Los productos tocados, cada uno una vez y en el orden en que aparece su primera variante
        // de destino en la petición (no en el que los trajo PostgreSQL, que no está garantizado).
        // canDelete de cada variante sale de una consulta por lote al IVariantUsageChecker.
        return await productsInOrder
            .ToDtosAsync(imageLookup, optionRepository, usageChecker, command.TenantId, cancellationToken);
    }
}
