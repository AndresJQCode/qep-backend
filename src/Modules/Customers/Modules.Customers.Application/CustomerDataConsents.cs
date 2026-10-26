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
    /// el cliente no es del tenant. Exige la transacción de <see cref="ICustomersUnitOfWork.BeginTransactionAsync"/>.
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
