using System.Text.Json;
using BuildingBlocks.Application;
using BuildingBlocks.Domain;
using FluentValidation;
using Microsoft.Extensions.Logging;
using Modules.Audit.Domain;
using Modules.Siigo.Application.CallFailures;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Invoicing;

/// <summary>Cuántos pedidos de una tanda están en un estado. BFF (spec 2026-11-24 §5): viajan los seis, en orden fijo e incluso en cero,
/// para que la pantalla no tenga que conocer el enum del backend para dibujar el que falta.</summary>
public sealed record SiigoInvoiceBatchStatusCountDto(string Status, int Count)
{
    public static IReadOnlyList<SiigoInvoiceBatchStatusCountDto> From(IReadOnlyCollection<SiigoBatchItemState> states) =>
        [
            .. SiigoBatchItemState.CountOrder.Select(status =>
                new SiigoInvoiceBatchStatusCountDto(status.ToString(), states.Count(state => state.Status == status))),
        ];
}

/// <summary>Una tanda de facturación con Siigo y el avance de cada pedido. BFF: <c>status</c> es <c>InProgress</c> o <c>Finished</c>, para que
/// la pantalla sepa cuándo dejar de consultar; <c>statusCounts</c> trae los seis estados contados para pintar el resumen sin recorrer los
/// ítems (<c>invoiced</c>, <c>skipped</c> y <c>failed</c> se quedan porque la pantalla publicada los lee). Todo sale del estado
/// <b>efectivo</b> de cada pedido (spec 2026-11-24 §2): uno que quedó por confirmar sigue a su factura, así que una tanda <c>Finished</c>
/// puede mostrar uno <c>Sending</c> porque otra lo está reenviando. Es <c>Finished</c> cuando la tanda quedó cerrada o cuando ningún pedido
/// guardado le queda por procesar, y <c>finishedAt</c> siempre viene en una <c>Finished</c>: en una que quedó sin cerrar es la hora del
/// último pedido resuelto (<see cref="SiigoInvoiceBatch.ReadFinishedAt"/>). Nunca lleva la petición a Siigo, la clave ni sus
/// mensajes.</summary>
public sealed record SiigoInvoiceBatchDto(
    Guid Id,
    string Status,
    int Total,
    int Invoiced,
    int Skipped,
    int Failed,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<SiigoInvoiceBatchItemDto> Items,
    IReadOnlyList<SiigoInvoiceBatchStatusCountDto> StatusCounts)
{
    /// <param name="invoices">Las filas de factura enlazadas a los ítems, por id.</param>
    /// <param name="labels">Número y cliente de los pedidos de la tanda, por id de pedido.</param>
    /// <param name="queuedOrderIds">Los pedidos de la tanda que están en un ítem sin terminar, de ésta o de otra.</param>
    /// <param name="freights">La de flete de cada factura del pedido que existe en Siigo, por el id de ésta.</param>
    /// <param name="unmarkedOrderIds">Los que al leer tienen la factura en Siigo y el pedido sin la marca.</param>
    public static SiigoInvoiceBatchDto From(
        SiigoInvoiceBatch batch,
        IReadOnlyDictionary<Guid, SiigoInvoice> invoices,
        IReadOnlyDictionary<Guid, SiigoOrderLabel> labels,
        IReadOnlyCollection<Guid> queuedOrderIds,
        DateTimeOffset now,
        IReadOnlyDictionary<Guid, SiigoInvoice>? freights = null,
        IReadOnlyCollection<Guid>? unmarkedOrderIds = null)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var items = batch.Items
            .OrderBy(item => item.Position)
            .Select(item =>
            {
                var invoice = SiigoInvoiceBatchViews.Linked(item, invoices);
                return (Item: item, Invoice: invoice,
                    State: item.Effective(invoice, now, SiigoInvoiceBatchViews.Linked(item, freights)));
            })
            .ToList();
        var states = items.Select(row => row.State).ToList();
        var queued = queuedOrderIds.ToHashSet();
        var finishedAt = batch.ReadFinishedAt();
        return new(
            batch.Id,
            SiigoInvoiceBatchViews.StatusOf(finishedAt),
            items.Count,
            states.Count(state => state.Status == SiigoInvoiceBatchItemStatus.Invoiced),
            states.Count(state => state.Status == SiigoInvoiceBatchItemStatus.Skipped),
            states.Count(state => state.Status == SiigoInvoiceBatchItemStatus.Failed),
            batch.CreatedAt,
            finishedAt,
            [
                .. items.Select(row => SiigoInvoiceBatchItemDto.From(
                    row.Item,
                    row.State,
                    row.Invoice,
                    labels.GetValueOrDefault(row.Item.OrderId),
                    queued.Contains(row.Item.OrderId),
                    unmarkedOrderIds?.Contains(row.Item.OrderId) ?? false)),
            ],
            SiigoInvoiceBatchStatusCountDto.From(states));
    }
}

/// <summary>
/// Un pedido de una tanda como la pantalla lo dibuja (spec 2026-11-24 §5). <paramref name="Status"/> y <paramref name="ErrorCode"/> son
/// los efectivos. <paramref name="OrderNumber"/> y <paramref name="CustomerName"/> son los de <c>/orders</c>, leídos al momento (<c>null</c>
/// si el pedido no es del tenant): la pantalla de tandas no tiene las filas del listado. <paramref name="SiigoErrors"/> son los códigos que
/// contestó Siigo, en lista para que la pantalla no parta texto, y nunca sus mensajes; van vacíos en uno por confirmar cuyo
/// <paramref name="ErrorCode"/> no es el de su fila (el motivo de un reenvío que no salió), para no mezclar dos intentos.
/// <paramref name="Missing"/> trae, para un pedido
/// saltado por <c>siigo.invoice.not_ready</c>, los códigos de lo que le faltaba (la pantalla los traduce igual que los del <c>preview</c>).
/// <paramref name="OrderMarkFailed"/> avisa que la factura existe pero el pedido no se pudo marcar; en uno que quedó por confirmar se
/// calcula al leer el detalle y en uno cuya factura se anuló en Siigo va en <c>false</c>. <paramref name="CanResend"/>,
/// <paramref name="CanResolve"/> y <paramref name="MissingAvailableAt"/> (desde cuándo se acepta «No existe», si todavía hay que esperar)
/// van calculados porque la pantalla no ve desde cuándo se está mandando ni el reloj de 24 horas. <paramref name="CanResend"/> va en
/// <c>false</c> mientras una tanda sin terminar tenga el pedido (otra respondería 409), aunque este ítem siga por confirmar.
/// <paramref name="CanResolveMissing"/> es <paramref name="CanResolve"/> con la misma salvedad: «No existe» respondería 409, «Existe» no.
/// </summary>
public sealed record SiigoInvoiceBatchItemDto(
    Guid OrderId,
    int Position,
    string? OrderNumber,
    string? CustomerName,
    string Status,
    string? ErrorCode,
    IReadOnlyList<string> SiigoErrors,
    Guid? InvoiceId,
    string? InvoiceNumber,
    IReadOnlyList<string> Missing,
    bool OrderMarkFailed,
    bool CanResend,
    bool CanResolve,
    DateTimeOffset? MissingAvailableAt,
    bool CanResolveMissing = false)
{
    /// <param name="queued">Un ítem sin terminar tiene el pedido.</param>
    /// <param name="unmarked">La lectura vio la factura en Siigo y el pedido sin la marca.</param>
    public static SiigoInvoiceBatchItemDto From(
        SiigoInvoiceBatchItem item,
        SiigoBatchItemState state,
        SiigoInvoice? invoice,
        SiigoOrderLabel? label,
        bool queued,
        bool unmarked = false)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(state);
        // El detalle de la fila explica el código de la fila: si por confirmar se muestra otro (el del ítem), o uno de la de flete,
        // es de otro intento.
        var explainsAnotherCode = state.ErrorCode != invoice?.ErrorCode
            && (state.Status == SiigoInvoiceBatchItemStatus.ToConfirm
                || state.ErrorCode is SiigoErrorCodes.InvoiceFreightPending or SiigoErrorCodes.InvoiceFreightNotCreated);
        return new(
            item.OrderId,
            item.Position,
            label?.OrderNumber,
            label?.CustomerName,
            state.Status.ToString(),
            state.ErrorCode,
            CodesOf(explainsAnotherCode ? null : invoice?.ErrorDetail),
            item.InvoiceId,
            state.InvoiceNumber,
            item.MissingCodes,
            // El aviso guardado era de una factura que ya se anuló: no queda número que marcar.
            invoice?.Status != SiigoInvoiceStatus.Annulled && (item.OrderMarkFailed || unmarked),
            !queued && state.CanResend,
            state.CanResolve,
            state.MissingAvailableAt,
            !queued && state.CanResolve);
    }

    // error_detail va como «codigo; invalid_total_payments: total»: de cada parte sólo el código. Lo que traiga espacios no lo es.
    private static string[] CodesOf(string? errorDetail) =>
        errorDetail is null
            ? []
            : [.. errorDetail.Split("; ")
                .Select(part => part.Split(':')[0].Trim())
                .Where(code => code.Length > 0 && !code.Any(char.IsWhiteSpace))];
}

/// <summary>Una tanda en la lista (spec 2026-11-24 §5). BFF: <paramref name="StatusCounts"/> trae los seis estados efectivos, incluso en
/// cero, así la pantalla pinta el avance de cada fila sin pedir el detalle. <paramref name="Status"/> y <paramref name="FinishedAt"/> salen
/// igual que en el detalle.</summary>
public sealed record SiigoInvoiceBatchSummaryDto(
    Guid Id,
    string Status,
    int Total,
    IReadOnlyList<SiigoInvoiceBatchStatusCountDto> StatusCounts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt)
{
    public static SiigoInvoiceBatchSummaryDto From(
        SiigoInvoiceBatch batch,
        IReadOnlyDictionary<Guid, SiigoInvoice> invoices,
        DateTimeOffset now,
        IReadOnlyDictionary<Guid, SiigoInvoice>? freights = null)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var states = batch.Items
            .Select(item => item.Effective(
                SiigoInvoiceBatchViews.Linked(item, invoices), now, SiigoInvoiceBatchViews.Linked(item, freights)))
            .ToList();
        var finishedAt = batch.ReadFinishedAt();
        return new(
            batch.Id,
            SiigoInvoiceBatchViews.StatusOf(finishedAt),
            batch.Items.Count,
            SiigoInvoiceBatchStatusCountDto.From(states),
            batch.CreatedAt,
            finishedAt);
    }
}

/// <summary>Las tandas del tenant, de la más nueva a la más vieja. <paramref name="Total"/> las cuenta todas, para el paginador.</summary>
public sealed record SiigoInvoiceBatchPageDto(
    IReadOnlyList<SiigoInvoiceBatchSummaryDto> Items, int Total, int Page, int PageSize);

/// <summary>Lo que comparten las lecturas de las tandas.</summary>
internal static class SiigoInvoiceBatchViews
{
    /// <param name="finishedAt">El de <see cref="SiigoInvoiceBatch.ReadFinishedAt"/>.</param>
    public static string StatusOf(DateTimeOffset? finishedAt) => finishedAt is null ? "InProgress" : "Finished";

    /// <summary>Lo que el diccionario tiene para la factura enlazada al ítem, o <c>null</c>.</summary>
    public static SiigoInvoice? Linked(SiigoInvoiceBatchItem item, IReadOnlyDictionary<Guid, SiigoInvoice>? invoices) =>
        item.InvoiceId is { } id ? invoices?.GetValueOrDefault(id) : null;

    /// <summary>Las filas de factura enlazadas a esos ítems, por id, en una sola consulta.</summary>
    public static async Task<IReadOnlyDictionary<Guid, SiigoInvoice>> LinkedInvoicesAsync(
        ISiigoInvoiceRepository invoices,
        Guid tenantId,
        IEnumerable<SiigoInvoiceBatchItem> items,
        CancellationToken cancellationToken)
    {
        Guid[] ids = [.. items.Select(item => item.InvoiceId).OfType<Guid>().Distinct()];
        return (await invoices.ListByIdsAsync(tenantId, ids, cancellationToken)).ToDictionary(invoice => invoice.Id);
    }

    /// <summary>La fila enlazada de un pedido facturado o por confirmar cuya factura existe en Siigo, o <c>null</c>. Sólo en ésos la
    /// lectura mira la de flete.</summary>
    public static SiigoInvoice? InvoiceInSiigoOf(
        SiigoInvoiceBatchItem item, IReadOnlyDictionary<Guid, SiigoInvoice> invoices) =>
        item.Status is SiigoInvoiceBatchItemStatus.Invoiced or SiigoInvoiceBatchItemStatus.ToConfirm
        && Linked(item, invoices) is
        {
            Status: SiigoInvoiceStatus.Created or SiigoInvoiceStatus.Stamped or SiigoInvoiceStatus.StampRejected,
        } invoice
            ? invoice
            : null;

    /// <summary>La de flete de cada una de esas facturas, por el id de la del pedido, en una sola consulta: la más nueva creada con ella o
    /// después. Una anterior es de un intento del pedido que ya no vale.</summary>
    public static async Task<IReadOnlyDictionary<Guid, SiigoInvoice>> FreightsAsync(
        ISiigoInvoiceRepository invoices,
        Guid tenantId,
        IEnumerable<SiigoInvoiceBatchItem> items,
        IReadOnlyDictionary<Guid, SiigoInvoice> linked,
        CancellationToken cancellationToken)
    {
        SiigoInvoice[] orderInvoices =
            [.. items.Select(item => InvoiceInSiigoOf(item, linked)).OfType<SiigoInvoice>().DistinctBy(invoice => invoice.Id)];
        Guid[] orderIds = [.. orderInvoices.Select(invoice => invoice.OrderId).Distinct()];
        var byOrder = (await invoices.ListFreightAsync(tenantId, orderIds, cancellationToken)).ToLookup(freight => freight.OrderId);
        var freights = new Dictionary<Guid, SiigoInvoice>();
        foreach (var invoice in orderInvoices)
        {
            if (byOrder[invoice.OrderId]
                    .Where(freight => freight.CreatedAt >= invoice.CreatedAt)
                    .MaxBy(freight => freight.CreatedAt) is { } freight)
            {
                freights[invoice.Id] = freight;
            }
        }

        return freights;
    }
}

/// <summary>Las tandas de facturación (<c>siigo.invoice_batches</c>). Con tracking: el llamador guarda con <see cref="ISiigoUnitOfWork"/>.
/// Todo filtra por <c>tenant_id</c>.</summary>
public interface ISiigoInvoiceBatchRepository
{
    void Add(SiigoInvoiceBatch batch);

    /// <summary>La tanda del tenant con sus pedidos, o <c>null</c> si no existe o es de otro tenant.</summary>
    Task<SiigoInvoiceBatch?> FindAsync(Guid tenantId, Guid batchId, CancellationToken cancellationToken);

    /// <summary>De esos pedidos, los que ya están en un ítem sin terminar (<c>Pending</c> o <c>Sending</c>). Sin tracking.</summary>
    Task<IReadOnlyList<Guid>> ListOpenOrderIdsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> orderIds, CancellationToken cancellationToken);

    /// <summary>La tanda que tiene a ese pedido sin terminar, o <c>null</c>.</summary>
    Task<Guid?> FindOpenBatchIdAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken);

    /// <summary>La página pedida (<paramref name="page"/> desde 1) con sus pedidos, de la más nueva a la más vieja, y cuántas tandas
    /// tiene el tenant.</summary>
    Task<(IReadOnlyList<SiigoInvoiceBatch> Items, int Total)> SearchAsync(
        Guid tenantId, int page, int pageSize, CancellationToken cancellationToken);
}

/// <summary>Lo que pasó al facturar un pedido de una tanda. Una unión cerrada que decide el ejecutor y aplica el worker.</summary>
public abstract record SiigoBatchItemOutcome
{
    private SiigoBatchItemOutcome()
    {
    }

    /// <summary>La factura existe en Siigo (creada, estampada, o rechazada por la DIAN pero creada): el número que le dio y su fila.
    /// <paramref name="OrderMarkFailed"/> dice que el pedido no se pudo marcar.</summary>
    public sealed record Invoiced(string Number, Guid? InvoiceId = null, bool OrderMarkFailed = false) : SiigoBatchItemOutcome;

    /// <summary>El pedido no estaba en condiciones: no listo, ya facturado, anulado, sin pagar… No se llamó a Siigo para crear nada.</summary>
    public sealed record Skipped(string ErrorCode, IReadOnlyList<string> Missing) : SiigoBatchItemOutcome;

    /// <summary>No se facturó y se sabe: Siigo la rechazó por un dato, la conexión no sirve, quien la pidió ya no puede facturar o el envío
    /// se cortó antes de salir. <paramref name="InvoiceId"/> viene si quedó una fila. No se programa un reintento (owner,
    /// 2026-11-24). Si el pedido ya tenía una factura por confirmar, <c>Unknown</c> o en camino, no es éste sino <see cref="ToConfirm"/>;
    /// si ya la tenía creada y no queda una de flete <c>Pending</c>, es <see cref="Invoiced"/>. Es también el de un pedido cuya factura
    /// existe y cuya de flete sigue <c>Pending</c> (decidido el 2026-11-25): va sin <paramref name="InvoiceId"/> y con
    /// <c>siigo.invoice.freight_pending</c>, aunque lo que no se pudo haya sido el reenvío.</summary>
    public sealed record Failed(string ErrorCode, Guid? InvoiceId = null) : SiigoBatchItemOutcome;

    /// <summary>Se mandó (o se está mandando) y no se sabe si Siigo la creó: la fila sigue viva con la misma clave y la decide una persona
    /// (spec 2026-11-24 §2). <paramref name="ErrorCode"/> es el de la fila o, si este reenvío no se pudo hacer, el motivo.</summary>
    public sealed record ToConfirm(Guid InvoiceId, string? ErrorCode) : SiigoBatchItemOutcome;
}

/// <summary>
/// Factura un pedido de una tanda <b>como la persona que la pidió</b>. El adaptador (Bootstrapper) resuelve los permisos que esa persona tiene
/// <b>ahora</b>, corre <see cref="IssueSiigoInvoiceCommand"/> con ellos y traduce el resultado o la excepción a un
/// <see cref="SiigoBatchItemOutcome"/>. Nunca lanza por el pedido: lo que pasa con un pedido es un resultado, no una excepción.
/// </summary>
public interface ISiigoBatchItemInvoker
{
    /// <param name="payments">Las formas de pago elegidas al pedir la tanda de un pedido; <c>null</c> usa la propuesta de la cuenta de
    /// cobro.</param>
    Task<SiigoBatchItemOutcome> InvokeAsync(
        Guid tenantId,
        Guid requestedBy,
        Guid orderId,
        IReadOnlyList<SiigoPaymentChoice>? payments,
        CancellationToken cancellationToken);
}

/// <summary><c>POST …/siigo/invoice-batches</c> (<c>siigo.invoice.issue</c>). <paramref name="Payments"/> es opcional y sólo vale en una
/// tanda de un pedido: las formas de pago son por comprobante de ese pedido.</summary>
public sealed record CreateSiigoInvoiceBatchCommand(
    Guid TenantId, IReadOnlyList<Guid>? OrderIds, IReadOnlyList<SiigoPaymentChoice>? Payments = null) : ICommand<SiigoInvoiceBatchDto>;

/// <summary>409 <c>siigo.invoice_batch.order_in_progress</c>: esos pedidos ya están en una tanda sin terminar; <c>details.orderIds</c>
/// le dice a la pantalla cuáles.</summary>
public sealed class SiigoInvoiceBatchOrderInProgressException(IReadOnlyList<Guid> orderIds)
    : ResourceConflictException(SiigoErrorCodes.InvoiceBatchOrderInProgress, "An order is already in an unfinished invoice batch."),
      IHasProblemExtensions
{
    public IReadOnlyDictionary<string, object?> ProblemExtensions { get; } = new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["details"] = new { orderIds },
    };
}

/// <summary>La forma del cuerpo (422 <c>validation.failed</c> con el campo en camelCase): entre 1 y 50 pedidos, sin vacíos ni repetidos;
/// las formas de pago, con las reglas del comando de emisión y un solo pedido.</summary>
public sealed class CreateSiigoInvoiceBatchValidator : AbstractValidator<CreateSiigoInvoiceBatchCommand>
{
    public CreateSiigoInvoiceBatchValidator()
    {
        RuleFor(command => command.OrderIds)
            .NotNull()
            .Must(ids => ids is { Count: >= 1 and <= SiigoInvoiceBatch.MaxItems })
            .WithMessage($"A batch holds between 1 and {SiigoInvoiceBatch.MaxItems} orders.")
            .Must(ids => ids is null || (ids.All(id => id != Guid.Empty) && ids.Distinct().Count() == ids.Count))
            .WithMessage("The orders of a batch are required and cannot repeat.")
            .OverridePropertyName("orderIds");
        RuleForEach(command => command.Payments).NotNull().ChildRules(SiigoPaymentChoiceRules.Configure);
        RuleFor(command => command.Payments)
            .Must((command, payments) => payments is not { Count: > 0 } || command.OrderIds is { Count: 1 })
            .WithMessage("Payment choices only travel with a batch of a single order.")
            .OverridePropertyName("payments");
    }
}

/// <summary>
/// Crea la tanda y sus ítems <c>Pending</c> en una sola transacción. El <c>INSERT</c> de los ítems timbra al worker solo (<c>QueueDoorbellInterceptor</c>). No llama a Siigo ni mira los
/// pedidos: cada uno se revalida cuando le toca, con el handler de siempre, y una tanda con un pedido ajeno o inexistente lo ve saltado. Un
/// pedido que ya está en una tanda sin terminar es 409: la consulta lo dice antes de guardar y, si otra tanda lo toma entre medias, lo
/// frena el índice único. La auditoría lleva cuántos pedidos, si traía formas de pago y quién la pidió.
/// </summary>
public sealed class CreateSiigoInvoiceBatchHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISiigoInvoiceBatchRepository batches,
    ISiigoOrderSource orders,
    ISiigoUnitOfWork unitOfWork,
    ISiigoAuditRecorder auditRecorder,
    IClock clock,
    IValidator<CreateSiigoInvoiceBatchCommand> validator) : ICommandHandler<CreateSiigoInvoiceBatchCommand, SiigoInvoiceBatchDto>
{
    public async Task<SiigoInvoiceBatchDto> HandleAsync(CreateSiigoInvoiceBatchCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await SiigoInvoiceSupport.AuthorizeAsync(executionContext, tenantModules, command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var inProgress = await batches.ListOpenOrderIdsAsync(command.TenantId, command.OrderIds!, cancellationToken);
        if (inProgress.Count > 0)
        {
            throw new SiigoInvoiceBatchOrderInProgressException(inProgress);
        }

        var now = clock.UtcNow;
        var paymentsJson = SiigoBatchPayments.Serialize(command.Payments);
        var batch = SiigoInvoiceBatch.Create(
            Guid.CreateVersion7(), command.TenantId, executionContext.SubjectId, command.OrderIds!, now, paymentsJson);
        batches.Add(batch);
        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            AuditActorType.Human,
            SiigoAuditActions.InvoiceBatchRequested,
            SiigoAuditActions.InvoiceBatchResourceType,
            batch.Id.ToString(),
            JsonSerializer.Serialize(new { orders = batch.Items.Count, withPayments = paymentsJson is not null }),
            now);
        // Antes de guardar: si esta lectura falla, no queda una tanda creada detrás de un error.
        var labels = await orders.ListLabelsAsync(command.TenantId, command.OrderIds!, cancellationToken);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ResourceConflictException conflict) when (conflict.Code == SiigoErrorCodes.InvoiceBatchOrderInProgress)
        {
            // Otra tanda tomó el pedido entre la consulta y el guardado: se vuelve a preguntar cuáles para dar el mismo 409.
            throw new SiigoInvoiceBatchOrderInProgressException(
                await batches.ListOpenOrderIdsAsync(command.TenantId, command.OrderIds!, cancellationToken));
        }

        // Recién creada, ningún pedido tiene factura enlazada y la tanda los tiene a todos.
        return SiigoInvoiceBatchDto.From(batch, new Dictionary<Guid, SiigoInvoice>(), labels, command.OrderIds!, now);
    }
}

/// <summary><c>GET …/siigo/invoice-batches/{batchId}</c> (<c>siigo.invoice.issue</c>).</summary>
public sealed record GetSiigoInvoiceBatchQuery(Guid TenantId, Guid BatchId) : IQuery<SiigoInvoiceBatchDto>;

/// <summary>El avance de una tanda. De otro tenant es 404 como si no existiera (la doble capa ya dio 403 si el tenant de la ruta no es el del
/// llamador).</summary>
public sealed class GetSiigoInvoiceBatchHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISiigoInvoiceBatchRepository batches,
    ISiigoInvoiceRepository invoices,
    ISiigoOrderSource orders,
    IClock clock,
    ILogger<GetSiigoInvoiceBatchHandler> logger)
    : IQueryHandler<GetSiigoInvoiceBatchQuery, SiigoInvoiceBatchDto>
{
    public async Task<SiigoInvoiceBatchDto> HandleAsync(GetSiigoInvoiceBatchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await SiigoInvoiceSupport.AuthorizeAsync(executionContext, tenantModules, query.TenantId, cancellationToken);
        var batch = await batches.FindAsync(query.TenantId, query.BatchId, cancellationToken)
            ?? throw new ResourceNotFoundException(SiigoErrorCodes.InvoiceBatchNotFound, "The invoice batch was not found.");
        var linked = await SiigoInvoiceBatchViews.LinkedInvoicesAsync(invoices, query.TenantId, batch.Items, cancellationToken);
        Guid[] orderIds = [.. batch.Items.Select(item => item.OrderId)];
        var labels = await orders.ListLabelsAsync(query.TenantId, orderIds, cancellationToken);
        // Una sola pregunta por todos los pedidos, después de leer las facturas.
        var queued = await batches.ListOpenOrderIdsAsync(query.TenantId, orderIds, cancellationToken);
        var freights = await SiigoInvoiceBatchViews.FreightsAsync(invoices, query.TenantId, batch.Items, linked, cancellationToken);
        // Un ítem que quedó por confirmar no guarda si el pedido se marcó: cuando su factura ya existe, se pregunta al leer.
        var unmarked = new HashSet<Guid>();
        foreach (var item in batch.Items)
        {
            if (item.Status == SiigoInvoiceBatchItemStatus.ToConfirm
                && SiigoInvoiceBatchViews.InvoiceInSiigoOf(item, linked) is { } invoice
                && await SiigoOrderMark.IsMissingAsync(orders, invoice, queued.Contains(item.OrderId), logger, cancellationToken))
            {
                unmarked.Add(item.OrderId);
            }
        }

        return SiigoInvoiceBatchDto.From(batch, linked, labels, queued, clock.UtcNow, freights, unmarked);
    }
}

/// <summary><c>GET …/siigo/invoice-batches?page&amp;pageSize</c> (<c>siigo.invoice.issue</c>). Los dos llegan como texto: un valor ilegible
/// es 422 <c>validation.failed</c> con su campo, como en las fallas de Siigo.</summary>
public sealed record ListSiigoInvoiceBatchesQuery(Guid TenantId, string? Page, string? PageSize) : IQuery<SiigoInvoiceBatchPageDto>;

public sealed class ListSiigoInvoiceBatchesValidator : AbstractValidator<ListSiigoInvoiceBatchesQuery>
{
    public ListSiigoInvoiceBatchesValidator()
    {
        RuleFor(query => query.Page)
            .Must(value => ListSiigoCallFailuresQueryParser.TryInt(value, out _))
            .When(query => !string.IsNullOrWhiteSpace(query.Page))
            .WithMessage("The page must be a whole number.")
            .OverridePropertyName("page");
        RuleFor(query => query.PageSize)
            .Must(value => ListSiigoCallFailuresQueryParser.TryInt(value, out _))
            .When(query => !string.IsNullOrWhiteSpace(query.PageSize))
            .WithMessage("The page size must be a whole number.")
            .OverridePropertyName("pageSize");
    }
}

/// <summary>
/// Spec 2026-11-24 §5: todas las tandas del tenant, no sólo las propias, como el <c>GET</c> por id. En orden: tenant, permiso y módulo
/// (403, nunca 404); la forma (422). Los contadores salen del estado efectivo, así que se leen las filas de los pedidos por confirmar de
/// la página.
/// </summary>
public sealed class ListSiigoInvoiceBatchesHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISiigoInvoiceBatchRepository batches,
    ISiigoInvoiceRepository invoices,
    IClock clock,
    IValidator<ListSiigoInvoiceBatchesQuery> validator) : IQueryHandler<ListSiigoInvoiceBatchesQuery, SiigoInvoiceBatchPageDto>
{
    public async Task<SiigoInvoiceBatchPageDto> HandleAsync(ListSiigoInvoiceBatchesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await SiigoInvoiceSupport.AuthorizeAsync(executionContext, tenantModules, query.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var page = SiigoCallFailurePaging.NormalizePage(Int(query.Page) ?? 1);
        var pageSize = SiigoCallFailurePaging.NormalizePageSize(Int(query.PageSize) ?? 0);
        var (items, total) = await batches.SearchAsync(query.TenantId, page, pageSize, cancellationToken);
        // Sólo un pedido guardado por confirmar o facturado sigue a sus facturas: de los demás no hace falta la fila.
        SiigoInvoiceBatchItem[] following =
        [
            .. items.SelectMany(batch => batch.Items)
                .Where(item => item.Status is SiigoInvoiceBatchItemStatus.ToConfirm or SiigoInvoiceBatchItemStatus.Invoiced),
        ];
        var linked = await SiigoInvoiceBatchViews.LinkedInvoicesAsync(invoices, query.TenantId, following, cancellationToken);
        var freights = await SiigoInvoiceBatchViews.FreightsAsync(invoices, query.TenantId, following, linked, cancellationToken);
        var now = clock.UtcNow;
        return new SiigoInvoiceBatchPageDto(
            [.. items.Select(batch => SiigoInvoiceBatchSummaryDto.From(batch, linked, now, freights))], total, page, pageSize);
    }

    private static int? Int(string? value) => ListSiigoCallFailuresQueryParser.TryInt(value, out var parsed) ? parsed : null;
}
