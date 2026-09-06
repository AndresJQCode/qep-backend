using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record UpdateQuotationCommand(
    Guid TenantId,
    Guid QuotationId,
    DateOnly? ValidUntil,
    string? PaymentMethod,
    string? Notes,
    QuotationPartiesRequest? Parties,
    QuotationBillingAccountRequest? BillingAccount) : ICommand<QuotationDto>;

public sealed class UpdateQuotationValidator : AbstractValidator<UpdateQuotationCommand>
{
    public UpdateQuotationValidator()
    {
        RuleFor(command => command.PaymentMethod)
            .MaximumLength(Quotation.PaymentMethodMaxLength)
            .When(command => command.PaymentMethod is not null);
    }
}

public sealed class UpdateQuotationHandler(
    IQuotationRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationCustomerLookup customerLookup,
    IQuotationCompanyLookup companyLookup,
    IQuotationProductPricingLookup pricingLookup,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateQuotationCommand> validator)
    : ICommandHandler<UpdateQuotationCommand, QuotationDto>
{
    public async Task<QuotationDto> HandleAsync(
        UpdateQuotationCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, QuotationsPermissions.QuotationManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

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

        var billingAccount = await QuotationBillingAccountResolver.ResolveAsync(
            companyLookup, command.TenantId, command.BillingAccount, cancellationToken);

        // Cambiar a una cuenta en otra moneda cambia la moneda de la cotización entera, y con
        // ella el precio de cada línea: lo guardado es el precio del producto en la moneda vieja,
        // no una cifra convertible. Se piden todos los precios juntos y **antes** de tocar el
        // agregado, así una línea sin precio en la moneda nueva corta el guardado completo en vez
        // de dejar la cotización con dos monedas adentro.
        var currency = quotation.CurrencyFor(billingAccount);
        var repricing = currency == quotation.Currency
            ? null
            : await QuotationProductPricingResolver.ResolveManyAsync(
                pricingLookup,
                command.TenantId,
                quotation.Items
                    .Select(item => (item.ProductId, item.Quantity))
                    .ToArray(),
                currency,
                cancellationToken);

        var updatedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        // La foto de antes, para poder decir en el historial **qué** se editó: el PATCH manda el
        // encabezado entero en cada guardado, así que sin comparar no hay forma de saberlo.
        var before = QuotationHeaderSnapshot.Of(quotation);
        quotation.UpdateDetails(
            command.ValidUntil,
            command.PaymentMethod,
            command.Notes,
            command.Parties.ToDomain(),
            billingAccount,
            repricing,
            updatedBy,
            now);

        // Guardar sin cambiar nada no deja fila: apretar Guardar dos veces no es un evento del
        // historial, y una lista de "editó" vacíos esconde las ediciones que sí importan.
        var summary = QuotationChangeSummary.HeaderChanged(
            before, QuotationHeaderSnapshot.Of(quotation));
        if (summary is not null)
        {
            repository.AddHistoryEntry(QuotationHistoryEntry.Create(
                QuotationHistoryEntryId.New(),
                quotation.Id,
                QuotationHistoryEventType.Edited,
                updatedBy,
                summary,
                now));
        }
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.quotation.updated",
            quotation.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return quotation.ToDto();
    }
}
