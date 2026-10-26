using Modules.Geography.Domain;

namespace Modules.Geography.Application;

/// <summary>
/// Spec 2026-10-26 D-C6: lo que la consola de ciudades puede hacerle a los trabajos de corrección de un sinónimo, que son de
/// Shopify. Geography no referencia Shopify: el adaptador (<c>CityCorrectionJobsAdapter</c>, Bootstrapper) lleva la llamada al
/// puerto de Shopify.
/// </summary>
public interface ICityCorrectionJobs
{
    /// <summary>Devuelve a <c>Pending</c> los trabajos <c>Failed</c> del sinónimo, con los intentos en cero y sin lease ni error:
    /// el worker los vuelve a tomar. Devuelve cuántos eran; repetirlo sin nuevos fallidos devuelve 0.</summary>
    Task<int> RetryFailedAsync(CitySynonymId synonymId, CancellationToken cancellationToken);
}
