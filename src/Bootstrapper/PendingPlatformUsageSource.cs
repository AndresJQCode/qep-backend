using Modules.Tenancy.Application;

namespace Bootstrapper;

/// <summary>Provisional: sólo para que el contenedor resuelva el handler del reporte de uso mientras llega la fuente real
/// (tarea 3), que reemplaza esta clase y su registro. Si alguien la llama, falla fuerte en vez de devolver ceros.</summary>
internal sealed class PendingPlatformUsageSource : IPlatformUsageSource
{
    public Task<IReadOnlyList<PlatformUsageTenantRow>> ListAsync(DateTimeOffset startUtc, CancellationToken cancellationToken) =>
        throw new NotSupportedException("La fuente del reporte de uso aún no está implementada.");
}
