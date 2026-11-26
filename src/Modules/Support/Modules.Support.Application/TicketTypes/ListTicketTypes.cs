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
    ISupportTicketTypeRepository types,
    ISupportTicketRepository tickets)
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
        // Una sola lectura de los tipos usados para toda la lista (plan G6).
        var used = await tickets.UsedTypeIdsAsync(query.TenantId, cancellationToken);
        var listed = (await types.ListAsync(query.TenantId, cancellationToken))
            .Select(type => SupportDtoMapping.ToDto(type, used.Contains(type.Id)))
            .ToList();
        // Ajustes sólo ve los ocho propios del tenant. El selector de tickets agrega al final el tipo interno marcado (D9): el
        // frontend decide con isInternal si lo ofrece; nunca se reordena ni se edita.
        if (!query.ForSettings && await types.FindInternalAsync(query.TenantId, cancellationToken) is { } internalType)
        {
            listed.Add(SupportDtoMapping.ToDto(internalType, used.Contains(internalType.Id)));
        }

        return listed;
    }
}
