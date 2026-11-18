using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wholesale.Domain;

namespace Modules.Wholesale.Application;

/// <summary>
/// Comando y no query aunque no escribe: exige <c>wholesale.price_list.manage</c> y corre las mismas reglas de términos que el
/// alta (sin el nombre), como <c>PreviewPurchaseOrderCommand</c>.
/// </summary>
public sealed record PreviewPriceListCommand(
    Guid TenantId,
    decimal DiscountPercent,
    string Visibility,
    IReadOnlyList<Guid> CategoryIds)
    : ICommand<PriceListPreviewDto>, IPriceListTermsCommand;

/// <summary>
/// «Así lo ve el distribuidor» (D-10, D-11): hasta 20 variantes vendibles en la moneda por defecto del mayorista, con su
/// precio de lista redondeado con la regla del mayorista para esa moneda. Una categoría que ya no existe se cae en silencio
/// (D-23): la vista previa no escribe, así que no hay nada que rechazar. Sin repositorio, sin auditoría y sin unidad de
/// trabajo: no deja rastro.
/// </summary>
public sealed class PreviewPriceListHandler(
    IExecutionContext executionContext,
    ITenantDefaultCurrency defaultCurrency,
    ITenantPriceRounding priceRounding,
    IWholesaleCatalogReader catalog,
    IValidator<PreviewPriceListCommand> validator)
    : ICommandHandler<PreviewPriceListCommand, PriceListPreviewDto>
{
    internal const int SampleSize = 20;

    public async Task<PriceListPreviewDto> HandleAsync(PreviewPriceListCommand command, CancellationToken cancellationToken)
    {
        WholesaleAuthorization.EnsureAuthorized(executionContext, command.TenantId, WholesalePermissions.PriceListManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var currency = await defaultCurrency.GetAsync(command.TenantId, cancellationToken);
        Guid[]? categoryIds = null;
        if (PriceListTerms.VisibilityOf(command) == PriceListVisibility.Categories)
        {
            var found = await catalog.FindCategoriesAsync(command.TenantId, command.CategoryIds, cancellationToken);
            categoryIds = command.CategoryIds.Where(found.ContainsKey).ToArray();
            if (categoryIds.Length == 0)
            {
                // Todas borradas: la lista no ve nada. Sin esto el lector recibiría un filtro vacío.
                return new PriceListPreviewDto(currency, 0, []);
            }
        }

        var rounding = await priceRounding.GetAsync(command.TenantId, currency, cancellationToken);
        var (items, totalProducts) = await catalog.SampleAsync(
            command.TenantId, currency, categoryIds, SampleSize, cancellationToken);

        return new PriceListPreviewDto(
            currency,
            totalProducts,
            items
                .Select(item => new PriceListPreviewItemDto(
                    item.VariantId,
                    item.Code,
                    item.Name,
                    item.BasePrice,
                    WholesalePricing.ListPrice(item.BasePrice, command.DiscountPercent, rounding)))
                .ToList());
    }
}
