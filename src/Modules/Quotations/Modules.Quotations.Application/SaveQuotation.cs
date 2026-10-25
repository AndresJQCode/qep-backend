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
    IReadOnlyList<QuotationItemAddition> Items,
    int? GlobalScaleFloor,
    bool IsRetail,
    ShippingRequest? Shipping = null) : ICommand<QuotationDto>, IQuotationEdits;

public sealed class SaveQuotationValidator : QuotationEditsValidator<SaveQuotationCommand>;

/// <summary>
/// Todo sobre el agregado rastreado y un único <c>SaveChangesAsync</c>, así que una falla en
/// cualquier paso no deja nada a medio guardar — antes, guardar el encabezado y tres cambios de
/// línea eran cuatro viajes a la base, cada uno con su propia versión y su propia chance de chocar
/// contra otra pestaña.
///
/// El historial y la auditoría son los mismos que emiten los endpoints que esto reemplaza
/// (<c>AddQuotationItemHandler</c>, <c>UpdateQuotationItemHandler</c>,
/// <c>RemoveQuotationItemHandler</c>): la línea de tiempo de una cotización no puede depender de
/// por qué endpoint entró el cambio.
/// </summary>
public sealed class SaveQuotationHandler(
    IQuotationRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationCustomerLookup customerLookup,
    IQuotationCompanyLookup companyLookup,
    IQuotationProductPricingLookup pricingLookup,
    IQuotationSettingsStore settingsStore,
    IQuotationProductLookup productLookup,
    ShippingResolver shippingResolver,
    IMembershipDirectory membershipDirectory,
    ITenantDirectory tenantDirectory,
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
            quotation, command, command.TenantId, customerLookup, companyLookup, pricingLookup, settingsStore,
            shippingResolver, updatedBy, now, cancellationToken);
        // UpdateDetails no lo exige para que el cálculo previo, que comparte ApplyAsync, no falle
        // mientras la persona escribe. El guardado sí: nada se persiste si esto lanza. El
        // validador ya lo frena antes con el campo en `errors`; esto es el respaldo del dominio.
        quotation.EnsureBillingIdentified();

        var format = await HistoryFormat.ReadAsync(tenantDirectory, command.TenantId, cancellationToken);
        // Guardar sin cambiar el encabezado no deja fila: una lista de "editó" vacíos esconde las
        // ediciones que sí importan.
        if (QuotationChangeSummary.HeaderChanged(outcome.HeaderBefore, outcome.HeaderAfter, format) is { } headerSummary)
        {
            repository.AddHistoryEntry(QuotationHistoryEntry.Create(
                QuotationHistoryEntryId.New(),
                quotation.Id,
                QuotationHistoryEventType.Edited,
                updatedBy,
                headerSummary,
                now));
        }

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.quotation.updated",
            quotation.Id.ToString(),
            "success",
            now);

        await QuotationItemEditHistory.RecordAsync(
            repository, auditPublisher, productLookup, executionContext.SubjectId, command.TenantId, quotation,
            outcome.ItemEdits, format, updatedBy, now, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return quotation.ToDto();
    }
}
