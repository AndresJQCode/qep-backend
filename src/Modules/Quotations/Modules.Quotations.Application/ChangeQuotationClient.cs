using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record ChangeQuotationClientCommand(
    Guid TenantId,
    Guid QuotationId,
    Guid ClientId) : ICommand<QuotationDto>;

public sealed class ChangeQuotationClientValidator
    : AbstractValidator<ChangeQuotationClientCommand>
{
    public ChangeQuotationClientValidator()
    {
        RuleFor(command => command.ClientId).NotEmpty();
    }
}

/// <summary>
/// US-2 (revisada): cambia el cliente de una cotización editable.
///
/// Caso de uso propio y no un campo más de <see cref="UpdateQuotationHandler"/>: el cliente nuevo
/// pasa por la misma puerta que al crear —existe, tiene CUC y está activo
/// (<see cref="QuotationCustomerEligibility"/>)— y el cambio arrastra las partes y los totales,
/// que <c>UpdateDetails</c> no toca. Además deja su propia entrada de historial y de auditoría:
/// "a quién se le cotizó" es justo el dato que después se pregunta.
/// </summary>
public sealed class ChangeQuotationClientHandler(
    IQuotationRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationCustomerLookup customerLookup,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<ChangeQuotationClientCommand> validator)
    : ICommandHandler<ChangeQuotationClientCommand, QuotationDto>
{
    public async Task<QuotationDto> HandleAsync(
        ChangeQuotationClientCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, QuotationsPermissions.QuotationManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var quotation = await repository.FindAsync(
            command.TenantId, new QuotationId(command.QuotationId), cancellationToken)
            ?? throw QuotationNotFound.For(command.QuotationId);

        var customer = await customerLookup.FindAsync(
            command.TenantId, command.ClientId, cancellationToken);
        QuotationCustomerEligibility.Ensure(customer, command.TenantId, command.ClientId);

        // El nombre del cliente que sale, para que el historial diga "de X a Y" y no dos ids.
        // Una consulta más, y sólo en el cambio de cliente, que no es una operación frecuente.
        var previousNames = await customerLookup.FindNamesAsync(
            command.TenantId, [quotation.ClientId], cancellationToken);
        // A un local **antes** de ChangeClient: despues, quotation.ClientId ya es el nuevo.
        var previousName = previousNames.GetValueOrDefault(quotation.ClientId);

        var updatedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        quotation.ChangeClient(
            command.ClientId,
            customer.WithRetention,
            customer.VatSurplus,
            updatedBy,
            now);

        repository.AddHistoryEntry(QuotationHistoryEntry.Create(
            QuotationHistoryEntryId.New(),
            quotation.Id,
            QuotationHistoryEventType.Edited,
            updatedBy,
            QuotationChangeSummary.ClientChanged(previousName, customer.Name),
            now));
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.quotation.client_changed",
            quotation.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return quotation.ToDto();
    }
}
