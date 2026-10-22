using Modules.Automations.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-21 D-H4: adaptador provisional de <see cref="IAutomationExternalApis"/>. El proveedor <c>external-api</c> de
/// Integrations llega con el plan 2 y la tarea 16 conecta el adaptador real; hasta entonces el tenant no tiene ninguna y un bloque HTTP
/// sale con <c>http.connection_missing</c>.</summary>
internal sealed class NullAutomationExternalApis : IAutomationExternalApis
{
    public Task<IReadOnlyList<ExternalApiRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ExternalApiRef>>([]);
}
