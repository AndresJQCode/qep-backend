using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.Application;

/// <summary>El IPC por tenant y año (spec reajuste §6.7).</summary>
public interface IIpcRateRepository
{
    void Add(IpcRate rate);

    /// <summary>Con tracking: quien la lee la cambia.</summary>
    Task<IpcRate?> FindAsync(Guid tenantId, int year, CancellationToken cancellationToken);

    /// <summary>Sin tracking, del año más reciente al más viejo.</summary>
    Task<IReadOnlyList<IpcRate>> ListAsync(Guid tenantId, CancellationToken cancellationToken);
}
