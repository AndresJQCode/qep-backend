using BuildingBlocks.Application;
using FluentValidation;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

/// <summary>
/// Copia el juego completo de escalas de un producto a varios otros, en una sola operación.
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
/// del producto dueño: todo destino con otro precio base rechazaba la copia. Ver
/// <see cref="PriceScaleCopy"/>.</item>
/// <item>**Mandaba el producto completo** para cambiarle las escalas, así que cualquier campo
/// que el cliente reconstruyera mal se perdía en todos los destinos a la vez.</item>
/// </list>
///
/// El reemplazo es total y no aditivo: el destino termina con las escalas del origen y ninguna
/// otra. Es lo que la pantalla viene avisando ("las escalas que copies reemplazan a las
/// actuales").
/// </summary>
public sealed record CopyPriceScalesCommand(
    Guid TenantId,
    Guid SourceProductId,
    IReadOnlyList<Guid> TargetProductIds) : ICommand<IReadOnlyList<ProductDto>>;

public static class PriceScaleCopyLimits
{
    /// <summary>
    /// Tope de destinos por llamada. Mismo número que <c>ProductPaging.MaxPageSize</c> y por la
    /// misma razón: es lo máximo que el listado puede tener marcado a la vez sin paginar dos
    /// veces, y un lote sin tope convierte un `POST` en una transacción arbitrariamente larga.
    /// </summary>
    public const int MaxTargets = 200;
}

public sealed class CopyPriceScalesValidator : AbstractValidator<CopyPriceScalesCommand>
{
    public CopyPriceScalesValidator()
    {
        RuleFor(command => command.SourceProductId).NotEmpty();

        RuleFor(command => command.TargetProductIds)
            // Cascade.Stop: sin esto las reglas de abajo corren igual sobre una lista nula y el
            // 422 sale con un error real y una NullReferenceException al lado.
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(ids => ids.Count <= PriceScaleCopyLimits.MaxTargets)
                .WithMessage(
                    $"No more than {PriceScaleCopyLimits.MaxTargets} target products are allowed.")
            .Must(ids => ids.All(id => id != Guid.Empty))
                .WithMessage("A target product id cannot be empty.")
            // Un id repetido copiaría dos veces sobre el mismo producto: inofensivo en el
            // resultado, pero delata que el cliente armó mal la lista y el conteo que devuelve la
            // respuesta no coincidiría con lo que pidió.
            .Must(ids => ids.Distinct().Count() == ids.Count)
                .WithMessage("The target products cannot repeat.")
            .Must((command, ids) => !ids.Contains(command.SourceProductId))
                .WithMessage("The source product cannot also be a target.");
    }
}

public sealed class CopyPriceScalesHandler(
    IProductRepository repository,
    IProductImageLookup imageLookup,
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

        var source = await repository.FindAsync(
            command.TenantId, new ProductId(command.SourceProductId), cancellationToken)
            ?? throw ProductNotFound.For(command.SourceProductId);

        // Un origen sin escalas no es una copia vacía: es un borrado masivo disfrazado, porque el
        // reemplazo dejaría a cada destino sin las suyas. Nadie abre esta pantalla para eso.
        if (source.PriceScales.Count == 0)
        {
            throw new CatalogDomainException(
                "catalog.product.price_scale_copy.source_without_scales",
                "The source product has no price scales to copy.");
        }

        var targetIds = command.TargetProductIds.Select(id => new ProductId(id)).ToArray();

        // Una sola consulta para todos los destinos, con seguimiento. La versión anterior hacía
        // un GET por destino desde el navegador.
        var targets = await repository.ListByIdsForUpdateAsync(
            command.TenantId, targetIds, cancellationToken);

        // Un id que no está tumba el lote entero en vez de saltearse en silencio: el cliente
        // marcó N productos y espera N copias, y un destino que desapareció es exactamente el
        // caso donde seguir de largo deja a alguien creyendo que copió algo que no copió.
        if (targets.Count != targetIds.Length)
        {
            var found = targets.Select(target => target.Id).ToHashSet();
            throw ProductNotFound.For(
                targetIds.First(id => !found.Contains(id)).Value);
        }

        var now = clock.UtcNow;

        foreach (var target in targets)
        {
            var pricing = PriceScaleCopy.ToPricingFor(source, target);

            // Antes de aplicar, igual que en UpdateProductHandler: después del reemplazo el
            // descuento viejo no existe en ningún lado desde donde recuperarlo. Las filas viajan
            // en el mismo SaveChangesAsync de más abajo.
            repository.AddPriceChanges(ProductPriceChangeDetector.Detect(
                target, pricing, executionContext.SubjectId, now));

            target.ApplyPriceScales(pricing.Scales, now);

            // Una entrada por destino y no una por lote: la auditoría se lee por recurso, y un
            // solo evento con N ids adentro no aparece cuando alguien busca qué le pasó a un
            // producto. Acción propia, distinta de product.updated, porque el disparador es
            // distinto y el reporte de cambios de precio ya distingue los dos.
            auditPublisher.Publish(
                command.TenantId,
                executionContext.SubjectId,
                "catalog.product.price_scales_copied",
                target.Id.ToString(),
                "success",
                now);
        }

        // Un solo commit para el lote entero: o copia a todos o no copia a ninguno. Es la
        // diferencia con el lote del cliente, que dejaba mitades escritas.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Devueltos en el orden en que los pidieron y no en el que los trajo PostgreSQL, que no
        // está garantizado.
        var byId = targets.ToDictionary(target => target.Id);
        return await targetIds
            .Select(id => byId[id])
            .ToDtosAsync(imageLookup, command.TenantId, cancellationToken);
    }
}
