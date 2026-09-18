namespace Modules.Tenancy.Application;

/// <summary>
/// El calendario de un tenant: el instante de ahora y su huso (spec 2026-09-17). Vive junto a
/// <see cref="IExecutionContext"/> e <see cref="ITenantDirectory"/> porque todos los módulos que
/// cortan instantes en días ya referencian esta capa.
///
/// No hay default a UTC: un tenant que no existe es <c>tenancy.tenant.not_found</c>, porque un
/// default silencioso es justo el defecto que este puerto quita (decisión 3). Quien recorre varios
/// tenants pide un calendario por tenant, no uno por fila.
/// </summary>
public interface ITenantClock
{
    Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken);
}
