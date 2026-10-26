using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>
/// «Reconocer este nombre» (spec de Shopify D-S25): el pedido llegó de una tienda con un título de envío que
/// ninguna transportadora reconoció (<see cref="Order.ExternalShippingTitle"/>). Quien lo revisa elige la
/// transportadora y, con <paramref name="RememberName"/>, la deja recordando ese título para los próximos
/// pedidos.
/// </summary>
public sealed record RecognizeOrderCarrierCommand(
    Guid TenantId,
    Guid OrderId,
    Guid CarrierId,
    bool RememberName) : ICommand<OrderDetailDto>;

/// <summary>El <c>carrierId</c> vacío llega como campo en <c>errors</c>: es el selector que la pantalla marca.</summary>
public sealed class RecognizeOrderCarrierValidator : AbstractValidator<RecognizeOrderCarrierCommand>
{
    public RecognizeOrderCarrierValidator()
    {
        RuleFor(command => command.CarrierId).NotEmpty();
    }
}

/// <summary>
/// Fija la transportadora conservando modo, valor, tasa y contraentrega —lo que cobró la tienda no se toca— y,
/// si se pide, suma el título a los externos de la transportadora. Todo en una sola escritura de Quotations:
/// pedido, historial, transportadora y auditoría. El choque del título con otra transportadora lo da
/// <c>IX_carrier_names_tenant_name</c> al guardar (<c>quotations.carrier.name_taken</c>), como en
/// <see cref="CreateCarrierHandler"/>.
/// </summary>
public sealed class RecognizeOrderCarrierHandler(
    IOrderRepository orderRepository,
    IQuotationRepository quotationRepository,
    ICarrierRepository carriers,
    ITenantModules tenantModules,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IMembershipDirectory membershipDirectory,
    ITenantDirectory tenantDirectory,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<RecognizeOrderCarrierCommand> validator)
    : ICommandHandler<RecognizeOrderCarrierCommand, OrderDetailDto>
{
    public async Task<OrderDetailDto> HandleAsync(
        RecognizeOrderCarrierCommand command,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, OrdersPermissions.OrderManage);
        // Recordar un nombre es configurar transportadoras (flete §10): el mismo permiso que editarlas, y
        // antes de tocar nada.
        if (command.RememberName)
        {
            QuotationsAuthorization.EnsureAuthorized(
                executionContext, command.TenantId, TenancyPermissions.SettingsUpdate);
        }

        await TenantModuleGuard.EnsureEnabledAsync(
            tenantModules, command.TenantId, Modules.Tenancy.Domain.TenantModuleKeys.Orders, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var order = await orderRepository.FindByIdAsync(
            command.TenantId, new OrderId(command.OrderId), cancellationToken)
            ?? throw OrderNotFound.ById(command.OrderId);
        if (order.Status != OrderStatus.Pending)
        {
            throw new QuotationsDomainException(
                "order.order.not_pending",
                "The carrier of an order can only change while it is pending.");
        }

        if (order.ExternalShippingTitle is not { } title)
        {
            throw new QuotationsDomainException(
                "order.order.carrier_recognition_unavailable",
                "The order has no unrecognized shipping title to recognize.");
        }

        var carrier = await carriers.FindAsync(command.TenantId, new CarrierId(command.CarrierId), cancellationToken);
        if (carrier is null || !carrier.IsActive)
        {
            throw new QuotationsDomainException(
                "order.order.carrier_not_available",
                "The carrier does not exist or is no longer active.");
        }

        // Mismo criterio que ShippingResolver (H21): la contraentrega con una transportadora que no recauda
        // es una contradicción que se puede detectar al elegirla.
        if (order.CashOnDelivery && !carrier.SupportsCashOnDelivery)
        {
            throw new QuotationsDomainException(
                "order.order.cash_on_delivery_not_supported",
                "The carrier does not collect cash on delivery.");
        }

        var updatedBy = await QuotationAdvisorResolver.ResolveAsync(
            membershipDirectory, executionContext, command.TenantId, cancellationToken);
        // Lee el formato del tenant aunque hoy este handler sólo cambie la transportadora:
        // ShippingChanged lo pide, y un formato colombiano fijo imprimiría mal, en silencio, el día
        // que este handler toque el valor del flete.
        var format = await HistoryFormat.ReadAsync(tenantDirectory, command.TenantId, cancellationToken);
        var now = clock.UtcNow;

        var before = order.ShippingDetails;
        var after = before with { CarrierId = carrier.Id.Value, CarrierName = carrier.Name };
        if (after != before)
        {
            order.SetShipping(after, updatedBy, now);
            // PartiesChanged y la misma acción de auditoría que la edición del envío (SaveOrderEditsHandler).
            if (OrderChangeSummary.ShippingChanged(before, order.ShippingDetails, format) is { } summary)
            {
                orderRepository.AddHistoryEntry(OrderHistoryEntry.Create(
                    OrderHistoryEntryId.New(), order.Id, OrderHistoryEventType.PartiesChanged, updatedBy, summary, now));
            }

            auditPublisher.Publish(
                command.TenantId, executionContext.SubjectId, "quotation.order.shipping_changed",
                order.Id.ToString(), "success", now);
        }

        if (command.RememberName)
        {
            var versionBefore = carrier.Version;
            carrier.AddExternalName(title, now);
            if (carrier.Version != versionBefore)
            {
                auditPublisher.Publish(
                    command.TenantId, executionContext.SubjectId, UpdateCarrierHandler.AuditAction,
                    carrier.Id.ToString(), "success", now);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await OrderDetails.LoadAsync(
            orderRepository, quotationRepository, command.TenantId, order, cancellationToken);
    }
}
