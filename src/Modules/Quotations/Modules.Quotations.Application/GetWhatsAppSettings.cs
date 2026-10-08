using BuildingBlocks.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Quotations.Application;

public sealed record GetWhatsAppSettingsQuery(Guid TenantId) : IQuery<WhatsAppSettingsDto>;

/// <summary>
/// La configuración de WhatsApp para Configuración (spec 2026-10-07). Sin efecto colateral: sin
/// fila responde <see cref="TenantWhatsAppSettings.CreateEmpty"/> (Shared, versión 1) y no la crea.
/// Gate de capacidad después de autorizar y antes de leer, igual que el PUT. La key se descifra
/// para saber si es legible y el valor se descarta en el acto.
/// </summary>
public sealed class GetWhatsAppSettingsHandler(
    ITenantWhatsAppSettingsRepository repository,
    IWhatsAppSecretProtector protector,
    ITenantModules tenantModules,
    IExecutionContext executionContext,
    IClock clock)
    : IQueryHandler<GetWhatsAppSettingsQuery, WhatsAppSettingsDto>
{
    public async Task<WhatsAppSettingsDto> HandleAsync(
        GetWhatsAppSettingsQuery query,
        CancellationToken cancellationToken)
    {
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, TenancyPermissions.SettingsRead);
        await TenantModuleGuard.EnsureEnabledAsync(
            tenantModules, query.TenantId, TenantModuleKeys.Quotations, cancellationToken);

        var stored = await repository.FindReadOnlyAsync(query.TenantId, cancellationToken);
        return WhatsAppSettingsMappings.ToDto(
            stored ?? TenantWhatsAppSettings.CreateEmpty(query.TenantId, clock.UtcNow), protector);
    }
}
