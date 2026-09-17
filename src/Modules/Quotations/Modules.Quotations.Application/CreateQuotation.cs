using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record CreateQuotationCommand(
    Guid TenantId,
    Guid ClientId,
    DateOnly? ValidUntil,
    string? PaymentMethod,
    string? Notes,
    QuotationPartiesRequest? Parties,
    QuotationBillingAccountRequest? BillingAccount) : ICommand<QuotationDto>;

public sealed class CreateQuotationValidator : AbstractValidator<CreateQuotationCommand>
{
    public CreateQuotationValidator()
    {
        RuleFor(command => command.ClientId).NotEmpty();
        RuleFor(command => command.PaymentMethod)
            .MaximumLength(Quotation.PaymentMethodMaxLength)
            .When(command => command.PaymentMethod is not null);
    }
}

public sealed class CreateQuotationHandler(
    IQuotationRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationCustomerLookup customerLookup,
    IQuotationCompanyLookup companyLookup,
    IQuotationNumberGenerator numberGenerator,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    ITenantClock tenantClock,
    IValidator<CreateQuotationCommand> validator)
    : ICommandHandler<CreateQuotationCommand, QuotationDto>
{
    public async Task<QuotationDto> HandleAsync(
        CreateQuotationCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de validar y no al revés, mismo criterio que CreateProductHandler: la
        // política del endpoint ya frena a quien le falta el permiso, pero no al que lo tiene
        // para otro tenant.
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, QuotationsPermissions.QuotationManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        // US-1/US-18: cliente inexistente, sin CUC o inactivo bloquea la creación.
        var customer = await customerLookup.FindAsync(
            command.TenantId, command.ClientId, cancellationToken);
        QuotationCustomerEligibility.Ensure(customer, command.TenantId, command.ClientId);

        var billingAccount = await QuotationBillingAccountResolver.ResolveAsync(
            companyLookup, command.TenantId, command.BillingAccount, cancellationToken);

        var advisorId = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        // El año del consecutivo es el del día del tenant, no el de UTC (spec 2026-09-17, punto 2a):
        // en Bogotá, el 31 de diciembre desde las 19:00 UTC ya es el año siguiente.
        var calendar = await tenantClock.GetAsync(command.TenantId, cancellationToken);
        var now = calendar.UtcNow;
        var year = calendar.Today.Year;
        var sequence = await numberGenerator.NextAsync(command.TenantId, year, cancellationToken);
        var quotationNumber = QuotationNumberFormatter.Format(year, sequence);

        var quotation = Quotation.Create(
            QuotationId.New(),
            command.TenantId,
            quotationNumber,
            command.ClientId,
            advisorId,
            // Sin vigencia en el request, quince días desde el hoy del tenant (spec 2026-09-17,
            // punto 2c). Se resuelve **al crear** y queda guardado: calcularlo al leer haría que la
            // misma cotización mostrara una fecha distinta cada día.
            command.ValidUntil ?? calendar.Today.AddDays(DefaultValidityDays),
            command.PaymentMethod,
            command.Notes,
            command.Parties.ToDomain(),
            billingAccount,
            customer.WithRetention,
            customer.VatSurplus,
            advisorId,
            now);

        repository.Add(quotation);
        repository.AddHistoryEntry(QuotationHistoryEntry.Create(
            QuotationHistoryEntryId.New(),
            quotation.Id,
            QuotationHistoryEventType.Created,
            advisorId,
            QuotationChangeSummary.Created(customer.Name, quotation.Currency),
            now));
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.quotation.created",
            quotation.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return quotation.ToDto();
    }

    public const int DefaultValidityDays = 15;
}
