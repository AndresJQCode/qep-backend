using BuildingBlocks.Application;
using FluentValidation;
using Modules.Purchases.Domain;
using Modules.Tenancy.Application;

namespace Modules.Purchases.Application;

/// <summary>
/// Comando y no query aunque no escribe: exige <c>purchases.order.manage</c> y corre las mismas reglas de escritura que el
/// alta, en el mismo orden.
/// </summary>
public sealed record PreviewPurchaseOrderCommand(
    Guid TenantId,
    Guid SupplierId,
    string Currency,
    string? Notes,
    IReadOnlyList<PurchaseOrderLineInput> Lines)
    : ICommand<PurchaseOrderPreviewDto>, IPurchaseOrderWriteCommand;

/// <summary>
/// La vista previa del formulario: el mismo armado de líneas y la misma aritmética que el alta (D-19). Sin repositorio, sin
/// transacción, sin número, sin auditoría y sin membresía: no deja rastro.
/// </summary>
public sealed class PreviewPurchaseOrderHandler(
    IExecutionContext executionContext,
    PurchaseOrderLineBuilder lineBuilder,
    IValidator<PreviewPurchaseOrderCommand> validator)
    : ICommandHandler<PreviewPurchaseOrderCommand, PurchaseOrderPreviewDto>
{
    public async Task<PurchaseOrderPreviewDto> HandleAsync(PreviewPurchaseOrderCommand command, CancellationToken cancellationToken)
    {
        PurchasesAuthorization.EnsureAuthorized(executionContext, command.TenantId, PurchasesPermissions.OrderManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var build = await lineBuilder.BuildAsync(command.TenantId, command, cancellationToken);

        var show = build.ShowPrices;
        var priced = build.Lines
            .Select(draft => (Draft: draft, Subtotal: PurchaseOrderMath.Subtotal(draft.Quantity, draft.UnitCost)))
            .ToList();
        var lines = priced
            .Select(pair => new PurchaseOrderPreviewLineDto(
                // Externa: la variante propia. Vinculada: la del mayorista, con su precio de lista (mayorista fase 3).
                pair.Draft.Listing?.SupplierVariantId ?? pair.Draft.VariantId!.Value,
                pair.Draft.Code,
                pair.Draft.Name,
                pair.Draft.TaxRatePercent,
                show ? pair.Subtotal : null,
                show && pair.Draft.Listing is { } listing
                    ? new PurchaseOrderListPriceDto(listing.BasePrice, listing.DiscountPercent, listing.UnitPrice)
                    : null))
            .ToList();
        var totals = PurchaseOrderMath.Sum(
            priced.Select(pair => (pair.Subtotal, PurchaseOrderMath.Tax(pair.Subtotal, pair.Draft.TaxRatePercent))));

        return new PurchaseOrderPreviewDto(lines, show ? PurchaseOrderComposer.ToTotals(totals) : null, build.Currency, show);
    }
}
