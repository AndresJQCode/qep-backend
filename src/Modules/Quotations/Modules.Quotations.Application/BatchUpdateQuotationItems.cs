using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Una línea a agregar, dentro de la tanda — mismos dos datos que
/// <see cref="AddQuotationItemCommand"/>, sin tenant ni cotización porque ya viajan una sola
/// vez en el comando que la contiene.</summary>
public sealed record QuotationItemAddition(Guid ProductId, decimal Quantity);

/// <summary>
/// Varias altas y bajas de línea en una sola operación — lo que pide la modal "Agregar
/// productos" al guardar: antes cada alta o cada baja era su propio <c>POST</c>/<c>DELETE</c>,
/// una cadena de escrituras en serie sobre la misma cotización con su propia chance de chocar
/// por concurrencia optimista entre una y la siguiente. Acá se cargan el agregado una sola vez,
/// se aplican todas las altas y bajas sobre esa misma instancia, y se guarda una sola vez.
/// </summary>
public sealed record BatchUpdateQuotationItemsCommand(
    Guid TenantId,
    Guid QuotationId,
    IReadOnlyList<QuotationItemAddition> ToAdd,
    IReadOnlyList<Guid> ToRemoveItemIds) : ICommand<QuotationDto>;

public sealed class BatchUpdateQuotationItemsValidator
    : AbstractValidator<BatchUpdateQuotationItemsCommand>
{
    public BatchUpdateQuotationItemsValidator()
    {
        RuleForEach(command => command.ToAdd).ChildRules(item =>
        {
            item.RuleFor(addition => addition.ProductId).NotEmpty();
            item.RuleFor(addition => addition.Quantity).GreaterThan(0m);
        });
        // Una tanda vacía no es un pedido — es el caso de "Guardar" sin haber cambiado nada,
        // y la pantalla ya lo evita deshabilitando el botón. Que llegue igual (una URL escrita a
        // mano) no tiene nada que hacer acá.
        RuleFor(command => command)
            .Must(command => command.ToAdd.Count > 0 || command.ToRemoveItemIds.Count > 0)
            .WithMessage("The batch must include at least one addition or removal.");
    }
}

public sealed class BatchUpdateQuotationItemsHandler(
    IQuotationRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IQuotationProductPricingLookup pricingLookup,
    IQuotationProductLookup productLookup,
    IQuotationCustomerLookup customerLookup,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<BatchUpdateQuotationItemsCommand> validator)
    : ICommandHandler<BatchUpdateQuotationItemsCommand, QuotationDto>
{
    public async Task<QuotationDto> HandleAsync(
        BatchUpdateQuotationItemsCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, QuotationsPermissions.QuotationManage);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var quotation = await repository.FindAsync(
            command.TenantId, new QuotationId(command.QuotationId), cancellationToken)
            ?? throw QuotationNotFound.For(command.QuotationId);

        // Deja la retención/excedente de IVA al día con el cliente maestro antes de recalcular
        // — ver Quotation.RefreshCustomerTaxProfile. Una sola vez para toda la tanda, no por
        // línea: es el mismo cliente para todas.
        var customer = await customerLookup.FindAsync(
            command.TenantId, quotation.ClientId, cancellationToken);
        if (customer is not null)
        {
            quotation.RefreshCustomerTaxProfile(customer.WithRetention, customer.VatSurplus);
        }

        var updatedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);
        var now = clock.UtcNow;

        // Los nombres de los productos que se sacan, para el resumen del historial de cada
        // baja — una sola ida al catálogo para toda la tanda en vez de una por línea (mismo
        // criterio que ListQuotationsHandler resolviendo nombres de cliente).
        var removedProductIds = command.ToRemoveItemIds
            .Select(itemId => quotation.Items.FirstOrDefault(item => item.Id.Value == itemId))
            .Where(item => item is not null)
            .Select(item => item!.ProductId)
            .Distinct()
            .ToArray();
        var removedProductNames = removedProductIds.Length == 0
            ? new Dictionary<Guid, QuotationProductRef>()
            : await productLookup.FindManyAsync(
                command.TenantId, removedProductIds, cancellationToken);

        // Bajas primero: si una alta y una baja compartieran producto —no debería pasar, la
        // modal no lo ofrece— sacar antes que sumar es el orden que no se pisa a sí mismo.
        foreach (var itemId in command.ToRemoveItemIds)
        {
            var removed = quotation.Items.FirstOrDefault(item => item.Id.Value == itemId);
            var productName = removed is not null
                && removedProductNames.TryGetValue(removed.ProductId, out var removedProduct)
                    ? removedProduct.Name
                    : "un producto";

            // El propio agregado traduce un itemId desconocido a quotation.item.not_found.
            quotation.RemoveItem(new QuotationItemId(itemId), updatedBy, now);

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
        }

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

            quotation.AddItem(
                QuotationItemId.New(),
                addition.ProductId,
                addition.Quantity,
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
                QuotationChangeSummary.ItemAdded(pricing.Name, addition.Quantity),
                now));
            auditPublisher.Publish(
                command.TenantId,
                executionContext.SubjectId,
                "quotation.quotation.item_added",
                quotation.Id.ToString(),
                "success",
                now);
        }

        // Una sola escritura para toda la tanda: es justo lo que esto reemplaza — antes,
        // guardar tres altas y una baja eran cuatro viajes a la base, cada uno con su propia
        // versión y su propia chance de chocar contra otra pestaña editando la misma cotización.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return quotation.ToDto();
    }
}
