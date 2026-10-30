using Modules.Billing.Domain;

namespace Modules.Billing.Application;

public interface IBillingSettingsRepository
{
    /// <summary>
    /// Los ajustes del tenant o, si aún no hay fila, unos nuevos sin guardar (<see cref="BillingSettings.Create"/> con
    /// <paramref name="now"/>). Quien los cambie los pasa por <see cref="Upsert"/>.
    /// </summary>
    Task<BillingSettings> GetOrDefaultAsync(Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Inserta los ajustes si el tenant no tenía fila; si ya estaban rastreados no hace nada.</summary>
    void Upsert(BillingSettings settings);
}
