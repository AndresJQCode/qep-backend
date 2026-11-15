using Modules.Support.Domain;

namespace Modules.Support.Application;

public interface ISupportSettingsRepository
{
    /// <summary>La fila del tenant, con tracking. Se llama después de <see cref="ISupportProvisioner.EnsureAsync"/>: sin la fila,
    /// lanza.</summary>
    Task<SupportTenantSettings> GetAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>La fila del tenant, o <c>null</c> si nunca se aprovisionó. Para la consulta pública, que no aprovisiona (un visitante
    /// anónimo no le crea filas a un tenant).</summary>
    Task<SupportTenantSettings?> FindAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// Bloquea la fila del tenant (<c>FOR UPDATE</c>) hasta el fin de la transacción abierta: es el mismo candado de fila que toma el
    /// radicado (<see cref="ISupportTicketNumberGenerator"/>). El alta de un tipo lo toma antes de leer la posición mayor, así dos altas a
    /// la vez se serializan y no comparten posición (revisión final, m5). Exige la transacción de
    /// <see cref="ISupportUnitOfWork.BeginTransactionAsync"/> y la fila ya provisionada; sin ellas, lanza.
    /// </summary>
    Task LockAsync(Guid tenantId, CancellationToken cancellationToken);
}
