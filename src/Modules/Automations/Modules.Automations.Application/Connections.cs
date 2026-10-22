using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Automations.Application;

/// <summary>Spec 2026-10-21 §4.4: las conexiones del tenant que el formulario del chatbot deja elegir. Instagram viaja con su estado
/// y su canal para que el formulario la muestre deshabilitada (D-M6).</summary>
public sealed record ListSelectableConnectionsQuery(Guid TenantId) : IQuery<IReadOnlyList<SelectableConnectionDto>>;

public sealed class ListSelectableConnectionsHandler(
    IExecutionContext executionContext, ITenantModules tenantModules, ChatbotEditorBuilder builder)
    : IQueryHandler<ListSelectableConnectionsQuery, IReadOnlyList<SelectableConnectionDto>>
{
    public async Task<IReadOnlyList<SelectableConnectionDto>> HandleAsync(
        ListSelectableConnectionsQuery query, CancellationToken cancellationToken)
    {
        await AutomationsAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, AutomationsPermissions.ChatbotRead, cancellationToken);
        return await builder.ListSelectableAsync(query.TenantId, cancellationToken);
    }
}
