namespace Modules.Messaging.Domain;

/// <summary>El <c>sync_type</c> que se le pide a Meta: <c>smb_app_state_sync</c> (contactos) e <c>history</c> (mensajes).</summary>
public enum HistorySyncType
{
    SmbAppStateSync,
    History,
}

/// <summary>El estado del pedido de un tipo. Se guarda por nombre (<c>CK_history_syncs_*_state</c>).</summary>
public enum HistorySyncRequestState
{
    Pending,
    Requested,
    Failed,
}

/// <summary>El estado que ve la pantalla; no se guarda, lo calcula <see cref="HistorySync.StatusAt"/>.</summary>
public enum HistorySyncStatus
{
    Pending,
    Requested,
    InProgress,
    Completed,
    Declined,
    Failed,
    Expired,
}

/// <summary>
/// Spec 2026-10-29: la importación del historial de la app WhatsApp Business de una conexión por coexistencia. Una fila por
/// conexión. Meta sólo acepta el pedido (<c>smb_app_data</c>) dentro de las 24 horas siguientes a la conexión, así que todo lo que
/// se reintenta vence en <see cref="Deadline"/>. <see cref="Version"/> es el token de concurrencia.
/// </summary>
public sealed class HistorySync
{
    public static readonly TimeSpan RequestWindow = TimeSpan.FromHours(24);

    public const int RequestIdMaxLength = 128;

    public const int ErrorCodeMaxLength = 64;

    private HistorySync() { }

    public Guid ConnectionId { get; private set; }

    public Guid TenantId { get; private set; }

    public DateTimeOffset ConnectedAt { get; private set; }

    public DateTimeOffset Deadline => ConnectedAt + RequestWindow;

    public HistorySyncRequestState ContactsState { get; private set; }

    public string? ContactsRequestId { get; private set; }

    public DateTimeOffset? ContactsRequestedAt { get; private set; }

    public string? ContactsErrorCode { get; private set; }

    public HistorySyncRequestState HistoryState { get; private set; }

    public string? HistoryRequestId { get; private set; }

    public DateTimeOffset? HistoryRequestedAt { get; private set; }

    public string? HistoryErrorCode { get; private set; }

    /// <summary>La fase más alta que mandó Meta en un bloque del historial.</summary>
    public int? HistoryPhase { get; private set; }

    /// <summary>El progreso más alto (0 a 100) que mandó Meta.</summary>
    public int? HistoryProgress { get; private set; }

    /// <summary>La persona rechazó compartir el historial desde la app.</summary>
    public DateTimeOffset? DeclinedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public static HistorySync Start(Guid tenantId, Guid connectionId, DateTimeOffset connectedAt, DateTimeOffset now) =>
        new()
        {
            ConnectionId = connectionId,
            TenantId = tenantId,
            ConnectedAt = connectedAt,
            ContactsState = HistorySyncRequestState.Pending,
            HistoryState = HistorySyncRequestState.Pending,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };

    /// <summary>La precedencia es la del spec: lo que dijo la persona o Meta gana sobre lo que falta pedir.</summary>
    public HistorySyncStatus StatusAt(DateTimeOffset now)
    {
        if (DeclinedAt is not null)
        {
            return HistorySyncStatus.Declined;
        }

        if (CompletedAt is not null)
        {
            return HistorySyncStatus.Completed;
        }

        if (HistoryPhase is not null || HistoryProgress is not null)
        {
            return HistorySyncStatus.InProgress;
        }

        if (now >= Deadline && (ContactsState != HistorySyncRequestState.Requested || HistoryState != HistorySyncRequestState.Requested))
        {
            return HistorySyncStatus.Expired;
        }

        if (ContactsState == HistorySyncRequestState.Failed || HistoryState == HistorySyncRequestState.Failed)
        {
            return HistorySyncStatus.Failed;
        }

        if (ContactsState == HistorySyncRequestState.Pending || HistoryState == HistorySyncRequestState.Pending)
        {
            return HistorySyncStatus.Pending;
        }

        return HistorySyncStatus.Requested;
    }

    public bool CanRetryAt(DateTimeOffset now) =>
        DeclinedAt is null
        && now < Deadline
        && (IsRetryable(ContactsState) || IsRetryable(HistoryState));

    /// <summary>Devuelve a <c>Pending</c> cada tipo que falló, para que el worker lo vuelva a pedir.</summary>
    public void Retry(DateTimeOffset now)
    {
        if (!CanRetryAt(now))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.HistorySyncRetryNotAllowed, "The history sync cannot be retried.");
        }

        if (ContactsState == HistorySyncRequestState.Failed)
        {
            ContactsState = HistorySyncRequestState.Pending;
            ContactsErrorCode = null;
        }

        if (HistoryState == HistorySyncRequestState.Failed)
        {
            HistoryState = HistorySyncRequestState.Pending;
            HistoryErrorCode = null;
        }

        Touch(now);
    }

    /// <summary>Sólo pruebas: una importación en el estado que la dejarían el worker y los webhooks.
    /// Internal (visible sólo para las pruebas): ningún código de producción puede saltarse las reglas.</summary>
    internal static HistorySync ForTests(
        HistorySync source,
        HistorySyncRequestState contactsState,
        HistorySyncRequestState historyState,
        string? historyErrorCode = null,
        int? historyProgress = null,
        DateTimeOffset? declinedAt = null,
        DateTimeOffset? completedAt = null)
    {
        source.ContactsState = contactsState;
        source.HistoryState = historyState;
        source.HistoryErrorCode = historyErrorCode;
        source.HistoryProgress = historyProgress;
        source.DeclinedAt = declinedAt;
        source.CompletedAt = completedAt;
        return source;
    }

    private static bool IsRetryable(HistorySyncRequestState state) =>
        state is HistorySyncRequestState.Pending or HistorySyncRequestState.Failed;

    private void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }
}
