using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;

namespace Modules.Wholesale.Application;

/// <summary>
/// Comando y no query aunque no escribe: exige <c>catalog.price_list.manage</c> (spec 2026-11-22) y corre las reglas de términos de
/// la lista (sin el nombre), como <c>PreviewPurchaseOrderCommand</c>.
/// </summary>
public sealed record PreviewPriceListCommand(
    Guid TenantId,
    decimal AdjustmentPercent,
    string Visibility,
    IReadOnlyList<Guid> CategoryIds,
    IReadOnlyList<PriceListExclusionInput>? Exclusions = null)
    : ICommand<PriceListPreviewDto>;

/// <summary>
/// «Así lo ve el distribuidor» (D-10, D-11): hasta 20 variantes vendibles en la moneda por defecto del mayorista, con su
/// precio de lista redondeado con la regla del mayorista para esa moneda. Una categoría que ya no existe se cae en silencio
/// (D-23): la vista previa no escribe, así que no hay nada que rechazar. Sin repositorio, sin auditoría y sin unidad de
/// trabajo: no deja rastro.
/// </summary>
public sealed class PreviewPriceListHandler(
    IExecutionContext executionContext,
    ITenantDefaultCurrency defaultCurrency,
    IWholesalePriceLists priceLists,
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
        if (PreviewTerms.IsCategories(command.Visibility))
        {
            var found = await catalog.FindCategoriesAsync(command.TenantId, command.CategoryIds, cancellationToken);
            categoryIds = command.CategoryIds.Where(found.ContainsKey).ToArray();
            if (categoryIds.Length == 0)
            {
                // Todas borradas: la lista no ve nada. Sin esto el lector recibiría un filtro vacío.
                return new PriceListPreviewDto(currency, 0, []);
            }
        }

        var exclusions = command.Exclusions ?? [];
        var hiddenCategories = exclusions
            .Where(item => PreviewTerms.TryParseKind(item.Kind, out var isCategory) && isCategory)
            .Select(item => item.Id)
            .ToArray();
        var hiddenProducts = exclusions
            .Where(item => PreviewTerms.TryParseKind(item.Kind, out var isCategory) && !isCategory)
            .Select(item => item.Id)
            .ToArray();
        var (items, totalProducts) = await catalog.SampleAsync(
            command.TenantId, currency, categoryIds, hiddenCategories, hiddenProducts, SampleSize, cancellationToken);

        // Spec 2026-11-20: cuántos esconden las exclusiones = el total del alcance sin ellas menos el total con ellas. Sólo se
        // pregunta con exclusiones; sin ellas la vista previa cuesta lo de siempre.
        var hidden = 0;
        if (exclusions.Count > 0)
        {
            var (_, unfilteredTotal) = await catalog.SampleAsync(
                command.TenantId, currency, categoryIds, [], [], take: 1, cancellationToken);
            hidden = Math.Max(0, unfilteredTotal - totalProducts);
        }

        // Una lista sin guardar y sin precios fijos: el precio sale de la misma regla de Catalog que usa el distribuidor (D-2).
        var unsaved = new WholesalePriceList(default, string.Empty, command.AdjustmentPercent, categoryIds, hiddenCategories,
            hiddenProducts, ShowPrices: true);
        var prices = await priceLists.PriceAsync(
            command.TenantId, unsaved, currency, items.ToDictionary(item => item.VariantId, item => item.BasePrice), cancellationToken);

        return new PriceListPreviewDto(
            currency,
            totalProducts,
            items
                .Select(item => new PriceListPreviewItemDto(
                    item.VariantId,
                    item.Code,
                    item.Name,
                    item.BasePrice,
                    prices.TryGetValue(item.VariantId, out var listed) ? listed.Price : item.BasePrice))
                .ToList(),
            hidden);
    }
}
