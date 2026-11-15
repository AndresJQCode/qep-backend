using BuildingBlocks.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;

namespace Modules.Support.Application;

public sealed record ReorderTicketTypesRequest(IReadOnlyList<Guid>? TypeIds);

/// <summary><paramref name="TypeIds"/> es la lista entera en el orden nuevo: la posición de cada tipo es su índice + 1.</summary>
public sealed record ReorderTicketTypesCommand(Guid TenantId, IReadOnlyList<Guid> TypeIds)
    : ICommand<IReadOnlyList<SupportTicketTypeDto>>
{
    public static ReorderTicketTypesCommand From(Guid tenantId, ReorderTicketTypesRequest request) =>
        new(tenantId, request.TypeIds ?? []);
}

/// <summary>
/// Exige todos los ids del tenant, sin repetidos ni ajenos (si no, 422 <c>support.ticket_type.order_incomplete</c>): una lista a
/// medias dejaría dos tipos en la misma posición. Sin <c>If-Match</c>: el orden se manda completo, y un tipo editado a la vez choca
/// por su propia versión al guardar.
/// </summary>
public sealed class ReorderTicketTypesHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IClock clock,
    ISupportProvisioner provisioner,
    ISupportTicketTypeRepository types,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher)
    : ICommandHandler<ReorderTicketTypesCommand, IReadOnlyList<SupportTicketTypeDto>>
{
    private static readonly string[] ReorderedFields = ["position"];

    public async Task<IReadOnlyList<SupportTicketTypeDto>> HandleAsync(ReorderTicketTypesCommand command, CancellationToken cancellationToken)
    {
        await SupportAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, SupportPermissions.SettingsManage, cancellationToken);
        await provisioner.EnsureAsync(command.TenantId, cancellationToken);

        var existing = (await types.ListAsync(command.TenantId, cancellationToken)).ToDictionary(type => type.Id.Value);
        if (command.TypeIds.Count != existing.Count ||
            command.TypeIds.Distinct().Count() != command.TypeIds.Count ||
            !command.TypeIds.All(existing.ContainsKey))
        {
            throw new SupportDomainException(
                SupportErrorCodes.TicketTypeOrderIncomplete, "The new order must list every ticket type of the tenant exactly once.");
        }

        var now = clock.UtcNow;
        var changed = false;
        for (var index = 0; index < command.TypeIds.Count; index++)
        {
            var type = existing[command.TypeIds[index]];
            var version = type.Version;
            type.MoveTo(index + 1, now);
            changed |= type.Version != version;
        }

        if (changed)
        {
            auditPublisher.Publish(
                command.TenantId,
                executionContext.SubjectId,
                SupportAuditActions.TicketTypesReordered,
                SupportAuditActions.Resources.TicketType,
                command.TenantId.ToString(),
                "success",
                ReorderedFields,
                now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return command.TypeIds.Select(id => SupportDtoMapping.ToDto(existing[id])).ToList();
    }
}
