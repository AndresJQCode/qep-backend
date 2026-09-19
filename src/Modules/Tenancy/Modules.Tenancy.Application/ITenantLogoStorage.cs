namespace Modules.Tenancy.Application;

/// <summary>
/// Publica y despublica el logo de un tenant en el bucket público de Storage, sin pasar por los
/// handlers ni el dispatcher de ese módulo (decisión 4 del spec 2026-09-19): publicar el logo lo
/// autoriza <c>tenancy.settings.update</c>, no <c>storage.file.publish</c>, que es lo que exigen
/// <c>PublishFileHandler</c>/<c>SoftDeleteFileHandler</c>. Implementado en Bootstrapper
/// (<c>TenantLogoStorage</c>), que ya referencia los dos módulos.
/// </summary>
public interface ITenantLogoStorage
{
    /// <summary>
    /// Valida que <paramref name="fileId"/> sea una imagen disponible del tenant, dentro del
    /// límite de tamaño, y la publica. Lanza <c>TenantDomainException</c> con los códigos
    /// <c>tenancy.logo.file_not_found</c>, <c>tenancy.logo.not_owned</c>,
    /// <c>tenancy.logo.not_available</c>, <c>tenancy.logo.not_image</c> o
    /// <c>tenancy.logo.too_large</c>; o <c>StorageDomainException</c> con
    /// <c>storage.public.not_configured</c> si el bucket público no está configurado.
    /// </summary>
    Task<TenantLogoPublication> PublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);

    /// <summary>
    /// Despublica y borra lógicamente el archivo. Idempotente: un archivo que ya no existe, es de
    /// otro tenant, no es el logo de este tenant (otro dueño), o ya está borrado, vuelve sin error — el commit de Tenancy que dispara esta
    /// llamada puede fallar después de que el archivo nuevo ya se publicó (decisión 8 del spec).
    /// </summary>
    Task UnpublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);

    /// <summary>
    /// Igual que <see cref="UnpublishAsync"/> pero nunca lanza: registra la falla y vuelve. Existe
    /// para el paso 8 de <c>SetTenantLogoHandler</c> (retirar el logo viejo, mejor esfuerzo,
    /// después de que Tenancy ya commiteó el nuevo) — <c>Modules.Tenancy.Application</c> no
    /// referencia <c>Microsoft.Extensions.Logging</c>, así que el registro vive acá, del lado del
    /// adaptador, que sí tiene un <c>ILogger</c>.
    /// </summary>
    Task TryUnpublishAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken);

    /// <summary>`null` si el bucket público no está configurado — nunca lanza.</summary>
    string? GetUrl(string publicKey);
}

/// <summary>La clave pública bajo la que quedó publicado el logo.</summary>
public sealed record TenantLogoPublication(string PublicKey);
