using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Shopify.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-26 S6: Geography → Shopify. «Reintentar» de la consola de sinónimos devuelve a la cola los trabajos de
/// corrección fallidos del sinónimo, que son de Shopify. Geography no referencia Shopify (<c>GeographyLayerTests</c>): el puerto
/// es de Geography y éste es el único lugar que ve los dos.</summary>
internal sealed class CityCorrectionJobsAdapter(IShopifyCityCorrectionJobs jobs) : ICityCorrectionJobs
{
    public Task<int> RetryFailedAsync(CitySynonymId synonymId, CancellationToken cancellationToken) =>
        jobs.RetryFailedAsync(synonymId.Value, cancellationToken);
}
