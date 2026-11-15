using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

/// <summary><paramref name="ForSettings"/>: la tabla de ajustes, que exige <c>support.settings.manage</c>; si no, el selector de los
/// tickets, que acepta leer o gestionar. Las dos traen todos los tipos, también los inactivos: el selector los marca y un ticket viejo
/// sigue nombrando el suyo.</summary>
public sealed record ListTicketTypesQuery(Guid TenantId, bool ForSettings) : IQuery<IReadOnlyList<SupportTicketTypeDto>>;

public sealed class ListTicketTypesHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISupportProvisioner provisioner,
    ISupportTicketTypeRepository types)
    : IQueryHandler<ListTicketTypesQuery, IReadOnlyList<SupportTicketTypeDto>>
{
    public async Task<IReadOnlyList<SupportTicketTypeDto>> HandleAsync(ListTicketTypesQuery query, CancellationToken cancellationToken)
    {
        if (query.ForSettings)
        {
            await SupportAuthorization.EnsureAsync(
                executionContext, tenantModules, query.TenantId, SupportPermissions.SettingsManage, cancellationToken);
        }
        else
        {
            await SupportAuthorization.EnsureCanViewAsync(executionContext, tenantModules, query.TenantId, cancellationToken);
        }

        await provisioner.EnsureAsync(query.TenantId, cancellationToken);
        return (await types.ListAsync(query.TenantId, cancellationToken)).Select(SupportDtoMapping.ToDto).ToList();
    }
}
