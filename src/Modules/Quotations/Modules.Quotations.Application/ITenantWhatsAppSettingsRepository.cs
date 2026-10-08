using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// La configuración de WhatsApp, una por tenant (spec 2026-10-07). Sin <c>Update</c>:
/// <see cref="FindAsync"/> devuelve la entidad rastreada y
/// <see cref="IQuotationsUnitOfWork.SaveChangesAsync"/> persiste el <c>Configure</c>, como el resto
/// de repositorios del módulo. Sin fila, el llamador arma
/// <see cref="TenantWhatsAppSettings.CreateEmpty"/> y la agrega si algo cambió.
/// </summary>
public interface ITenantWhatsAppSettingsRepository
{
    /// <summary>Rastreada: para el PUT y el re-cifrado.</summary>
    Task<TenantWhatsAppSettings?> FindAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Sin tracking: el envío y los GET sólo leen.</summary>
    Task<TenantWhatsAppSettings?> FindReadOnlyAsync(Guid tenantId, CancellationToken cancellationToken);

    void Add(TenantWhatsAppSettings settings);
}
