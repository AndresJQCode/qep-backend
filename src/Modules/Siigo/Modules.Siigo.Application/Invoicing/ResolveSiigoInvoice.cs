using BuildingBlocks.Application;
using FluentValidation;
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
/// <c>exists</c> con el número: la fila queda <c>Created</c> y el pedido se marca con ese número. <c>missing</c>: la fila pasa a
/// <c>Failed</c> y el pedido queda libre para facturar de nuevo; antes de los 10 minutos del último envío sin respuesta es 422
/// <c>siigo.invoice.resolve_too_early</c>. Al declarar que no existe la del pedido, la de flete que nunca salió se descarta en el mismo
/// guardado; una de flete sin respuesta se resuelve aparte. Audita quién la resolvió, cómo y desde qué estado.
/// </summary>
public sealed class ResolveSiigoInvoiceHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IMembershipDirectory memberships,
    ISiigoOrderSource orders,
    ISiigoInvoiceRepository invoices,
    ISiigoUnitOfWork unitOfWork,
    ISiigoAuditRecorder auditRecorder,
    IClock clock,
    IValidator<ResolveSiigoInvoiceCommand> validator) : ICommandHandler<ResolveSiigoInvoiceCommand, SiigoInvoiceDto>
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
            if (command.Outcome == ResolveSiigoInvoiceValidator.Exists)
            {
                row.ResolveExists(command.Number!, now);
            }
            else
            {
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

        // «exists» marca el pedido con ese número, quien la resolvió. Idempotente: si falla acá, repetirlo no duplica nada.
        if (row.Kind == SiigoInvoiceKind.Order && row.Status == SiigoInvoiceStatus.Created)
        {
            await orders.MarkInvoicedAsync(command.TenantId, command.OrderId, row.Number!, SiigoInvoiceSupport.ExternalIdOf(row), memberId, cancellationToken);
        }

        return SiigoInvoiceDto.From(row, now);
    }

    // La de flete sólo sale después de la del pedido: una Pending sin envío sin respuesta nunca salió. Si siguiera viva, bloquearía el
    // pedido para siempre.
    private static void DiscardFreightThatNeverLeft(IEnumerable<SiigoInvoice> rows, DateTimeOffset now)
    {
        foreach (var freight in rows.Where(value =>
            value is { Kind: SiigoInvoiceKind.Freight, Status: SiigoInvoiceStatus.Pending, UnansweredAt: null }))
        {
            freight.MarkFailed(
                "siigo.invoice.order_rejected", "The order invoice was declared missing, so the freight invoice was not sent.", now);
        }
    }
}
