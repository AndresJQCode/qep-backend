using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

public sealed record UpdateQuotationItemCommand(
    Guid TenantId, Guid QuotationId, Guid ItemId, decimal Quantity) : ICommand<QuotationDto>;

public sealed class UpdateQuotationItemValidator : AbstractValidator<UpdateQuotationItemCommand>
{
    public UpdateQuotationItemValidator()
    {
        RuleFor(command => command.Quantity).GreaterThan(0m);
    }
}

public sealed class UpdateQuotationItemHandler(
    IQuotationRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationProductPricingLookup pricingLookup,
    IQuotationCustomerLookup customerLookup,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateQuotationItemCommand> validator)
    : ICommandHandler<UpdateQuotationItemCommand, QuotationDto>
{
    public async Task<QuotationDto> HandleAsync(
        UpdateQuotationItemCommand command,
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

        // Mismo código que Quotation.UpdateItemQuantity usaría si se le pasara un id
        // desconocido: se busca acá primero porque hace falta el ProductId de la línea antes de
        // poder resolver su descuento.
        var item = quotation.Items.FirstOrDefault(item => item.Id.Value == command.ItemId)
            ?? throw new QuotationsDomainException(
                "quotation.item.not_found", "The quotation item was not found.");
        // Antes de que UpdateItemQuantity la pise: el historial dice de cuánto a cuánto.
        var previousQuantity = item.Quantity;

        // US-4: la cantidad nueva puede caer en otra escala del mismo producto, así que el
        // descuento se vuelve a resolver — nunca se conserva el anterior. El impuesto también:
        // la tasa del producto pudo cambiar desde que se agregó la línea.
        var pricing = await QuotationProductPricingResolver.ResolveAsync(
            pricingLookup,
            command.TenantId,
            item.ProductId,
            command.Quantity,
            quotation.Currency,
            cancellationToken);

        var updatedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        quotation.UpdateItemQuantity(
            item.Id,
            command.Quantity,
            pricing.Pricing.DiscountPercentage,
            pricing.Pricing.TaxPercentage,
            updatedBy,
            now);

        repository.AddHistoryEntry(QuotationHistoryEntry.Create(
            QuotationHistoryEntryId.New(),
            quotation.Id,
            QuotationHistoryEventType.Edited,
            updatedBy,
            QuotationChangeSummary.ItemQuantityChanged(
                pricing.Name, previousQuantity, command.Quantity),
            now));
        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            "quotation.quotation.item_updated",
            quotation.Id.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return quotation.ToDto();
    }
}
