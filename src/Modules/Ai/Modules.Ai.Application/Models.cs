using BuildingBlocks.Application;
using Modules.Ai.Domain;
using Modules.Tenancy.Application;

namespace Modules.Ai.Application;

/// <summary>
/// La lista curada de modelos por proveedor (plan 2026-11-12, C-3). Vive en configuración del backend: cambiar o retirar un modelo no
/// es un despliegue del SPA. La implementación la lee de <c>Ai:Providers</c>, validada al arrancar.
/// </summary>
public interface IAiModelCatalog
{
    /// <summary>El proveedor que toma un borrador nuevo (<c>Ai:DefaultProvider</c>).</summary>
    AiProvider DefaultProvider { get; }

    /// <summary>Todos los proveedores de <see cref="AiProviders.All"/>, en ese orden, cada uno con su lista.</summary>
    IReadOnlyList<AiProviderModels> Providers { get; }
}

public sealed record AiProviderModels(AiProvider Provider, IReadOnlyList<AiModelDefinition> Models);

/// <param name="Effort">C-7: el esfuerzo que se manda al proveedor. Con Anthropic siempre viene; con OpenAI es opcional (spec §9.1, O-6).</param>
public sealed record AiModelDefinition(string Id, string Label, string Description, bool IsDefault, bool Available, string? Effort);

public sealed record ListAiModelsQuery(Guid TenantId) : IQuery<AiModelCatalogDto>;

/// <summary>
/// Backend for frontend: el selector de modelo del agente se dibuja con esto y nada más. Viajan <b>todos</b> los proveedores, en el orden
/// fijo de <see cref="AiProviders.All"/>, aunque uno no tenga modelos disponibles: si un proveedor desapareciera de la respuesta, la
/// pantalla tendría que conocer la lista del backend para dibujar el que falta. El nombre visible del proveedor lo pone el frontend
/// (viaja la clave, <c>anthropic</c> u <c>openai</c>); el del modelo, no, porque cambia con la configuración. El esfuerzo no viaja: es
/// de la llamada al proveedor, no de la pantalla.
/// </summary>
public sealed record AiModelCatalogDto(string DefaultProvider, IReadOnlyList<AiProviderModelsDto> Providers);

/// <param name="Configured">Backend for frontend (plan 2026-11-17 A2, C-3): <c>true</c> sólo con la conexión del proveedor <c>Active</c>. El
/// selector marca el proveedor que no se puede usar sin cruzar con <c>GET /ai/settings</c> ni con Integraciones.</param>
/// <param name="Issue">Por qué no está configurado, con la tabla de <see cref="ModelCatalog.IssueOf"/>: <c>not_configured</c>,
/// <c>paused</c> o el <c>failureCode</c> de la conexión en <c>NeedsAttention</c> (<c>needs_attention</c> si no trae uno). <c>null</c> con
/// <c>configured: true</c>.</param>
public sealed record AiProviderModelsDto(string Provider, bool Configured, string? Issue, IReadOnlyList<AiModelDto> Models);

/// <param name="Available"><c>false</c>: se muestra pero no se puede elegir para publicar (un modelo retirado).</param>
public sealed record AiModelDto(string Id, string Label, string Description, bool IsDefault, bool Available);

public sealed class ListAiModelsHandler(
    IExecutionContext executionContext, ITenantModules tenantModules, IAiModelCatalog catalog, IAiProviderCredentials credentials)
    : IQueryHandler<ListAiModelsQuery, AiModelCatalogDto>
{
    public async Task<AiModelCatalogDto> HandleAsync(ListAiModelsQuery query, CancellationToken cancellationToken)
    {
        await AiAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, AiPermissions.AgentRead, cancellationToken);
        return ModelCatalog.ToDto(catalog, await credentials.DescribeAsync(query.TenantId, cancellationToken));
    }
}

/// <summary>La lista curada como la ve la pantalla, la misma en <c>GET /models</c> y en el editor del agente.</summary>
public static class ModelCatalog
{
    /// <summary>Plan 2026-11-17 A2: los códigos de <c>issue</c> que no son un <c>failureCode</c> de la conexión.</summary>
    public const string NotConfigured = "not_configured";

    public const string Paused = "paused";

    public const string NeedsAttention = "needs_attention";

    /// <param name="states">Los estados de <see cref="IAiProviderCredentials.DescribeAsync"/>, leídos una vez por request. Un proveedor sin
    /// estado se lee como sin conexión.</param>
    public static AiModelCatalogDto ToDto(IAiModelCatalog catalog, IReadOnlyList<AiProviderConnectionState> states)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(states);
        return new AiModelCatalogDto(
            catalog.DefaultProvider.Value,
            [
                .. catalog.Providers.Select(provider =>
                {
                    var issue = IssueOf(states.FirstOrDefault(state => state.Provider == provider.Provider));
                    return new AiProviderModelsDto(
                        provider.Provider.Value,
                        issue is null,
                        issue,
                        [.. provider.Models.Select(model => new AiModelDto(model.Id, model.Label, model.Description, model.IsDefault, model.Available))]);
                }),
            ]);
    }

    /// <summary>
    /// La tabla de A2 en un solo lugar: <c>null</c> sólo con la conexión <c>Active</c>; sin conexión o con un proveedor que Integrations no
    /// ofrece, <c>not_configured</c>; <c>Paused</c>, <c>paused</c>; <c>NeedsAttention</c>, su <c>failureCode</c> (<c>credentials_rejected</c>,
    /// <c>quota_exhausted</c>…) o <c>needs_attention</c> si viene vacío.
    /// </summary>
    public static string? IssueOf(AiProviderConnectionState? state) => state switch
    {
        null or { Connectable: false } or { ConnectionId: null } => NotConfigured,
        { Status: "Active" } => null,
        { Status: "Paused" } => Paused,
        { Status: "NeedsAttention", FailureCode: { Length: > 0 } failureCode } => failureCode,
        { Status: "NeedsAttention" } => NeedsAttention,
        _ => NotConfigured,
    };

    /// <summary>El modelo que toma un borrador nuevo: el predeterminado del proveedor predeterminado (<c>Ai:DefaultProvider</c>).</summary>
    public static AgentModelRef DefaultFor(IAiModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var provider = catalog.Providers.Single(entry => entry.Provider == catalog.DefaultProvider);
        var model = provider.Models.FirstOrDefault(candidate => candidate.IsDefault) ?? provider.Models[0];
        return new AgentModelRef(provider.Provider.Value, model.Id);
    }
}
