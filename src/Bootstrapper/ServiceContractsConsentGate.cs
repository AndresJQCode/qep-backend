using Modules.ServiceContracts.Application;

namespace Bootstrapper;

/// <summary>
/// Fase 1 de cobros recurrentes: el aviso de cobro sale siempre. El owner decidió el 2026-10-29 que el aviso de cobro sale
/// sin importar el consentimiento de datos (Ley 1581), aunque Customers lo registre: el estado vigente es la última fila del
/// registro de sólo-agregar de <c>CustomerDataConsent</c> (<c>CustomerDataConsent.cs:52-56</c>) y esta fase no lo consulta.
///
/// Es el único punto que cambiaría si esa decisión se revierte: el código <c>consent_blocked</c> (spec §3.4) queda reservado
/// para cuando este método responda <c>false</c>, y el resto de ServiceContracts sólo pregunta por la costura.
/// </summary>
internal sealed class ServiceContractsConsentGate : IServiceContractsConsentGate
{
    public Task<bool> MayNotifyAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken) =>
        Task.FromResult(true);
}
