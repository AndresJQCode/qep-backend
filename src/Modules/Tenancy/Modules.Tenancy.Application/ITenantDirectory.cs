using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

// Consulta de sólo lectura entre módulos para que otros (por ejemplo uno que aprovisiona un
// subdominio acotado al tenant) puedan resolver el slug de un tenant sin tomar dependencia
// de la persistencia de Tenancy ni repetir el slug en su propio esquema.
public interface ITenantDirectory
{
    Task<string?> GetSlugAsync(TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>Nombre visible del tenant. Lo usa Notifications para nombrar la organización
    /// en el correo de invitación: se lee al entregar y no viaja en el evento, así que un tenant
    /// renombrado entre la invitación y el envío sale con el nombre vigente.</summary>
    Task<string?> GetDisplayNameAsync(TenantId tenantId, CancellationToken cancellationToken);

    Task<string?> GetTimeZoneAsync(TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>La moneda por defecto del tenant (un código del catálogo de monedas), o null si no tiene fila.</summary>
    Task<string?> GetDefaultCurrencyAsync(TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>El logo vigente del tenant, o null sin logo. Es la única lectura que Quotations
    /// necesita de Tenancy para imprimir el logo en el PDF (decisión 9 del spec 2026-09-19).</summary>
    Task<Guid?> GetLogoFileIdAsync(TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-08 §4: el estado del tenant, o null si no tiene fila (el tenant simulado del
    /// stub). Es el puerto propio del stub para el estado: <see cref="ITenantModules.FindAsync"/> no cambia
    /// su semántica de null («no enmascarar»), porque usarlo para "inactivo" le daría al stub todo lo de
    /// X-Permissions sin enmascarar.</summary>
    Task<TenantStatus?> GetStatusAsync(TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>El formato regional del tenant en una sola lectura, o null si no tiene fila. Lo usa
    /// <c>/auth/me</c>: el formato llega a todo miembro sin pedir <c>tenancy.settings.read</c>
    /// —no es sensible— y antes de la primera pintura de la SPA.</summary>
    Task<TenantRegionalSettings?> GetRegionalSettingsAsync(TenantId tenantId, CancellationToken cancellationToken);
}

/// <summary>Los cuatro valores que deciden cómo se muestran fechas, horas y montos del tenant.</summary>
public sealed record TenantRegionalSettings(
    string TimeZone,
    string DateFormat,
    string DefaultCurrency,
    string NumberFormat);
