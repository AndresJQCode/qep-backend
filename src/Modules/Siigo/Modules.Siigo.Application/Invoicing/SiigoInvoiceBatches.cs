using System.Text.Json;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Audit.Domain;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Invoicing;

/// <summary>Una tanda de facturación con Siigo y el avance de cada pedido. BFF: <c>status</c> es <c>InProgress</c> o <c>Finished</c>, para que la
/// pantalla sepa cuándo dejar de consultar; los contadores evitan recorrer los ítems para pintar el resumen. Nunca lleva la petición a Siigo ni
/// datos del cliente: sólo códigos.</summary>
public sealed record SiigoInvoiceBatchDto(
    Guid Id,
    string Status,
    int Total,
    int Invoiced,
    int Skipped,
    int Failed,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<SiigoInvoiceBatchItemDto> Items)
{
    public static SiigoInvoiceBatchDto From(SiigoInvoiceBatch batch) =>
        new(
            batch.Id,
            batch.IsFinished ? "Finished" : "InProgress",
            batch.Items.Count,
            batch.Items.Count(item => item.Status == SiigoInvoiceBatchItemStatus.Invoiced),
            batch.Items.Count(item => item.Status == SiigoInvoiceBatchItemStatus.Skipped),
            batch.Items.Count(item => item.Status == SiigoInvoiceBatchItemStatus.Failed),
            batch.CreatedAt,
            batch.FinishedAt,
            [.. batch.Items.OrderBy(item => item.Position).Select(SiigoInvoiceBatchItemDto.From)]);
}

/// <summary>Un pedido de la tanda. <paramref name="Missing"/> trae, para un pedido saltado por <c>siigo.invoice.not_ready</c>, los códigos de lo
/// que le faltaba (la pantalla los traduce igual que los del <c>preview</c>). <paramref name="OrderNumber"/> no viaja: la pantalla ya lo tiene en
/// las filas que marcó.</summary>
public sealed record SiigoInvoiceBatchItemDto(
    Guid OrderId, string Status, string? ErrorCode, string? InvoiceNumber, IReadOnlyList<string> Missing)
{
    public static SiigoInvoiceBatchItemDto From(SiigoInvoiceBatchItem item) =>
        new(item.OrderId, item.Status.ToString(), item.ErrorCode, item.InvoiceNumber, item.MissingCodes);
}

/// <summary>Las tandas de facturación (<c>siigo.invoice_batches</c>). Con tracking: el llamador guarda con <see cref="ISiigoUnitOfWork"/>.
/// Todo filtra por <c>tenant_id</c>.</summary>
public interface ISiigoInvoiceBatchRepository
{
    void Add(SiigoInvoiceBatch batch);

    /// <summary>La tanda del tenant con sus pedidos, o <c>null</c> si no existe o es de otro tenant.</summary>
    Task<SiigoInvoiceBatch?> FindAsync(Guid tenantId, Guid batchId, CancellationToken cancellationToken);
}

/// <summary>Lo que pasó al facturar un pedido de una tanda. Una unión cerrada que decide el ejecutor y aplica el worker.</summary>
public abstract record SiigoBatchItemOutcome
{
    private SiigoBatchItemOutcome()
    {
    }

    /// <summary>La factura existe en Siigo (creada, estampada, o rechazada por la DIAN pero creada): el número que le dio.</summary>
    public sealed record Invoiced(string Number) : SiigoBatchItemOutcome;

    /// <summary>El pedido no estaba en condiciones: no listo, ya facturado, anulado, sin pagar… No se llamó a Siigo para crear nada.</summary>
    public sealed record Skipped(string ErrorCode, IReadOnlyList<string> Missing) : SiigoBatchItemOutcome;

    /// <summary>Siigo la rechazó o no respondió, la conexión no sirve, o quien la pidió ya no puede facturar. No se programa un reintento
    /// (owner, 2026-11-24).</summary>
    public sealed record Failed(string ErrorCode) : SiigoBatchItemOutcome;
}

/// <summary>
/// Factura un pedido de una tanda <b>como la persona que la pidió</b>. El adaptador (Bootstrapper) resuelve los permisos que esa persona tiene
/// <b>ahora</b>, corre <see cref="IssueSiigoInvoiceCommand"/> con ellos y traduce el resultado o la excepción a un
/// <see cref="SiigoBatchItemOutcome"/>. Nunca lanza por el pedido: lo que pasa con un pedido es un resultado, no una excepción.
/// </summary>
public interface ISiigoBatchItemInvoker
{
    Task<SiigoBatchItemOutcome> InvokeAsync(Guid tenantId, Guid requestedBy, Guid orderId, CancellationToken cancellationToken);
}

/// <summary><c>POST …/siigo/invoice-batches</c> (<c>siigo.invoice.issue</c>).</summary>
public sealed record CreateSiigoInvoiceBatchCommand(Guid TenantId, IReadOnlyList<Guid>? OrderIds) : ICommand<SiigoInvoiceBatchDto>;

/// <summary>La forma del cuerpo (422 <c>validation.failed</c> con el campo en camelCase): entre 1 y 50 pedidos, sin vacíos ni repetidos.</summary>
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
    }
}

/// <summary>
/// Crea la tanda y sus ítems <c>Pending</c> en una sola transacción. El <c>INSERT</c> de los ítems timbra al worker solo (<c>QueueDoorbellInterceptor</c>). No llama a Siigo ni mira los
/// pedidos: cada uno se revalida cuando le toca, con el handler de siempre, y una tanda con un pedido ajeno o inexistente lo ve saltado. La
/// auditoría lleva cuántos pedidos y quién la pidió.
/// </summary>
public sealed class CreateSiigoInvoiceBatchHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISiigoInvoiceBatchRepository batches,
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

        var now = clock.UtcNow;
        var batch = SiigoInvoiceBatch.Create(Guid.CreateVersion7(), command.TenantId, executionContext.SubjectId, command.OrderIds!, now);
        batches.Add(batch);
        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            AuditActorType.Human,
            SiigoAuditActions.InvoiceBatchRequested,
            SiigoAuditActions.InvoiceBatchResourceType,
            batch.Id.ToString(),
            JsonSerializer.Serialize(new { orders = batch.Items.Count }),
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return SiigoInvoiceBatchDto.From(batch);
    }
}

/// <summary><c>GET …/siigo/invoice-batches/{batchId}</c> (<c>siigo.invoice.issue</c>).</summary>
public sealed record GetSiigoInvoiceBatchQuery(Guid TenantId, Guid BatchId) : IQuery<SiigoInvoiceBatchDto>;

/// <summary>El avance de una tanda. De otro tenant es 404 como si no existiera (la doble capa ya dio 403 si el tenant de la ruta no es el del
/// llamador).</summary>
public sealed class GetSiigoInvoiceBatchHandler(
    IExecutionContext executionContext, ITenantModules tenantModules, ISiigoInvoiceBatchRepository batches)
    : IQueryHandler<GetSiigoInvoiceBatchQuery, SiigoInvoiceBatchDto>
{
    public async Task<SiigoInvoiceBatchDto> HandleAsync(GetSiigoInvoiceBatchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await SiigoInvoiceSupport.AuthorizeAsync(executionContext, tenantModules, query.TenantId, cancellationToken);
        var batch = await batches.FindAsync(query.TenantId, query.BatchId, cancellationToken)
            ?? throw new ResourceNotFoundException(SiigoErrorCodes.InvoiceBatchNotFound, "The invoice batch was not found.");
        return SiigoInvoiceBatchDto.From(batch);
    }
}
