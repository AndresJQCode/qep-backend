using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record TenantSettingsDto(
    TenantId TenantId,
    string DisplayName,
    string DefaultCulture,
    string TimeZone,
    string DateFormat,
    long Version,
    TenantLogoDto? Logo);

/// <summary>
/// La URL viaja **resuelta**: es la regla BFF del repo — el sidebar necesita un `src` listo, no
/// una clave que armar con una base que el navegador no conoce. `null` sólo si el bucket público
/// se desconfiguró después de asignar el logo; `FileId` viaja igual en ese caso para que la
/// pantalla ofrezca quitar y no subir (spec 2026-09-19, § Contrato).
/// </summary>
public sealed record TenantLogoDto(Guid FileId, string? Url);
