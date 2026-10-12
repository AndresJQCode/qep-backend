namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-12 §3.3 (D-Q5): el nombre visible del tenant, para <c>variables.empresa</c> de las respuestas rápidas. Adaptador
/// en Bootstrapper sobre Tenancy; <c>null</c> si el tenant no tiene fila (el handler lo vuelve <c>""</c>).</summary>
public interface IMessagingTenantNames
{
    Task<string?> FindAsync(Guid tenantId, CancellationToken cancellationToken);
}
