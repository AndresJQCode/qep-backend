using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// Guarda de una vez todo lo que el editor de la cotización cambió —encabezado y productos— en una
/// sola escritura. <paramref name="ExpectedVersion"/> es el <c>If-Match</c>: el
/// <c>Quotation.Version</c> que la pantalla cargó.
///
/// Convive con <c>PATCH /quotations/{quotationId}</c> y con los endpoints de línea, que siguen
/// intactos: el frontend migra cuando quiera.
/// </summary>
public sealed record SaveQuotationCommand(
    Guid TenantId,
    Guid QuotationId,
    long ExpectedVersion,
    DateOnly? ValidUntil,
    string? PaymentMethod,
    string? Notes,
    QuotationPartiesRequest? Parties,
    QuotationBillingAccountRequest? BillingAccount,
    IReadOnlyList<QuotationItemAddition> Items) : ICommand<QuotationDto>, IQuotationEdits;

public sealed class SaveQuotationValidator : QuotationEditsValidator<SaveQuotationCommand>;

/// <summary>
/// Todo sobre el agregado rastreado y un único <c>SaveChangesAsync</c>, así que una falla en
/// cualquier paso no deja nada a medio guardar — antes, guardar el encabezado y tres cambios de
/// línea eran cuatro viajes a la base, cada uno con su propia versión y su propia chance de chocar
/// contra otra pestaña.
///
/// El historial y la auditoría son los mismos que emiten los endpoints que esto reemplaza
/// (<c>UpdateQuotationHandler</c>, <c>AddQuotationItemHandler</c>,
/// <c>UpdateQuotationItemHandler</c>, <c>RemoveQuotationItemHandler</c>): la línea de tiempo de una
/// cotización no puede depender de por qué endpoint entró el cambio.
/// </summary>
public sealed class SaveQuotationHandler(
    IQuotationRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationCustomerLookup customerLookup,
    IQuotationCompanyLookup companyLookup,
    IQuotationProductPricingLookup pricingLookup,
    IQuotationProductLookup productLookup,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<SaveQuotationCommand> validator)
    : ICommandHandler<SaveQuotationCommand, QuotationDto>
{
    public async Task<QuotationDto> HandleAsync(
        SaveQuotationCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, QuotationsPermissions.QuotationManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var quotation = await repository.FindAsync(
            command.TenantId, new QuotationId(command.QuotationId), cancellationToken)
            ?? throw QuotationNotFound.For(command.QuotationId);

        QuotationEditable.Ensure(quotation);

        // Con guardado de estado completo, «el último que guarda gana» pisaría en silencio lo que
        // otra persona cambió después de que esta pantalla cargó — mismo motivo y mismo contrato
        // que PUT /orders/{orderId}.
        if (quotation.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The quotation changed after it was loaded.");
        }

        var updatedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);
        var now = clock.UtcNow;

        var outcome = await QuotationEditsApplication.ApplyAsync(
            quotation, command, command.TenantId, customerLookup, companyLookup, pricingLookup,
            updatedBy, now, cancellationToken);

        // Guardar sin cambiar el encabezado no deja fila, igual que en UpdateQuotationHandler: una
        // lista de "editó" vacíos esconde las ediciones que sí importan.
        if (outcome.HeaderSummary is not null)
        {
            repository.AddHistoryEntry(QuotationHistoryEntry.Create(
                QuotationHistoryEntryId.New(),
                quotation.Id,
                QuotationHistoryEventType.Edited,
                updatedBy,
                outcome.HeaderSummary,
                now));
        }

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.quotation.updated",
            quotation.Id.ToString(),
            "success",
            now);

        await RecordItemEditsAsync(
            command.TenantId, quotation, outcome.ItemEdits, updatedBy, now, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return quotation.ToDto();
    }

    // Una fila de historial Edited y una acción quotation.quotation.item_* por cada cambio, como
    // los endpoints de línea de hoy. El nombre de una baja se busca en una sola ida al catálogo,
    // igual que BatchUpdateQuotationItemsHandler.
    private async Task RecordItemEditsAsync(
        Guid tenantId,
        Quotation quotation,
        IReadOnlyList<QuotationItemEdit> edits,
        MemberId updatedBy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var removedProductIds = edits
            .Where(edit => edit.Kind == QuotationItemEditKind.Removed)
            .Select(edit => edit.ProductId)
            .ToArray();
        var removedProducts = removedProductIds.Length == 0
            ? new Dictionary<Guid, QuotationProductRef>()
            : await productLookup.FindManyAsync(tenantId, removedProductIds, cancellationToken);

        foreach (var edit in edits)
        {
            string summary;
            string action;
            if (edit.Kind == QuotationItemEditKind.Added)
            {
                summary = QuotationChangeSummary.ItemAdded(edit.ProductName!, edit.Quantity!.Value);
                action = "quotation.quotation.item_added";
            }
            else if (edit.Kind == QuotationItemEditKind.QuantityChanged)
            {
                summary = QuotationChangeSummary.ItemQuantityChanged(
                    edit.ProductName!, edit.PreviousQuantity!.Value, edit.Quantity!.Value);
                action = "quotation.quotation.item_updated";
            }
            else
            {
                var productName = removedProducts.TryGetValue(edit.ProductId, out var product)
                    ? product.Name
                    : "un producto";
                summary = QuotationChangeSummary.ItemRemoved(productName);
                action = "quotation.quotation.item_removed";
            }

            repository.AddHistoryEntry(QuotationHistoryEntry.Create(
                QuotationHistoryEntryId.New(), quotation.Id, QuotationHistoryEventType.Edited,
                updatedBy, summary, now));
            auditPublisher.Publish(
                tenantId, executionContext.SubjectId, action, quotation.Id.ToString(), "success", now);
        }
    }
}
