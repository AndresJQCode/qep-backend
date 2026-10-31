using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.Application;

public interface IServiceContractsSettingsRepository
{
    /// <summary>
    /// Los ajustes del tenant o, si aún no hay fila, unos nuevos sin guardar (<see cref="ServiceContractsSettings.Create"/> con
    /// <paramref name="now"/>). Quien los cambie los pasa por <see cref="Upsert"/>.
    /// </summary>
    Task<ServiceContractsSettings> GetOrDefaultAsync(Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Inserta los ajustes si el tenant no tenía fila; si ya estaban rastreados no hace nada.</summary>
    void Upsert(ServiceContractsSettings settings);
}
