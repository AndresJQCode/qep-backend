using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record RemoveQuotationItemCommand(
    Guid TenantId, Guid QuotationId, Guid ItemId) : ICommand<QuotationDto>;

// Spec 2026-10-15 §4.4: una cotización Converted queda congelada; quitar líneas de un pedido
// pendiente se hace sobre el pedido (SaveOrderEdits). Ver el comentario gemelo en
// UpdateQuotationItemHandler.
public sealed class RemoveQuotationItemHandler(
    IQuotationRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationCustomerLookup customerLookup,
    IQuotationProductLookup productLookup,
    IQuotationProductPricingLookup pricingLookup,
    IQuotationSettingsStore settingsStore,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<RemoveQuotationItemCommand, QuotationDto>
{
    public async Task<QuotationDto> HandleAsync(
        RemoveQuotationItemCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, QuotationsPermissions.QuotationManage);

        var quotation = await repository.FindAsync(
            command.TenantId, new QuotationId(command.QuotationId), cancellationToken)
            ?? throw QuotationNotFound.For(command.QuotationId);

        // Deja la retención/excedente de IVA al día con el cliente maestro antes de recalcular
        // — ver Quotation.RefreshCustomerTaxProfile.
        var customer = await customerLookup.FindAsync(
            command.TenantId, quotation.ClientId, cancellationToken);
        if (customer is not null)
        {
            quotation.RefreshCustomerTaxProfile(customer.WithRetention, customer.VatSurplus);
        }

        var updatedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        // Para el resumen del historial: el nombre se resuelve **antes** de quitar la línea,
        // que es lo único que sabe a qué producto apuntaba.
        var removed = quotation.Items.FirstOrDefault(item => item.Id.Value == command.ItemId);
        var products = removed is null
            ? new Dictionary<Guid, QuotationVariantRef>()
            : await productLookup.FindManyAsync(
                command.TenantId, [removed.VariantId], cancellationToken);
        var productName = removed is not null
            && products.TryGetValue(removed.VariantId, out var product)
                ? product.Name
                : "un producto";

        var now = clock.UtcNow;
        // El propio agregado traduce un itemId desconocido a quotation.item.not_found.
        quotation.RemoveItem(new QuotationItemId(command.ItemId), updatedBy, now);

        repository.AddHistoryEntry(QuotationHistoryEntry.Create(
            QuotationHistoryEntryId.New(),
            quotation.Id,
            QuotationHistoryEventType.Edited,
            updatedBy,
            QuotationChangeSummary.ItemRemoved(productName),
            now));
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.quotation.item_removed",
            quotation.Id.ToString(),
            "success",
            now);

        // Quitar una línea también mueve el descuento de las que quedan: se va su cantidad del
        // total del grupo, y puede caerse el mínimo de compra de la cotización entera. Va antes de
        // mirar el total.
        await QuotationPricingRecalculation.ApplyAsync(
            pricingLookup, settingsStore, command.TenantId, quotation, now, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return quotation.ToDto();
    }
}
