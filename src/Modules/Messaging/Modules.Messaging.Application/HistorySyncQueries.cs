using BuildingBlocks.Application;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

/// <summary>
/// BFF: el estado y el «se puede reintentar» los calcula el servidor, porque dependen del plazo de 24 h de Meta y del orden de
/// precedencia de los estados, que la pantalla no debe conocer. <see cref="MustReconnect"/> existe por lo mismo: decir «reconecta» a
/// partir de <see cref="Status"/> y <see cref="Deadline"/> obligaría a la pantalla a comparar contra su reloj, que no es el del servidor.
/// </summary>
/// <param name="Status">Pending | Requested | InProgress | Completed | Declined | Failed | Expired.</param>
/// <param name="Deadline"><see cref="ConnectedAt"/> + 24 h: después Meta ya no acepta el pedido.</param>
/// <param name="Phase">0..2, la más alta que llegó; <c>null</c> hasta el primer bloque.</param>
/// <param name="Progress">0..100, el más alto que llegó; <c>null</c> hasta el primer bloque.</param>
/// <param name="CanRetry">Hay un pedido en Pending o Failed, no venció el plazo y la persona no rechazó compartir.</param>
/// <param name="MustReconnect">Expired, o Failed con el plazo vencido: la única salida es desconectar y volver a hacer el Embedded Signup.</param>
public sealed record HistorySyncDto(
    Guid ConnectionId,
    string Status,
    DateTimeOffset ConnectedAt,
    DateTimeOffset Deadline,
    HistorySyncRequestDto Contacts,
    HistorySyncRequestDto History,
    int? Phase,
    int? Progress,
    bool CanRetry,
    bool MustReconnect);

/// <param name="State">Pending | Requested | Failed.</param>
/// <param name="ErrorCode">connection_unavailable | unconfirmed | graph:{code} | <c>null</c>.</param>
public sealed record HistorySyncRequestDto(string State, DateTimeOffset? RequestedAt, string? ErrorCode);

public sealed record GetHistorySyncQuery(Guid TenantId, Guid ConnectionId) : IQuery<HistorySyncDto>;

public sealed record RetryHistorySyncCommand(Guid TenantId, Guid ConnectionId) : ICommand<HistorySyncDto>;

internal static class HistorySyncSupport
{
    /// <summary>Spec 2026-10-29 §5 (P-8): tenant y permiso de la conexión, sin el guard del módulo de Messaging: la importación es de la
    /// pantalla de la conexión y D-5 la procesa con el módulo apagado.</summary>
    public static void EnsureAuthorized(IExecutionContext executionContext, Guid tenantId, string permission)
    {
        if (executionContext.TenantId.Value != tenantId || !executionContext.HasPermission(permission))
        {
            throw new RequestForbiddenException("authorization.denied", "The subject cannot perform this history sync operation for this tenant.");
        }
    }

    public static ResourceNotFoundException NotFound(Guid connectionId) =>
        new(MessagingErrorCodes.HistorySyncNotFound, $"Connection '{connectionId}' has no history sync.");

    public static HistorySyncDto ToDto(HistorySync sync, DateTimeOffset now)
    {
        var status = sync.StatusAt(now);
        return new HistorySyncDto(
            sync.ConnectionId,
            status.ToString(),
            sync.ConnectedAt,
            sync.Deadline,
            new HistorySyncRequestDto(sync.ContactsState.ToString(), sync.ContactsRequestedAt, sync.ContactsErrorCode),
            new HistorySyncRequestDto(sync.HistoryState.ToString(), sync.HistoryRequestedAt, sync.HistoryErrorCode),
            sync.HistoryPhase,
            sync.HistoryProgress,
            sync.CanRetryAt(now),
            status == HistorySyncStatus.Expired || (status == HistorySyncStatus.Failed && now >= sync.Deadline));
    }
}

/// <summary>§5 <c>GET /connections/{id}/history-sync</c>: 404 si la conexión no tiene importación por coexistencia (la pantalla oculta
/// el panel).</summary>
public sealed class GetHistorySyncHandler(IHistorySyncRepository syncs, IExecutionContext executionContext, IClock clock)
    : IQueryHandler<GetHistorySyncQuery, HistorySyncDto>
{
    public async Task<HistorySyncDto> HandleAsync(GetHistorySyncQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        HistorySyncSupport.EnsureAuthorized(executionContext, query.TenantId, MessagingConnectionPermissions.Read);
        var sync = await syncs.FindAsync(query.TenantId, query.ConnectionId, cancellationToken)
            ?? throw HistorySyncSupport.NotFound(query.ConnectionId);
        return HistorySyncSupport.ToDto(sync, clock.UtcNow);
    }
}

/// <summary>§5 <c>POST /connections/{id}/history-sync/retry</c>: devuelve a <c>Pending</c> lo que falló y suelta el reclamo, para que el
/// pedido salga en la próxima pasada del worker. Vencido, rechazado o sin nada que pedir es 422 (el dominio). Un cambio del worker entre
/// la lectura y el guardado es <c>concurrency.conflict</c>.</summary>
public sealed class RetryHistorySyncHandler(
    IHistorySyncRepository syncs,
    IMessagingUnitOfWork unitOfWork,
    IMessagingAuditRecorder audit,
    IExecutionContext executionContext,
    IClock clock)
    : ICommandHandler<RetryHistorySyncCommand, HistorySyncDto>
{
    public async Task<HistorySyncDto> HandleAsync(RetryHistorySyncCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        HistorySyncSupport.EnsureAuthorized(executionContext, command.TenantId, MessagingConnectionPermissions.Manage);
        var sync = await syncs.FindAsync(command.TenantId, command.ConnectionId, cancellationToken)
            ?? throw HistorySyncSupport.NotFound(command.ConnectionId);

        var now = clock.UtcNow;
        // Los que el worker vuelve a pedir: Requested no se pide nunca otra vez (D-9).
        var retried = new List<string>(2);
        if (sync.ContactsState != HistorySyncRequestState.Requested)
        {
            retried.Add(nameof(HistorySyncType.SmbAppStateSync));
        }

        if (sync.HistoryState != HistorySyncRequestState.Requested)
        {
            retried.Add(nameof(HistorySyncType.History));
        }

        sync.Retry(now);
        syncs.ReleaseClaim(sync);
        audit.RecordHistorySync(command.TenantId, executionContext.SubjectId, MessagingAuditActions.HistorySyncRetried, sync.ConnectionId, now, retried);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return HistorySyncSupport.ToDto(sync, now);
    }
}
