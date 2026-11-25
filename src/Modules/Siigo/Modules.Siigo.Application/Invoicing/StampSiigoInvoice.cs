using BuildingBlocks.Application;
using FluentValidation;
using Microsoft.Extensions.Logging;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Invoicing;

/// <summary><c>POST …/orders/{orderId}/siigo-invoice/stamp?kind=</c> (<c>siigo.invoice.issue</c>): reintenta el envío a la DIAN de una
/// factura ya creada. <paramref name="Kind"/> es <c>Order</c> por defecto.</summary>
public sealed record StampSiigoInvoiceCommand(Guid TenantId, Guid OrderId, string? Kind) : ICommand<SiigoInvoiceDto>;

public sealed class StampSiigoInvoiceValidator : AbstractValidator<StampSiigoInvoiceCommand>
{
    public StampSiigoInvoiceValidator() =>
        RuleFor(command => command.Kind).Must(SiigoInvoiceSupport.IsKind).OverridePropertyName("kind");
}

/// <summary>
/// Spec 2026-11-18 §5.4: la DIAN rechazó la factura (o el envío quedó pendiente) y, después de corregir en Siigo, la persona la vuelve a
/// mandar. Sólo una <c>Created</c> o <c>StampRejected</c> con id de Siigo; con el candado del pedido, para no estampar dos veces a la
/// vez. Aceptada → <c>Stamped</c> con el CUFE; rechazada → <c>StampRejected</c> con los errores (los del envío o, si no vienen, los de
/// <c>GET …/stamp/errors</c>); sin respuesta → 503 <c>siigo.unavailable</c> y la fila no cambia. El pedido no se toca: ya está marcado.
/// </summary>
public sealed class StampSiigoInvoiceHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISiigoConnectionDirectory connections,
    ISiigoApi api,
    ISiigoInvoiceRepository invoices,
    ISiigoUnitOfWork unitOfWork,
    IClock clock,
    IValidator<StampSiigoInvoiceCommand> validator,
    ISiigoInvoiceBatchRepository batches,
    ISiigoOrderSource orders,
    ILogger<StampSiigoInvoiceHandler> logger) : ICommandHandler<StampSiigoInvoiceCommand, SiigoInvoiceDto>
{
    public async Task<SiigoInvoiceDto> HandleAsync(StampSiigoInvoiceCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await SiigoInvoiceSupport.AuthorizeAsync(executionContext, tenantModules, command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var kind = SiigoInvoiceSupport.ParseKind(command.Kind);

        SiigoInvoice row;
        DateTimeOffset now;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            await unitOfWork.LockOrderAsync(command.OrderId, cancellationToken);
            row = (await invoices.ListByOrderAsync(command.TenantId, command.OrderId, cancellationToken))
                .Where(value => value.Kind == kind && value.IsLive)
                .OrderByDescending(value => value.CreatedAt)
                .FirstOrDefault()
                ?? throw new ResourceNotFoundException(SiigoErrorCodes.InvoiceNotFound, "The order has no current Siigo invoice of that kind.");
            if (!row.CanStamp)
            {
                throw new SiigoDomainException(
                    SiigoErrorCodes.InvoiceStateInvalid, "Only an invoice created in Siigo and not yet sent to the DIAN can be stamped.");
            }

            var credentials = await SiigoInvoiceSupport.CredentialsAsync(connections, command.TenantId, row.ConnectionId, cancellationToken);
            now = clock.UtcNow;
            switch (await api.StampInvoiceAsync(credentials, row.SiigoInvoiceId!, cancellationToken))
            {
                case SiigoStampOutcome.Stamped stamped:
                    row.MarkStamped(stamped.Cufe, now);
                    break;
                case SiigoStampOutcome.Rejected rejected when rejected.Errors.Any(error => error.Code == SiigoApiErrors.CredentialsRejected):
                    // La clave de acceso ya no sirve: no es un rechazo de la DIAN, es la conexión.
                    throw new ResourceConflictException(SiigoErrorCodes.ConnectionUnavailable, "Siigo rejected the credentials of the connection.");
                case SiigoStampOutcome.Rejected rejected when SiigoApiErrors.IsServiceUnavailable(rejected.Errors):
                    // Un servicio de Siigo caído no es un rechazo de la DIAN: la factura sigue Created y se reintenta.
                    throw new ServiceUnavailableException(SiigoErrorCodes.Unavailable, "Siigo service is unavailable; try sending the invoice to the DIAN again.");
                case SiigoStampOutcome.Rejected rejected:
                    var errors = rejected.Errors.Count > 0
                        ? rejected.Errors
                        : await api.GetStampErrorsAsync(credentials, row.SiigoInvoiceId!, cancellationToken);
                    row.MarkStampRejected(
                        errors.Count > 0 ? errors[0].Code : "siigo.invoice.stamp_rejected", SiigoInvoiceSupport.ErrorDetailOf(errors), now);
                    break;
                default:
                    throw new ServiceUnavailableException(SiigoErrorCodes.Unavailable, "Siigo did not answer; try sending the invoice to the DIAN again.");
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return await SiigoSavedInvoice.ToDtoAsync(batches, orders, row, now, logger, cancellationToken);
    }
}
