namespace Modules.Support.Application;

/// <summary>Plan D2: el siguiente radicado del tenant. Exige una transacción abierta con
/// <see cref="ISupportUnitOfWork.BeginTransactionAsync"/> y la fila que crea <see cref="ISupportProvisioner"/>: el lock de fila
/// serializa dos altas, y un alta que falla deshace su número.</summary>
public interface ISupportTicketNumberGenerator
{
    Task<long> NextAsync(Guid tenantId, CancellationToken cancellationToken);
}
