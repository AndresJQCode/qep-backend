namespace Modules.Tenancy.Application;

/// <summary>
/// La moneda por defecto de un tenant, para los módulos de negocio. Un tenant que no existe es
/// <c>tenancy.tenant.not_found</c>, nunca un default silencioso (mismo criterio que
/// <see cref="ITenantClock"/>).
/// </summary>
public interface ITenantDefaultCurrency
{
    /// <summary>Returns a catalogue code (<see cref="Currencies"/>).</summary>
    Task<string> GetAsync(Guid tenantId, CancellationToken cancellationToken);
}
