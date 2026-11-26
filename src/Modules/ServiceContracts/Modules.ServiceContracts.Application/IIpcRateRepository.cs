using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.Application;

/// <summary>El IPC por tenant y año (spec reajuste §6.7).</summary>
public interface IIpcRateRepository
{
    void Add(IpcRate rate);

    /// <summary>Sin candado: sirve para mirar, no para decidir una escritura.</summary>
    Task<IpcRate?> FindAsync(Guid tenantId, int year, CancellationToken cancellationToken);

    /// <summary>
    /// Sin tracking y con candado compartido de la fila hasta que termine la transacción: la tasa no puede cambiar mientras se
    /// confirma con ella. Dos que la comparten no se esperan; contra quien la tiene para cambiarla
    /// (<see cref="FindForUpdateAsync"/>) sí se espera, y se lee la tasa que dejó. Exige la transacción de
    /// <see cref="IServiceContractsUnitOfWork.BeginTransactionAsync"/>.
    /// </summary>
    Task<IpcRate?> FindForShareAsync(Guid tenantId, int year, CancellationToken cancellationToken);

    /// <summary>
    /// Con tracking (quien la lee la cambia) y con candado exclusivo de la fila hasta que termine la transacción: espera a quienes
    /// estén confirmando con ella, y lo que se lea después ya ve sus cambios programados. Sin fila no hay candado: dos primeras
    /// escrituras las separa la clave primaria. Exige la transacción de
    /// <see cref="IServiceContractsUnitOfWork.BeginTransactionAsync"/>.
    /// </summary>
    Task<IpcRate?> FindForUpdateAsync(Guid tenantId, int year, CancellationToken cancellationToken);

    /// <summary>Sin tracking, del año más reciente al más viejo.</summary>
    Task<IReadOnlyList<IpcRate>> ListAsync(Guid tenantId, CancellationToken cancellationToken);
}
