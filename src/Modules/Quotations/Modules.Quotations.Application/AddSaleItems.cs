using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Una línea a agregar, dentro de la tanda — mismo par que
/// <see cref="QuotationItemAddition"/>, DTO propio de comando porque el request de Sales trae
/// el suyo (<see cref="SaleItemAdditionRequest"/>).</summary>
public sealed record SaleItemAddition(Guid ProductId, decimal Quantity);

/// <summary>La venta y la cotización actualizadas, para componer <c>SaleDetailResponse</c> igual
/// que <c>GetSaleByIdAsync</c> — la pantalla necesita las dos: el total nuevo vive en la
/// cotización y el estado de pago recalculado, en la venta.</summary>
public sealed record SaleItemsAddedResult(SaleDto Sale, QuotationDto Quotation);

/// <summary>
/// "Editar" un pedido pendiente para sumarle productos que faltaron al convertir (a pedido,
/// 2026-09) — sólo mientras <see cref="SaleStatus.Pending"/>: aprobada, la venta es el respaldo
/// de un cobro que alguien ya revisó con lo que tenía en ese momento.
///
/// Una tanda y no un producto por request, mismo motivo que
/// <see cref="BatchUpdateQuotationItemsCommand"/>: quien edita puede elegir varios productos
/// antes de guardar, y guardarlos de a uno encadenaría escrituras que compiten por la misma
/// versión optimista de la cotización.
///
/// Ruta hermana de <see cref="AddSalePaymentProofsCommand"/> y no una edición general del
/// pedido: sólo sumar líneas, nada de cantidad ni de quitar — ver
/// <see cref="Quotation.AddItemAfterConversion"/>.
/// </summary>
public sealed record AddSaleItemsCommand(
    Guid TenantId,
    Guid QuotationId,
    IReadOnlyList<SaleItemAddition> ToAdd) : ICommand<SaleItemsAddedResult>;

public sealed class AddSaleItemsValidator : AbstractValidator<AddSaleItemsCommand>
{
    public AddSaleItemsValidator()
    {
        RuleForEach(command => command.ToAdd).ChildRules(item =>
        {
            item.RuleFor(addition => addition.ProductId).NotEmpty();
            item.RuleFor(addition => addition.Quantity).GreaterThan(0m);
        });
        RuleFor(command => command.ToAdd)
            .Must(toAdd => toAdd.Count > 0)
            .WithMessage("At least one product must be added.");
    }
}

public sealed class AddSaleItemsHandler(
    IQuotationRepository quotationRepository,
    ISaleRepository saleRepository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationProductPricingLookup pricingLookup,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<AddSaleItemsCommand> validator)
    : ICommandHandler<AddSaleItemsCommand, SaleItemsAddedResult>
{
    public async Task<SaleItemsAddedResult> HandleAsync(
        AddSaleItemsCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, SalesPermissions.SaleManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var quotation = await quotationRepository.FindAsync(
            command.TenantId, new QuotationId(command.QuotationId), cancellationToken)
            ?? throw QuotationNotFound.For(command.QuotationId);

        var sale = await saleRepository.FindByQuotationIdAsync(
            command.TenantId, quotation.Id, cancellationToken)
            ?? throw SaleNotFound.For(command.QuotationId);

        // Mitad del chequeo que le toca a la venta (la otra la hace Quotation.AddItemAfterConversion
        // al mutar de verdad) — se hace antes de tocar nada para no dejar la cotización a medio
        // editar si la venta ya no está pendiente.
        if (sale.Status != SaleStatus.Pending)
        {
            throw new QuotationsDomainException(
                "sale.sale.not_pending",
                "Products can only be added while the sale is pending.");
        }

        var updatedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);
        var now = clock.UtcNow;

        foreach (var addition in command.ToAdd)
        {
            // US-3/US-4: precio base, descuento por escala e impuesto del producto, resueltos
            // contra el catálogo del tenant para la cantidad pedida — y en la moneda de la
            // cotización, que fija su cuenta de cobro.
            var pricing = await QuotationProductPricingResolver.ResolveAsync(
                pricingLookup,
                command.TenantId,
                addition.ProductId,
                addition.Quantity,
                quotation.Currency,
                cancellationToken);

            quotation.AddItemAfterConversion(
                QuotationItemId.New(),
                addition.ProductId,
                addition.Quantity,
                pricing.Pricing.UnitPrice,
                pricing.Pricing.DiscountPercentage,
                pricing.Pricing.TaxPercentage,
                updatedBy,
                now);

            quotationRepository.AddHistoryEntry(QuotationHistoryEntry.Create(
                QuotationHistoryEntryId.New(),
                quotation.Id,
                QuotationHistoryEventType.Edited,
                updatedBy,
                QuotationChangeSummary.ItemAdded(pricing.Name, addition.Quantity),
                now));
            auditPublisher.Publish(
                command.TenantId,
                executionContext.SubjectId,
                "quotation.sale.item_added",
                quotation.Id.ToString(),
                "success",
                now);
        }

        // El total ya quedó al día (cada AddItemAfterConversion recalcula, vía Touch): lo
        // cargado en comprobantes no cambió, pero el total contra el que se compara sí.
        sale.RecalculatePaymentStatus(quotation.Total, now);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new SaleItemsAddedResult(sale.ToDto(), quotation.ToDto());
    }
}
