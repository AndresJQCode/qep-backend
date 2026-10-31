namespace Modules.ServiceContracts.Application;

/// <summary>
/// Fase 1: el adaptador responde <c>true</c> siempre (owner, 2026-10-29: el aviso sale sin importar el consentimiento y esta fase no lo
/// pide). Es el único lugar donde entraría Ley 1581 si cambia, y <c>consent_blocked</c> (§3.4) el código reservado para eso.
/// </summary>
public interface IServiceContractsConsentGate
{
    Task<bool> MayNotifyAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);
}
