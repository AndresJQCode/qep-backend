using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record AddQuotationItemCommand(
    Guid TenantId, Guid QuotationId, Guid ProductId, decimal Quantity) : ICommand<QuotationDto>;

public sealed class AddQuotationItemValidator : AbstractValidator<AddQuotationItemCommand>
{
    public AddQuotationItemValidator()
    {
        RuleFor(command => command.ProductId).NotEmpty();
        RuleFor(command => command.Quantity).GreaterThan(0m);
    }
}

public sealed class AddQuotationItemHandler(
    IQuotationRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationProductPricingLookup pricingLookup,
    IQuotationCustomerLookup customerLookup,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<AddQuotationItemCommand> validator)
    : ICommandHandler<AddQuotationItemCommand, QuotationDto>
{
    public async Task<QuotationDto> HandleAsync(
        AddQuotationItemCommand command,
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

        // US-3/US-4: precio base, descuento por escala e impuesto del producto, resueltos
        // contra el catálogo del tenant para la cantidad pedida — y en la moneda de la
        // cotización, que fija su cuenta de cobro.
        var pricing = await QuotationProductPricingResolver.ResolveAsync(
            pricingLookup,
            command.TenantId,
            command.ProductId,
            command.Quantity,
            quotation.Currency,
            cancellationToken);

        var updatedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        quotation.AddItem(
            QuotationItemId.New(),
            command.ProductId,
            command.Quantity,
            pricing.Pricing.UnitPrice,
            pricing.Pricing.DiscountPercentage,
            pricing.Pricing.TaxPercentage,
            updatedBy,
            now);

        repository.AddHistoryEntry(QuotationHistoryEntry.Create(
            QuotationHistoryEntryId.New(),
            quotation.Id,
            QuotationHistoryEventType.Edited,
            updatedBy,
            QuotationChangeSummary.ItemAdded(pricing.Name, command.Quantity),
            now));
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.quotation.item_added",
            quotation.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return quotation.ToDto();
    }
}
