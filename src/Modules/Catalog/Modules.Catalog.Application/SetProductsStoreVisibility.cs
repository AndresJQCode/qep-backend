using BuildingBlocks.Application;
using FluentValidation;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Application;

/// <summary>
/// Publica u oculta en la tienda un lote de productos de una vez (spec tienda pública §B). Todo o
/// nada: un id desconocido o de otro tenant tumba el lote con 404 y no cambia ninguna fila.
/// </summary>
public sealed record SetProductsStoreVisibilityCommand(
    Guid TenantId,
    IReadOnlyList<Guid> ProductIds,
    bool Visible) : ICommand<ProductStoreVisibilityResult>;

public sealed record ProductStoreVisibilityResult(IReadOnlyList<ProductStoreVisibilityItem> Items);

public sealed record ProductStoreVisibilityItem(Guid Id, bool VisibleInStore, long Version);

public static class ProductStoreVisibilityLimits
{
    /// <summary>Mismo tope que la copia de escalas y la página del listado: lo máximo que la grilla
    /// puede tener marcado a la vez.</summary>
    public const int MaxProducts = 200;
}

public sealed class SetProductsStoreVisibilityValidator : AbstractValidator<SetProductsStoreVisibilityCommand>
{
    public SetProductsStoreVisibilityValidator()
    {
        RuleFor(command => command.ProductIds)
            // Cascade.Stop: sin esto las reglas de abajo corren también sobre una lista nula.
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Elige al menos un producto.")
            .Must(ids => ids.Count <= ProductStoreVisibilityLimits.MaxProducts)
                .WithMessage($"Puedes cambiar a lo sumo {ProductStoreVisibilityLimits.MaxProducts} productos a la vez.")
            .Must(ids => ids.All(id => id != Guid.Empty))
                .WithMessage("Un producto no puede venir vacío.")
            .Must(ids => ids.Distinct().Count() == ids.Count)
                .WithMessage("Los productos no pueden repetirse.");
    }
}

public sealed class SetProductsStoreVisibilityHandler(
    IProductRepository repository,
    ICatalogUnitOfWork unitOfWork,
    ICatalogAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<SetProductsStoreVisibilityCommand> validator)
    : ICommandHandler<SetProductsStoreVisibilityCommand, ProductStoreVisibilityResult>
{
    public async Task<ProductStoreVisibilityResult> HandleAsync(
        SetProductsStoreVisibilityCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de validar. Ver la razón en CreateProductHandler.
        CatalogAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, CatalogPermissions.ProductManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var products = await repository.FindManyForUpdateAsync(
            command.TenantId,
            command.ProductIds.Select(id => new ProductId(id)).ToArray(),
            cancellationToken);

        // Tumba el lote entero con el primer id que falta, en el orden de la petición: marcar N y que
        // salgan N-1 deja a quien lo pidió creyendo que publicó lo que no publicó.
        var byId = products.ToDictionary(product => product.Id.Value);
        foreach (var id in command.ProductIds)
        {
            if (!byId.ContainsKey(id))
            {
                throw ProductNotFound.For(id);
            }
        }

        var now = clock.UtcNow;
        foreach (var id in command.ProductIds)
        {
            var product = byId[id];

            // Una entrada de auditoría por producto que cambió, y ninguna por el que ya estaba así:
            // la auditoría se lee por recurso.
            if (product.SetStoreVisibility(command.Visible, now))
            {
                auditPublisher.Publish(
                    command.TenantId,
                    executionContext.SubjectId,
                    "catalog.product.store_visibility_changed",
                    product.Id.ToString(),
                    "success",
                    now);
            }
        }

        // Un solo commit: o cambia a todos o a ninguno.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new ProductStoreVisibilityResult(
            command.ProductIds
                .Select(id => new ProductStoreVisibilityItem(id, byId[id].VisibleInStore, byId[id].Version))
                .ToArray());
    }
}
