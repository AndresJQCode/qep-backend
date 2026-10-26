using Modules.Customers.Domain;

namespace Modules.Customers.Application;

/// <summary>
/// Spec 2026-10-26 D-5: el registro de consentimiento de datos, que sólo agrega filas. Junto con <c>CustomerMergeStore</c>
/// (que mueve las filas del fusionado, D-7) es lo único que lee o escribe <c>customers.data_consents</c>.
/// </summary>
public interface ICustomerDataConsentRepository
{
    /// <summary>Del más nuevo al más viejo, por <c>(occurred_at DESC, sequence DESC)</c>.</summary>
    Task<IReadOnlyList<CustomerDataConsent>> ListAsync(Guid tenantId, CustomerId customerId, CancellationToken cancellationToken);

    /// <summary>La primera fila del orden de <see cref="ListAsync"/>: el estado vigente del cliente.</summary>
    Task<CustomerDataConsent?> FindLatestAsync(Guid tenantId, CustomerId customerId, CancellationToken cancellationToken);

    /// <summary>Si esa respuesta ya tiene fila. Es la vía rápida: la carrera la cierra el índice único, que
    /// <see cref="ICustomersUnitOfWork.SaveChangesAsync"/> traduce a <see cref="DataConsentReplyRecordedException"/>.</summary>
    Task<bool> ReplyRecordedAsync(Guid tenantId, Guid replyMessageId, CancellationToken cancellationToken);

    /// <summary>
    /// <c>SELECT … FOR UPDATE</c> del cliente y, si está fusionado, del que quedó (un salto, spec 2026-10-24 D-F2), para que
    /// la fila no se escriba sobre un cliente que una fusión está moviendo. Devuelve el id en que se escribe, o <c>null</c> si
    /// el cliente no es del tenant. Exige la transacción de <see cref="ICustomersUnitOfWork.BeginTransactionAsync"/>. Un deadlock
    /// con una fusión que toma las mismas filas al revés sale como <c>RequestConcurrencyException</c>
    /// (<c>concurrency.conflict</c>), con la transacción abortada.
    /// </summary>
    Task<Guid?> LockCustomerAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);

    void Add(CustomerDataConsent consent);
}

/// <summary>
/// Otra entrega ya registró la misma respuesta (<c>IX_data_consents_reply_message</c>): un lote que se reintenta no registra
/// dos veces. La lanza <c>CustomersUnitOfWork</c> y la atrapa quien registra, que la lee como «ya registrado».
/// </summary>
public sealed class DataConsentReplyRecordedException : Exception
{
    public DataConsentReplyRecordedException()
        : base("That reply already has a data consent row.")
    {
    }

    public DataConsentReplyRecordedException(Exception innerException)
        : base("That reply already has a data consent row.", innerException)
    {
    }

    public DataConsentReplyRecordedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public DataConsentReplyRecordedException(string message)
        : base(message)
    {
    }
}

/// <summary>Spec 2026-10-26 D-5: lo que respondió el titular al bloque «Política de datos», con su prueba.</summary>
public sealed record ChatbotDataConsentRecord(
    Guid TenantId,
    Guid CustomerId,
    DataConsentEvent Decision,
    DateTimeOffset OccurredAt,
    DataConsentIdentity Identity,
    ChatbotConsentEvidence Evidence);

public enum DataConsentRecordOutcome
{
    Recorded,

    /// <summary>Esa respuesta ya tenía fila: un lote que se reintenta, o dos entregas a la vez.</summary>
    AlreadyRecorded,

    /// <summary>El cliente no es del tenant.</summary>
    CustomerMissing,
}

/// <summary>Spec 2026-10-26 D-5: lo que el bot pregunta y registra del consentimiento de datos de un cliente.</summary>
public interface ICustomerDataConsents
{
    /// <summary>Si la última fila del cliente (o del que quedó, si está fusionado) es una aceptación.</summary>
    Task<bool> HasCurrentAcceptanceAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);

    /// <summary>Una fila por respuesta (<see cref="ChatbotConsentEvidence.ReplyMessageId"/>), sobre el que quedó si el cliente está
    /// fusionado, también si está inactivo, y auditada como <c>System</c> en la misma transacción.</summary>
    Task<DataConsentRecordOutcome> RecordFromChatbotAsync(ChatbotDataConsentRecord record, CancellationToken cancellationToken);
}

public sealed class CustomerDataConsents(
    ICustomerDataConsentRepository consents,
    ICustomerRepository customers,
    ICustomersUnitOfWork unitOfWork,
    ICustomersAuditPublisher auditPublisher) : ICustomerDataConsents
{
    private const string Success = "success";

    public async Task<bool> HasCurrentAcceptanceAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        var id = new CustomerId(customerId);
        // Un salto basta: MergedIntoId siempre apunta a un no fusionado (spec 2026-10-24 D-F2), y la fusión movió sus filas allá.
        if (!(await customers.FindManyAsync(tenantId, [id], cancellationToken)).TryGetValue(id, out var customer))
        {
            return false;
        }

        var latest = await consents.FindLatestAsync(tenantId, customer.MergedIntoId ?? customer.Id, cancellationToken);
        return latest?.Event == DataConsentEvent.Accepted;
    }

    public async Task<DataConsentRecordOutcome> RecordFromChatbotAsync(ChatbotDataConsentRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        // Cada return sin CommitAsync deshace la transacción al liberarla (ICustomersUnitOfWork.BeginTransactionAsync).
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        if (await consents.LockCustomerAsync(record.TenantId, record.CustomerId, cancellationToken) is not { } customerId)
        {
            return DataConsentRecordOutcome.CustomerMissing;
        }

        // La vía rápida; la carrera la cierra el índice, abajo.
        if (await consents.ReplyRecordedAsync(record.TenantId, record.Evidence.ReplyMessageId, cancellationToken))
        {
            return DataConsentRecordOutcome.AlreadyRecorded;
        }

        consents.Add(CustomerDataConsent.FromChatbot(
            record.TenantId, new CustomerId(customerId), record.Decision, record.OccurredAt, record.Identity, record.Evidence));
        auditPublisher.PublishBySystem(
            record.TenantId, ActionFor(record.Decision), customerId.ToString(), Success, record.OccurredAt);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DataConsentReplyRecordedException)
        {
            // La violación del índice abortó la transacción de PostgreSQL: no se vuelve a usar. El return la libera sin
            // confirmar, que la deshace, y el unit of work ya descartó la fila y la auditoría del tracker.
            return DataConsentRecordOutcome.AlreadyRecorded;
        }

        await transaction.CommitAsync(cancellationToken);
        return DataConsentRecordOutcome.Recorded;
    }

    // FromChatbot ya rechaza cualquier otra decisión, así que ésta nunca llega a escribirse.
    private static string ActionFor(DataConsentEvent decision) => decision switch
    {
        DataConsentEvent.Accepted => CustomerAuditActions.DataConsentAccepted,
        DataConsentEvent.Rejected => CustomerAuditActions.DataConsentRejected,
        _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "The chatbot only records an acceptance or a rejection."),
    };
}
