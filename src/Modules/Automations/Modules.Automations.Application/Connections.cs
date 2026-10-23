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

/// <summary>Las conexiones «API externa» del tenant, sin un chatbot de por medio: el frontend las lee al importar un archivo para
/// que el admin asigne cada API del archivo a una conexión propia. Es la misma lista que trae el editor en <c>externalApis</c>.</summary>
public sealed record ListExternalApisQuery(Guid TenantId) : IQuery<IReadOnlyList<ExternalApiRefDto>>;

public sealed class ListExternalApisHandler(
    IExecutionContext executionContext, ITenantModules tenantModules, IAutomationExternalApis externalApis)
    : IQueryHandler<ListExternalApisQuery, IReadOnlyList<ExternalApiRefDto>>
{
    public async Task<IReadOnlyList<ExternalApiRefDto>> HandleAsync(
        ListExternalApisQuery query, CancellationToken cancellationToken)
    {
        await AutomationsAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, AutomationsPermissions.ChatbotRead, cancellationToken);
        var apis = await externalApis.ListAsync(query.TenantId, cancellationToken);
        return [.. apis.Select(api => new ExternalApiRefDto(api.Id, api.Name, api.Status))];
    }
}
