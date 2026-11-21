using BuildingBlocks.Application;
using Modules.Siigo.Application.Settings;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Products;

/// <summary><c>POST …/products/auto-link</c> (<c>siigo.settings.manage</c>), sin cuerpo.</summary>
public sealed record AutoLinkSiigoVariantsByCodeCommand(Guid TenantId, Guid ConnectionId) : ICommand<SiigoAutoLinkResultDto>;

/// <summary>
/// Spec 2026-11-18 §5.3, paso 6: «Vincular por código». Recorre <b>todas</b> las páginas de productos de Siigo y empareja por
/// <see cref="SiigoCodeMatch"/> sólo variantes <b>sin vínculo</b> (uno manual o automático existente nunca se toca) y productos de Siigo
/// <b>activos</b>. Dos productos activos con el mismo código normalizado son ambiguos: no se vincula ninguna variante con ese código
/// (<c>skippedAmbiguous</c> cuenta variantes). Las llamadas a Siigo van primero y la configuración se lee después, para que la ventana
/// de la versión sea la del guardado y no la de un recorrido de varias páginas. Escribe todo en una sola transacción, deja la marca
/// <see cref="SiigoSettings.ProductsAutoLinkedAt"/> aunque no vincule nada (spec 2026-11-20 §9) y sube la versión una vez, sólo si vinculó. Una conexión que no se puede usar es 409 <c>siigo.connection_unavailable</c>.
/// </summary>
public sealed class AutoLinkSiigoVariantsByCodeHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISiigoConnectionDirectory connections,
    ISiigoSettingsRepository settingsRepository,
    ISiigoVariantCatalog catalog,
    ISiigoApi api,
    IMembershipDirectory memberships,
    ISiigoUnitOfWork unitOfWork,
    ISiigoAuditRecorder auditRecorder,
    IClock clock) : ICommandHandler<AutoLinkSiigoVariantsByCodeCommand, SiigoAutoLinkResultDto>
{
    /// <summary>Tope de páginas: 100 por página son 100.000 productos. Un Siigo que siguiera devolviendo páginas llenas no cuelga la petición.</summary>
    private const int MaxPages = 1000;

    public async Task<SiigoAutoLinkResultDto> HandleAsync(AutoLinkSiigoVariantsByCodeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await SiigoSettingsSupport.AuthorizeAsync(executionContext, tenantModules, command.TenantId, cancellationToken);
        await SiigoSettingsSupport.ConnectionAsync(connections, command.TenantId, command.ConnectionId, cancellationToken);
        var memberId = await memberships.FindActiveMembershipIdAsync(executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw SiigoAuthorization.Denied();
        var resolution = await connections.ResolveForSetupAsync(command.TenantId, command.ConnectionId, cancellationToken);
        if (resolution.Credentials is not { } credentials)
        {
            throw new ResourceConflictException(
                SiigoErrorCodes.ConnectionUnavailable,
                $"The Siigo connection cannot be used right now ({resolution.UnavailableReason}).");
        }

        var byCode = await ActiveSiigoProductsByCodeAsync(credentials, cancellationToken);

        var settings = await settingsRepository.FindOrCreateAsync(command.TenantId, command.ConnectionId, withVariantLinks: true, cancellationToken)
            ?? throw SiigoAuthorization.Denied();
        var alreadyLinked = settings.VariantLinks.Select(link => link.VariantId).ToHashSet();

        var toLink = new List<SiigoVariantLinkInput>();
        var skippedAmbiguous = 0;
        foreach (var variant in await catalog.ListAllActiveCodesAsync(command.TenantId, cancellationToken))
        {
            if (alreadyLinked.Contains(variant.VariantId)
                || SiigoCodeMatch.Normalize(variant.Code) is not { } code
                || !byCode.TryGetValue(code, out var candidates))
            {
                continue;
            }

            if (candidates.Count > 1)
            {
                skippedAmbiguous++;
                continue;
            }

            toLink.Add(new SiigoVariantLinkInput(variant.VariantId, candidates[0].Code, candidates[0].Id, candidates[0].Name));
        }

        if (toLink.Count > 0)
        {
            settings.LinkVariants(toLink, SiigoLinkOrigin.AutoCode, memberId, clock.UtcNow);
            SiigoSettingsSupport.Audit(auditRecorder, executionContext, settings, SiigoProductLinksSupport.StepName, clock.UtcNow);
        }

        // Spec 2026-11-20 §9: la marca se deja siempre, vincule o no, para que la pantalla no vuelva a intentarlo sola al abrir el paso.
        settings.MarkProductsAutoLinked(clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new SiigoAutoLinkResultDto(toLink.Count, skippedAmbiguous);
    }

    private async Task<Dictionary<string, List<SiigoProductOption>>> ActiveSiigoProductsByCodeAsync(
        SiigoCredentials credentials, CancellationToken cancellationToken)
    {
        var byCode = new Dictionary<string, List<SiigoProductOption>>(StringComparer.Ordinal);
        for (var page = 1; page <= MaxPages; page++)
        {
            var products = await api.SearchProductsAsync(credentials, null, page, cancellationToken);
            foreach (var product in products.Items.Where(product => product.Active))
            {
                if (SiigoCodeMatch.Normalize(product.Code) is not { } code)
                {
                    continue;
                }

                if (!byCode.TryGetValue(code, out var candidates))
                {
                    byCode[code] = candidates = [];
                }

                candidates.Add(product);
            }

            if (products.Items.Count == 0 || products.Page * products.PageSize >= products.TotalResults)
            {
                break;
            }
        }

        return byCode;
    }
}
