namespace Modules.ServiceContracts.Application;

/// <summary>
/// La corrida diaria de un tenant (spec §3.3, pasos 2 a 6): generar los cobros que entraron en ventana, el mensaje del día a cada
/// cliente y el email diario a cada encargado. Idempotente por día: la deduplican las filas únicas de <c>billing</c>, así que dos
/// réplicas pueden llamarla a la vez.
/// </summary>
public interface IServiceContractsReminderProcessor
{
    Task ProcessTenantAsync(Guid tenantId, CancellationToken cancellationToken);
}
