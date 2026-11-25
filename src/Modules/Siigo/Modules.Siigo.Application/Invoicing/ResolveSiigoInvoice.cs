using BuildingBlocks.Application;
using FluentValidation;
using Microsoft.Extensions.Logging;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Invoicing;

/// <summary><c>POST …/orders/{orderId}/siigo-invoice/resolve</c> (<c>siigo.invoice.issue</c>). <paramref name="Outcome"/> es <c>exists</c>
/// (con <paramref name="Number"/>) o <c>missing</c>.</summary>
public sealed record ResolveSiigoInvoiceCommand(Guid TenantId, Guid OrderId, string? Kind, string? Outcome, string? Number)
    : ICommand<SiigoInvoiceDto>;

public sealed class ResolveSiigoInvoiceValidator : AbstractValidator<ResolveSiigoInvoiceCommand>
{
    public const string Exists = "exists";
    public const string Missing = "missing";

    public ResolveSiigoInvoiceValidator()
    {
        RuleFor(command => command.Kind).Must(SiigoInvoiceSupport.IsKind).OverridePropertyName("kind");
        RuleFor(command => command.Outcome)
            .Must(value => value is Exists or Missing)
            .OverridePropertyName("outcome");
        RuleFor(command => command.Number)
            .NotEmpty().When(command => command.Outcome == Exists)
            .MaximumLength(SiigoInvoice.NumberMaxLength)
            .OverridePropertyName("number");
    }
}

/// <summary>
/// Spec 2026-11-18 §5.6 y 2026-11-24 §3: una factura de la que no hay certeza la resuelve una persona que miró en Siigo, sin esperar a que
/// pasen 24 horas: una <c>Unknown</c>, o una <c>Pending</c> que no se esté mandando (<c>siigo.invoice.state_invalid</c> si no).
/// <c>exists</c> con el número: la fila queda <c>Created</c> y el pedido se marca con ese número (si no se puede, la resolución queda
/// guardada y la respuesta avisa con <c>orderMarkFailed</c>, salvo con el pedido en una tanda). <c>missing</c>: la fila pasa a
/// <c>Failed</c> y el pedido queda libre para facturar de nuevo; antes de los 10 minutos del último envío sin respuesta es 422
/// <c>siigo.invoice.resolve_too_early</c>, también en una <c>Unknown</c>. Al declarar que no existe la del pedido, la de flete sin
/// resolver (<c>Pending</c> o <c>Unknown</c>) se descarta en el mismo guardado, porque nunca salió; al declarar que existe, no se toca.
/// La de flete no se resuelve, ni <c>exists</c> ni <c>missing</c>, mientras la del pedido siga abierta (<c>Pending</c> o <c>Unknown</c>):
/// nunca salió (422 <c>siigo.invoice.state_invalid</c>). Con la del pedido facturada, rechazada o descartada, sí.
/// Mientras una tanda tenga el pedido (ítem <c>Pending</c> o <c>Sending</c>), <c>missing</c> es 409
/// <c>siigo.invoice_batch.order_in_progress</c>; <c>exists</c> sí se acepta, porque no manda nada.
/// Audita quién la resolvió, cómo y desde qué estado.
/// </summary>
public sealed partial class ResolveSiigoInvoiceHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IMembershipDirectory memberships,
    ISiigoOrderSource orders,
    ISiigoInvoiceRepository invoices,
    ISiigoUnitOfWork unitOfWork,
    ISiigoAuditRecorder auditRecorder,
    IClock clock,
    IValidator<ResolveSiigoInvoiceCommand> validator,
    ISiigoInvoiceBatchRepository batches,
    ILogger<ResolveSiigoInvoiceHandler> logger) : ICommandHandler<ResolveSiigoInvoiceCommand, SiigoInvoiceDto>
{
    public async Task<SiigoInvoiceDto> HandleAsync(ResolveSiigoInvoiceCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await SiigoInvoiceSupport.AuthorizeAsync(executionContext, tenantModules, command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var memberId = await memberships.FindActiveMembershipIdAsync(executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw SiigoAuthorization.Denied();
        var kind = SiigoInvoiceSupport.ParseKind(command.Kind);
        var now = clock.UtcNow;

        SiigoInvoice row;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            await unitOfWork.LockOrderAsync(command.OrderId, cancellationToken);
            var rows = await invoices.ListByOrderAsync(command.TenantId, command.OrderId, cancellationToken);
            row = rows
                .Where(value => value.Kind == kind && value.IsLive)
                .OrderByDescending(value => value.CreatedAt)
                .FirstOrDefault()
                ?? throw new ResourceNotFoundException(SiigoErrorCodes.InvoiceNotFound, "The order has no current Siigo invoice of that kind.");

            // La transición perezosa va primero: una Pending de más de un día es Unknown aunque nadie la haya leído todavía.
            row.ExpireIfStale(now);
            var fromStatus = row.Status.ToString();
            if (SiigoInvoiceSupport.IsFreightWaitingForTheOrder(row, rows))
            {
                throw new SiigoDomainException(
                    SiigoErrorCodes.InvoiceStateInvalid,
                    "The freight invoice has not been sent: resolve it only after the order invoice is settled.");
            }

            if (command.Outcome == ResolveSiigoInvoiceValidator.Exists)
            {
                row.ResolveExists(command.Number!, now);
            }
            else
            {
                // Una tanda ya tiene el pedido: declarar que no existe soltaría esta fila y el worker armaría otra factura con otra
                // clave, en vez de reenviar la misma. Si la primera sí existía en Siigo, quedaría duplicada.
                if (await batches.FindOpenBatchIdAsync(command.TenantId, command.OrderId, cancellationToken) is not null)
                {
                    throw new SiigoInvoiceBatchOrderInProgressException([command.OrderId]);
                }

                row.ResolveMissing(now);
                if (row.Kind == SiigoInvoiceKind.Order)
                {
                    DiscardFreightThatNeverLeft(rows, now);
                }
            }

            SiigoInvoiceSupport.Audit(
                auditRecorder, executionContext, row, SiigoAuditActions.InvoiceResolved, now, command.Outcome, fromStatus);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // «exists» marca el pedido con ese número, quien la resolvió.
        if (row.Kind == SiigoInvoiceKind.Order && row.Status == SiigoInvoiceStatus.Created)
        {
            await TryMarkAsync(row, memberId, cancellationToken);
        }

        return await SiigoSavedInvoice.ToDtoAsync(batches, orders, row, now, logger, cancellationToken);
    }

    // La resolución ya está guardada y repetirla respondería 422 (la fila ya es Created): que el pedido no se pueda marcar (anulado,
    // marcado a mano, otra escritura que ganó, la base caída) no hace fallar la petición. La respuesta lee la marca y avisa con
    // orderMarkFailed, salvo que una tanda tenga el pedido; «Generar» reintenta sólo la marca. El log lleva sólo el tipo de la excepción.
    private async Task TryMarkAsync(SiigoInvoice row, Guid memberId, CancellationToken cancellationToken)
    {
        try
        {
            await orders.MarkInvoicedAsync(
                row.TenantId, row.OrderId, row.Number!, SiigoInvoiceSupport.ExternalIdOf(row), memberId, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogMarkFailed(logger, exception.GetType().Name);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Marking the order failed ({ExceptionType}) after its Siigo invoice was resolved as existing; the resolution stays saved.")]
    private static partial void LogMarkFailed(ILogger logger, string exceptionType);

    // La de flete sólo sale después de que la del pedido existe, y acá la del pedido estaba sin resolver: la de flete sin resolver nunca
    // salió, aunque tenga anotado un envío sin respuesta o ya haya pasado a Unknown. Si siguiera viva, bloquearía el pedido.
    private static void DiscardFreightThatNeverLeft(IEnumerable<SiigoInvoice> rows, DateTimeOffset now)
    {
        foreach (var freight in rows.Where(value => value.Kind == SiigoInvoiceKind.Freight && value.IsOpen))
        {
            freight.MarkFailed(
                "siigo.invoice.order_rejected", "The order invoice was declared missing, so the freight invoice was not sent.", now);
        }
    }
}
