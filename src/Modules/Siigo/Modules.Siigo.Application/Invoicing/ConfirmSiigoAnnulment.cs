using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using Microsoft.Extensions.Logging;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Invoicing;

/// <summary><c>POST …/orders/{orderId}/siigo-invoice/annulment</c> (<c>siigo.invoice.issue</c>), sin cuerpo: «Ya la anulé en Siigo».
/// Devuelve las filas actuales del pedido, la del pedido primero.</summary>
public sealed record ConfirmSiigoAnnulmentCommand(Guid TenantId, Guid OrderId) : ICommand<IReadOnlyList<SiigoInvoiceDto>>;

/// <summary>
/// 422: no se pudo confirmar la anulación y no cambió nada. <paramref name="code"/> es <c>siigo.invoice.annulment_unverifiable</c> (una
/// factura sin resolver o sin id de Siigo), <c>siigo.invoice.not_annulled</c> o <c>siigo.invoice.annulment_unclear</c> (respuesta no
/// reconocida o contradictoria). <c>details</c> dice cuál factura (<c>kind</c> y <c>number</c>), para que la pantalla la nombre.
/// </summary>
public sealed class SiigoAnnulmentRejectedException(string code, SiigoInvoiceKind kind, string? number)
    : DomainException(code, "The annulment of the Siigo invoice could not be confirmed; nothing changed."),
      IHasProblemExtensions
{
    public IReadOnlyDictionary<string, object?> ProblemExtensions { get; } = new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["details"] = new { kind = kind.ToString(), number },
    };
}

/// <summary>
/// Spec 2026-11-25 (anulación) §3: una persona anuló (o borró) en Siigo las facturas del pedido; QEP lo verifica leyendo cada factura
/// viva y, <b>sólo si todas lo están</b>, las pasa a <c>Annulled</c> y le quita al pedido la marca de facturado. Ante cualquier duda
/// rechaza sin cambiar nada: soltar un pedido con la factura viva deja facturarlo otra vez y duplica un documento legal.
/// <para>
/// <b>Siigo primero y el pedido después</b> (§6): las filas se guardan con el candado del pedido y la marca se quita fuera de la
/// transacción. Al revés, un fallo al guardar las filas dejaría el pedido sin marca con la factura todavía <c>Created</c>, y la
/// siguiente emisión lo volvería a marcar con el número anulado. Si lo que falla es la marca, repetir la acción la reintenta sin llamar
/// a Siigo.
/// </para>
/// </summary>
public sealed partial class ConfirmSiigoAnnulmentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IMembershipDirectory memberships,
    ISiigoOrderSource orders,
    ISiigoInvoiceRepository invoices,
    ISiigoUnitOfWork unitOfWork,
    ISiigoAuditRecorder auditRecorder,
    ISiigoConnectionDirectory connections,
    ISiigoApi api,
    IClock clock,
    ISiigoInvoiceBatchRepository batches,
    ILogger<ConfirmSiigoAnnulmentHandler> logger)
    : ICommandHandler<ConfirmSiigoAnnulmentCommand, IReadOnlyList<SiigoInvoiceDto>>
{
    private const string AnnulledOutcome = "annulled";
    private const string DeletedOutcome = "deleted";

    // Lo que Siigo documenta como aprobada por la DIAN: ésa no se anula.
    private const string AcceptedStampStatus = "Accepted";

    public async Task<IReadOnlyList<SiigoInvoiceDto>> HandleAsync(
        ConfirmSiigoAnnulmentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await SiigoInvoiceSupport.AuthorizeAsync(executionContext, tenantModules, command.TenantId, cancellationToken);
        var memberId = await memberships.FindActiveMembershipIdAsync(executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw SiigoAuthorization.Denied();
        var now = clock.UtcNow;

        IReadOnlyList<SiigoInvoice> rows;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            await unitOfWork.LockOrderAsync(command.OrderId, cancellationToken);
            rows = await invoices.ListByOrderAsync(command.TenantId, command.OrderId, cancellationToken);
            if (rows.Count == 0)
            {
                throw new ResourceNotFoundException(SiigoErrorCodes.InvoiceNotFound, "The order has no Siigo invoice.");
            }

            foreach (var row in rows)
            {
                row.ExpireIfStale(now);
            }

            // Soltar las filas mientras el worker puede estar por mandar el pedido es la misma carrera que «No existe».
            if (await batches.FindOpenBatchIdAsync(command.TenantId, command.OrderId, cancellationToken) is not null)
            {
                throw new SiigoInvoiceBatchOrderInProgressException([command.OrderId]);
            }

            // La del pedido primero: es la que se consulta primero y la que un rechazo nombra.
            var live = rows.Where(row => row.IsLive).OrderBy(row => row.Kind).ToList();
            if (live.Count > 0)
            {
                await AnnulAsync(live, now, cancellationToken);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            else if (AnnulledOrderRow(rows) is null)
            {
                // Sólo hay rechazadas o descartadas: no hay nada que anular. Con la del pedido ya anulada es un reintento de la marca.
                throw new SiigoDomainException(SiigoErrorCodes.InvoiceStateInvalid, "The order has no Siigo invoice to annul.");
            }
        }

        // Fuera de la transacción: nunca se toman a la vez el candado de Siigo y la escritura del pedido (otro DbContext).
        if (AnnulledOrderRow(rows) is { } annulled)
        {
            await TryUnmarkAsync(annulled, memberId, cancellationToken);
        }

        return await ToDtosAsync(SiigoInvoiceSupport.Current(rows), now, cancellationToken);
    }

    // Lo que se lee después del commit no tumba la respuesta (como en SiigoSavedInvoice): si no se sabe si una tanda tiene el pedido, se
    // trata como en cola, y una marca que no se puede leer no se avisa. La lista lo vuelve a decir.
    private async Task<IReadOnlyList<SiigoInvoiceDto>> ToDtosAsync(
        IReadOnlyList<SiigoInvoice> current, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var queued = await SiigoSavedInvoice.IsQueuedAsync(batches, current[0], logger, cancellationToken);
        var result = new List<SiigoInvoiceDto>(current.Count);
        foreach (var row in current)
        {
            var orderUnmarkFailed = await SiigoOrderMark.IsStillMarkedAfterAnnulmentAsync(orders, row, logger, cancellationToken);
            result.Add(SiigoInvoiceDto.From(
                row,
                now,
                queued: queued,
                canConfirmAnnulment: SiigoOrderMark.CanConfirmAnnulment(row, current, queued, orderUnmarkFailed),
                orderUnmarkFailed: orderUnmarkFailed));
        }

        return result;
    }

    // La última fila del pedido, si quedó anulada y con número: de ésa es la marca que hay que quitar.
    private static SiigoInvoice? AnnulledOrderRow(IEnumerable<SiigoInvoice> rows) =>
        rows.Where(row => row.Kind == SiigoInvoiceKind.Order).OrderByDescending(row => row.CreatedAt).FirstOrDefault()
            is { Status: SiigoInvoiceStatus.Annulled, Number: not null } latest
                ? latest
                : null;

    // Todo o nada: primero se confirman todas contra Siigo y sólo entonces se tocan las filas.
    private async Task AnnulAsync(List<SiigoInvoice> live, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (live.FirstOrDefault(row => !row.IsAnnulmentVerifiable) is { } unverifiable)
        {
            throw Rejected(SiigoErrorCodes.InvoiceAnnulmentUnverifiable, unverifiable);
        }

        var checks = new List<(SiigoInvoice Row, SiigoCredentials Credentials)>(live.Count);
        foreach (var row in live)
        {
            checks.Add((row, await SiigoInvoiceSupport.CredentialsAsync(connections, row.TenantId, row.ConnectionId, cancellationToken)));
        }

        var confirmed = new List<(SiigoInvoice Row, bool Deleted)>(checks.Count);
        foreach (var (row, credentials) in checks)
        {
            var lookup = await api.GetInvoiceAsync(credentials, row.SiigoInvoiceId!, cancellationToken);
            confirmed.Add((row, ConfirmedAsDeleted(row, lookup)));
        }

        foreach (var (row, deleted) in confirmed)
        {
            var fromStatus = row.Status.ToString();
            row.MarkAnnulled(deleted, now);
            SiigoInvoiceSupport.Audit(
                auditRecorder,
                executionContext,
                row,
                SiigoAuditActions.InvoiceAnnulled,
                now,
                deleted ? DeletedOutcome : AnnulledOutcome,
                fromStatus);
        }
    }

    // Si la respuesta confirma la anulación, dice si fue por borrado; si no la confirma, rechaza.
    private static bool ConfirmedAsDeleted(SiigoInvoice row, SiigoInvoiceLookup lookup) => lookup switch
    {
        SiigoInvoiceLookup.Found { Annulled: false } => throw Rejected(SiigoErrorCodes.InvoiceNotAnnulled, row),
        // Anulada y aprobada por la DIAN a la vez es una respuesta contradictoria.
        SiigoInvoiceLookup.Found found when !string.Equals(found.StampStatus, AcceptedStampStatus, StringComparison.OrdinalIgnoreCase) =>
            false,
        SiigoInvoiceLookup.NotFound => true,
        _ => throw Rejected(SiigoErrorCodes.InvoiceAnnulmentUnclear, row),
    };

    private static SiigoAnnulmentRejectedException Rejected(string code, SiigoInvoice row) => new(code, row.Kind, row.Number);

    // La anulación ya está guardada: que el pedido no se pueda soltar (otra escritura que ganó dos veces, la base caída) no hace fallar
    // la petición. Repetir la acción reintenta sólo la marca. El log lleva sólo el tipo de la excepción.
    private async Task TryUnmarkAsync(SiigoInvoice row, Guid memberId, CancellationToken cancellationToken)
    {
        try
        {
            await orders.UnmarkInvoicedAsync(row.TenantId, row.OrderId, row.Number!, memberId, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogUnmarkFailed(logger, exception.GetType().Name);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Releasing the order failed ({ExceptionType}) after its Siigo invoice was annulled; the annulment stays saved.")]
    private static partial void LogUnmarkFailed(ILogger logger, string exceptionType);
}
