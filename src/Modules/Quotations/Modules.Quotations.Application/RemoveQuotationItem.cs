using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record RemoveQuotationItemCommand(
    Guid TenantId, Guid QuotationId, Guid ItemId) : ICommand<QuotationDto>;

public sealed class RemoveQuotationItemHandler(
    IQuotationRepository repository,
    IOrderRepository orderRepository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationCustomerLookup customerLookup,
    IQuotationProductLookup productLookup,
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

        // Mismo endpoint para dos pantallas (a pedido, 2026-09-15) — ver el comentario gemelo en
        // UpdateQuotationItemHandler.
        Order? order = null;
        if (quotation.Status == QuotationStatus.Converted)
        {
            order = await orderRepository.FindByQuotationIdAsync(
                command.TenantId, quotation.Id, cancellationToken)
                ?? throw OrderNotFound.For(command.QuotationId);

            if (order.Status != OrderStatus.Pending)
            {
                throw new QuotationsDomainException(
                    "order.order.not_pending",
                    "Products can only be removed while the order is pending.");
            }
        }

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
            ? new Dictionary<Guid, QuotationProductRef>()
            : await productLookup.FindManyAsync(
                command.TenantId, [removed.ProductId], cancellationToken);
        var productName = removed is not null
            && products.TryGetValue(removed.ProductId, out var product)
                ? product.Name
                : "un producto";

        var now = clock.UtcNow;
        // El propio agregado traduce un itemId desconocido a quotation.item.not_found.
        if (order is not null)
        {
            quotation.RemoveItemAfterConversion(new QuotationItemId(command.ItemId), updatedBy, now);
        }
        else
        {
            quotation.RemoveItem(new QuotationItemId(command.ItemId), updatedBy, now);
        }

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
            order is not null ? "quotation.order.item_removed" : "quotation.quotation.item_removed",
            order?.Id.ToString() ?? quotation.Id.ToString(),
            "success",
            now);

        order?.RecalculatePaymentStatus(quotation.Total, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return quotation.ToDto();
    }
}
