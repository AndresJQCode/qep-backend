using BuildingBlocks.Application;
using Modules.Siigo.Application.Invoicing;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Settings;

/// <summary><c>POST …/sellers/auto-link</c> (<c>siigo.settings.manage</c>), sin cuerpo.</summary>
public sealed record AutoLinkSiigoSellersByEmailCommand(Guid TenantId, Guid ConnectionId) : ICommand<SiigoSellerAutoLinkResultDto>;

/// <summary>
/// Spec 2026-11-20 §4.3 y §9: «Vincular por correo». Sólo miembros <b>activos sin vínculo</b> (uno existente nunca se toca) y usuarios de
/// Siigo <b>activos</b>, leídos sin caché porque es una acción explícita; el cruce es el de <see cref="SiigoSellerResolver"/>. Siempre deja
/// la marca <see cref="SiigoSettings.SellersAutoLinkedAt"/> y guarda, vincule o no: la pantalla intenta sola la primera vez que se abre el
/// paso y no lo repite. Sin <c>If-Match</c>, como el de productos. Una conexión que no se puede usar es 409.
/// </summary>
public sealed class AutoLinkSiigoSellersByEmailHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISiigoConnectionDirectory connections,
    ISiigoSettingsRepository settingsRepository,
    ISiigoCompanyDirectory companies,
    ISiigoTaxRateDirectory taxRates,
    ISiigoMemberDirectory members,
    ISiigoApi api,
    ISiigoUnitOfWork unitOfWork,
    ISiigoAuditRecorder auditRecorder,
    IClock clock) : ICommandHandler<AutoLinkSiigoSellersByEmailCommand, SiigoSellerAutoLinkResultDto>
{
    public async Task<SiigoSellerAutoLinkResultDto> HandleAsync(AutoLinkSiigoSellersByEmailCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await SiigoSettingsSupport.AuthorizeAsync(executionContext, tenantModules, command.TenantId, cancellationToken);
        var connection = await SiigoSettingsSupport.ConnectionAsync(connections, command.TenantId, command.ConnectionId, cancellationToken);
        var resolution = await connections.ResolveForSetupAsync(command.TenantId, command.ConnectionId, cancellationToken);
        if (resolution.Credentials is not { } credentials)
        {
            throw new ResourceConflictException(
                SiigoErrorCodes.ConnectionUnavailable,
                $"The Siigo connection cannot be used right now ({resolution.UnavailableReason}).");
        }

        // Siigo primero y la configuración después, como el de productos: la ventana de la versión es la del guardado.
        var users = await api.ListUsersAsync(credentials, refresh: true, cancellationToken);
        var settings = await SiigoSettingsSupport.SettingsAsync(settingsRepository, command.TenantId, command.ConnectionId, cancellationToken);
        var roster = await members.ListAsync(command.TenantId, [], cancellationToken);

        var kept = settings.SellerLinks.Select(link => new SiigoSellerLink(link.MembershipId, link.SiigoUserId, link.SiigoUserName)).ToList();
        var linkedIds = kept.Select(link => link.MembershipId).ToHashSet();
        var added = 0;
        foreach (var member in roster.Where(member => member.Active && !linkedIds.Contains(member.MembershipId)))
        {
            if (SiigoSellerResolver.Resolve(member.Email, users) is { } userId)
            {
                kept.Add(new SiigoSellerLink(member.MembershipId, userId, users.First(user => user.Id == userId).Name));
                added++;
            }
        }

        if (added > 0)
        {
            settings.ReplaceSellerLinks(kept);
            SiigoSettingsSupport.Audit(auditRecorder, executionContext, settings, SiigoSettingsSteps.Sellers, clock.UtcNow);
        }

        settings.MarkSellersAutoLinked(clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new SiigoSellerAutoLinkResultDto(
            added, await SiigoSettingsSupport.ToDtoAsync(settings, connection, companies, taxRates, members, cancellationToken));
    }
}
