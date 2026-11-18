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

        var lines = build.Lines
            .Select(draft => new PurchaseOrderPreviewLineDto(
                // Externa: la variante propia. Vinculada: la del mayorista, con su precio de lista (mayorista fase 3).
                draft.Listing?.SupplierVariantId ?? draft.VariantId!.Value,
                draft.Code,
                draft.Name,
                draft.TaxRatePercent,
                PurchaseOrderMath.Subtotal(draft.Quantity, draft.UnitCost),
                draft.Listing is { } listing
                    ? new PurchaseOrderListPriceDto(listing.BasePrice, listing.DiscountPercent, listing.UnitPrice)
                    : null))
            .ToList();
        var totals = PurchaseOrderMath.Sum(
            lines.Select(line => (line.Subtotal, PurchaseOrderMath.Tax(line.Subtotal, line.TaxRatePercent))));

        return new PurchaseOrderPreviewDto(lines, PurchaseOrderComposer.ToTotals(totals), build.Currency);
    }
}
