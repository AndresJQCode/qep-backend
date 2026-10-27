namespace Modules.Tenancy.Application;

/// <summary>Consulta de sólo lectura: si ya existe un tenant con ese slug.</summary>
public interface ITenantSlugAvailability
{
    /// <param name="normalizedSlug">El slug ya recortado y en minúsculas.</param>
    Task<bool> IsTakenAsync(string normalizedSlug, CancellationToken cancellationToken);
}
