using BuildingBlocks.Application;
using Modules.Siigo.Application.Settings;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Products;

/// <summary><c>DELETE …/products/{variantId}/link</c> (<c>siigo.settings.manage</c>). Devuelve la cobertura que quedó.</summary>
public sealed record UnlinkSiigoVariantCommand(Guid TenantId, Guid ConnectionId, Guid VariantId) : ICommand<SiigoCoverageDto>;

/// <summary>
/// Spec 2026-11-18 §5.3, paso 6: desvínculo manual. Idempotente: una variante sin vínculo responde igual (200) y no cambia la versión ni
/// audita. Con vínculo, sube la versión de la configuración y audita el paso <c>products</c>.
/// </summary>
public sealed class UnlinkSiigoVariantHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISiigoConnectionDirectory connections,
    ISiigoSettingsRepository settingsRepository,
    ISiigoVariantCatalog catalog,
    ISiigoUnitOfWork unitOfWork,
    ISiigoAuditRecorder auditRecorder,
    IClock clock) : ICommandHandler<UnlinkSiigoVariantCommand, SiigoCoverageDto>
{
    public async Task<SiigoCoverageDto> HandleAsync(UnlinkSiigoVariantCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await SiigoSettingsSupport.AuthorizeAsync(executionContext, tenantModules, command.TenantId, cancellationToken);
        await SiigoSettingsSupport.ConnectionAsync(connections, command.TenantId, command.ConnectionId, cancellationToken);
        var settings = await settingsRepository.FindOrCreateAsync(command.TenantId, command.ConnectionId, withVariantLinks: true, cancellationToken)
            ?? throw SiigoAuthorization.Denied();

        var version = settings.Version;
        settings.UnlinkVariant(command.VariantId);
        if (settings.Version != version)
        {
            SiigoSettingsSupport.Audit(auditRecorder, executionContext, settings, SiigoProductLinksSupport.StepName, clock.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await SiigoProductLinksSupport.CoverageAsync(catalog, settings, cancellationToken);
    }
}
