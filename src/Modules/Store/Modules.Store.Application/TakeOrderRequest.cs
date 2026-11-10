using BuildingBlocks.Application;
using FluentValidation;
using Microsoft.Extensions.Logging;
using Modules.Store.Domain;
using Modules.Tenancy.Application;

namespace Modules.Store.Application;

/// <summary>Sólo la clasificación que el diálogo ofrece: el resto del cliente sale del bloque de facturación de la solicitud.</summary>
public sealed record TakeNewCustomerBody(Guid? ClassificationId);

/// <summary>Exactamente uno de los dos: un cliente existente o uno nuevo.</summary>
public sealed record TakeOrderRequestBody(Guid? CustomerId, TakeNewCustomerBody? NewCustomer);

public sealed record TakeOrderRequestCommand(Guid TenantId, Guid RequestId, TakeOrderRequestBody Body)
    : ICommand<StoreTakeResultDto>;

public sealed class TakeOrderRequestValidator : AbstractValidator<TakeOrderRequestCommand>
{
    public TakeOrderRequestValidator()
    {
        RuleFor(command => command.Body).NotNull();
        RuleFor(command => command.Body)
            .Must(body => body is null || (body.CustomerId is not null) != (body.NewCustomer is not null))
            .WithMessage("Envía customerId o newCustomer, uno solo.");
        RuleFor(command => command.Body.CustomerId)
            .NotEqual(Guid.Empty)
            .When(command => command.Body?.CustomerId is not null);
    }
}

/// <summary>
/// «Tomar» (spec §5.2). No hay una transacción que cubra Store, Customers y Quotations, así que se sigue el patrón de Shopify: un
/// lease corto en la solicitud (un solo tomador), el pedido con una clave idempotente por solicitud, y la solicitud marcada
/// después. Si el proceso cae entre el pedido y la marca, el siguiente intento encuentra el pedido y sólo marca.
/// </summary>
public sealed partial class TakeOrderRequestHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IOnlineStoreRepository stores,
    IStoreOrderRequestRepository requests,
    IStoreUnitOfWork unitOfWork,
    IStoreAuditPublisher auditPublisher,
    ITenantClock tenantClock,
    StoreOptions options,
    IStoreMemberDirectory members,
    IStoreCustomerDirectory customers,
    IStoreOrders orders,
    OrderRequestRowComposer composer,
    IValidator<TakeOrderRequestCommand> validator,
    ILogger<TakeOrderRequestHandler> logger)
    : ICommandHandler<TakeOrderRequestCommand, StoreTakeResultDto>
{
    public async Task<StoreTakeResultDto> HandleAsync(TakeOrderRequestCommand command, CancellationToken cancellationToken)
    {
        await StoreAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, StorePermissions.OrderManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var caller = await members.ResolveCallerAsync(command.TenantId, cancellationToken);
        var request = await requests.FindAsync(command.TenantId, command.RequestId, cancellationToken)
            ?? throw StoreNotFound.OrderRequest(command.RequestId);
        if (request.Status != StoreOrderRequestStatus.New)
        {
            throw new ResourceConflictException(StoreErrorCodes.NotNew, "The order request was already converted or discarded.");
        }

        // La cuenta de la moneda de la solicitud, tal como está configurada ahora. Si la moneda ya no es de la tienda se corta acá,
        // antes de reservar nada, crear un cliente o un pedido: la solicitud queda Nueva y sin lease.
        var store = await stores.FindAsync(command.TenantId, cancellationToken);
        var account = store?.BillingAccountFor(request.Currency)
            ?? throw new StoreDomainException(
                StoreErrorCodes.CurrencyNotInStore, "The currency of the request is no longer one the store sells.");

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        try
        {
            request.BeginConversion(caller.MembershipId, now, options.ConversionLease);
        }
        catch (StoreDomainException exception) when (StoreConversionErrors.IsStateConflict(exception))
        {
            throw StoreConversionErrors.ToConflict(exception);
        }

        // El lease se guarda ya: quien pierde la carrera choca acá (409 conflict) y no alcanza a crear cliente ni dirección.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        try
        {
            return await ConvertAsync(command, caller, request, store!, account, now, cancellationToken);
        }
        catch
        {
            // Un cliente creado en el paso 7 o una dirección agregada en el 8 se quedan: el reintento los encuentra por documento y
            // por contenido, así que nada se duplica.
            await ReleaseLeaseAsync(command, caller.MembershipId);
            throw;
        }
    }

    private async Task<StoreTakeResultDto> ConvertAsync(
        TakeOrderRequestCommand command,
        (Guid MembershipId, Guid UserId) caller,
        StoreOrderRequest request,
        OnlineStore store,
        StoreBillingAccount account,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var tenantId = command.TenantId;

        // R5: un pedido que ya salió de esta solicitud (el proceso cayó antes de marcarla) se reconoce ANTES de tocar al cliente o su
        // libreta. Se marca con el cliente de ESE pedido, que puede no ser el que el segundo tomador eligió.
        if (await orders.FindExistingAsync(tenantId, request.Id, cancellationToken) is { } existing)
        {
            await MarkConvertedAsync(request, existing, now, cancellationToken);
            // Este camino no mira la libreta: no hay resultado de direcciones que informar.
            return await ResultAsync(
                request,
                existing,
                customerCreated: false,
                customerCompleted: false,
                new StoreTakeAddressesDto(null, null),
                now,
                cancellationToken);
        }

        var billing = BillingData(request, command.Body.NewCustomer?.ClassificationId);
        StoreCustomerRef customer;
        var created = false;
        var completed = false;
        if (command.Body.CustomerId is { } customerId)
        {
            customer = await customers.FindEligibleAsync(tenantId, customerId, cancellationToken)
                ?? throw CustomerNotFound();
            if (!customer.IsComplete)
            {
                (customer, completed) = await CompleteAsync(tenantId, caller.UserId, customer, billing, cancellationToken);
            }
        }
        else
        {
            var creation = await customers.CreateAsync(tenantId, caller.UserId, billing, cancellationToken);
            switch (creation.Outcome)
            {
                case StoreCustomerCreationOutcome.Created:
                    customer = creation.Customer!;
                    created = true;
                    break;
                case StoreCustomerCreationOutcome.ExistingByDocument:
                    // El documento ya era de otro cliente (la carrera con otra alta): se usa ése, y si estaba incompleto se completa
                    // como si la persona lo hubiera elegido.
                    customer = creation.Customer!;
                    if (!customer.IsComplete)
                    {
                        (customer, completed) = await CompleteAsync(tenantId, caller.UserId, customer, billing, cancellationToken);
                    }

                    break;
                case StoreCustomerCreationOutcome.DocumentOwnerUnavailable:
                    // El documento es de un cliente inactivo o fusionado: el mismo 422 de siempre en «Tomar» (se borra en la Tarea 9).
                    throw CustomerNotFound();
                default:
                    throw new StoreDomainException(
                        StoreErrorCodes.ValidationFailed, "The billing block is not enough for a complete customer.");
            }
        }

        var outcomes = await customers.EnsureAddressesAsync(tenantId, customer.Id, AddressInputs(request), cancellationToken);
        var addresses = new StoreTakeAddressesDto(
            outcomes[0].ToString(), request.Shipping is null ? null : outcomes[1].ToString());

        var draft = new StoreOrderDraft(
            tenantId,
            request.Id,
            request.DisplayNumber,
            customer.Id,
            caller.MembershipId,
            // El modo de precios de la tienda AHORA, no el de la solicitud: el pedido se precia como la tienda vende hoy.
            store.PricingMode == StorePricingMode.RetailOnly,
            account,
            BillingParty(request.Billing),
            ShippingParty(request),
            customer.WithRetention,
            customer.VatSurplus,
            request.Lines.OrderBy(line => line.Position).Select(line => new StoreOrderLineInput(line.VariantId, line.Quantity)).ToList(),
            request.Note,
            caller.UserId);
        var order = await orders.CreateAsync(draft, cancellationToken);

        await MarkConvertedAsync(request, order, now, cancellationToken);
        return await ResultAsync(request, order, created, completed, addresses, now, cancellationToken);
    }

    private async Task MarkConvertedAsync(
        StoreOrderRequest request, StoreOrderCreated order, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // convertedBy es el asesor del pedido: el tomador original cuando el pedido ya existía.
        request.MarkConverted(order.OrderId, order.ClientId, order.OrderNumber, order.Total, order.AdvisorMembershipId, now);
        auditPublisher.Publish(
            request.TenantId,
            executionContext.SubjectId,
            StoreAuditActions.RequestConverted,
            StoreAuditActions.Resources.OrderRequest,
            request.Id.ToString(),
            "success",
            ["status", "orderId", "customerId"],
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task<StoreTakeResultDto> ResultAsync(
        StoreOrderRequest request,
        StoreOrderCreated order,
        bool customerCreated,
        bool customerCompleted,
        StoreTakeAddressesDto addresses,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var row = (await composer.ComposeAsync(request.TenantId, [request], now, cancellationToken))[0];
        return new StoreTakeResultDto(
            row, order.OrderId, order.OrderNumber, order.ClientId, customerCreated, customerCompleted, addresses);
    }

    // Si algo falló, lo pendiente del contexto no vale: se suelta, se relee la solicitud y se libera SU lease. Sin el token del request:
    // si el cliente se desconectó, el lease igual se suelta en vez de quedar tomado hasta que venza. Un fallo al liberar no tapa el
    // error original (el lease vence solo a los pocos minutos), pero queda en el log.
    private async Task ReleaseLeaseAsync(TakeOrderRequestCommand command, Guid membershipId)
    {
        try
        {
            unitOfWork.DiscardPendingChanges();
            var fresh = await requests.FindAsync(command.TenantId, command.RequestId, CancellationToken.None);
            if (fresh is { Status: StoreOrderRequestStatus.New } && fresh.LeaseMemberId == membershipId)
            {
                fresh.ReleaseConversion();
                await unitOfWork.SaveChangesAsync(CancellationToken.None);
            }
        }
#pragma warning disable CA1031 // Ver arriba: el error que importa es el original, que se relanza.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogReleaseFailed(logger, command.RequestId, command.TenantId, exception);
        }
    }

    [LoggerMessage(
        EventId = 6100,
        Level = LogLevel.Warning,
        Message = "Could not release the take lease of store order request {RequestId} in tenant {TenantId}")]
    private static partial void LogReleaseFailed(ILogger logger, Guid requestId, Guid tenantId, Exception exception);

    // P5: el completado puede devolver al cliente sin tocar (no se pudo completar) o al dueño del documento; sólo cuenta como
    // completado si es el mismo cliente y quedó completo.
    private async Task<(StoreCustomerRef Customer, bool Completed)> CompleteAsync(
        Guid tenantId, Guid actorUserId, StoreCustomerRef customer, StoreNewCustomer billing, CancellationToken cancellationToken)
    {
        var result = await customers.CompleteAsync(tenantId, actorUserId, customer.Id, billing, cancellationToken);
        return (result, result.Id == customer.Id && result.IsComplete);
    }

    private static StoreDomainException CustomerNotFound() =>
        new(StoreErrorCodes.CustomerNotFound, "The customer does not exist, is inactive or was merged.");

    // Una ciudad de Colombia va sólo con su id; afuera, sólo con el texto.
    private static StoreNewCustomer BillingData(StoreOrderRequest request, Guid? classificationId)
    {
        var billing = request.Billing;
        var place = billing.Place;
        return new StoreNewCustomer(
            billing.Name,
            billing.IdentificationType,
            billing.IdentificationNumber,
            billing.Phone,
            billing.Email,
            billing.Address,
            place.Country,
            place.CityId,
            place.IsColombian ? null : place.CityName,
            classificationId);
    }

    private static List<StoreAddressInput> AddressInputs(StoreOrderRequest request)
    {
        var billing = request.Billing;
        var inputs = new List<StoreAddressInput>
        {
            new(billing.Name, billing.Address, billing.PhoneE164, billing.Place.Country, billing.Place.CityId,
                billing.Place.IsColombian ? null : billing.Place.CityName),
        };
        if (request.Shipping is { } shipping)
        {
            inputs.Add(new StoreAddressInput(
                shipping.EffectiveRecipientName(billing), shipping.Address, shipping.PhoneE164, shipping.Place.Country,
                shipping.Place.CityId, shipping.Place.IsColombian ? null : shipping.Place.CityName));
        }

        return inputs;
    }

    private static StoreOrderPartyInput BillingParty(StoreRequestBilling billing) =>
        new(
            billing.Name,
            billing.Phone,
            billing.Email,
            billing.Address,
            billing.Place.Country,
            billing.Place.DepartmentId,
            billing.Place.CityId,
            billing.Place.IsColombian ? null : billing.Place.CityName,
            billing.IdentificationType,
            billing.IdentificationNumber);

    // Sin «Usar los mismos datos» el destinatario es quien el visitante escribió; con él, la parte repite la facturación. En los dos
    // casos lleva el correo de facturación (el bloque de envío no tiene uno) y ningún documento: éste va sólo en facturación.
    private static StoreOrderPartyInput ShippingParty(StoreOrderRequest request)
    {
        var billing = request.Billing;
        if (request.Shipping is not { } shipping)
        {
            return BillingParty(billing) with { IdentificationType = null, IdentificationNumber = null };
        }

        return new StoreOrderPartyInput(
            shipping.EffectiveRecipientName(billing),
            shipping.Phone,
            billing.Email,
            shipping.Address,
            shipping.Place.Country,
            shipping.Place.DepartmentId,
            shipping.Place.CityId,
            shipping.Place.IsColombian ? null : shipping.Place.CityName,
            null,
            null);
    }
}
