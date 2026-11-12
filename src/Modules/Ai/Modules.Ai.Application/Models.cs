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

/// <param name="Effort">C-7: el esfuerzo que se manda al proveedor. Con Anthropic siempre viene; con OpenAI lo decide P-1.</param>
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

public sealed record AiProviderModelsDto(string Provider, IReadOnlyList<AiModelDto> Models);

/// <param name="Available"><c>false</c>: se muestra pero no se puede elegir para publicar (un modelo retirado, o todo OpenAI hasta que
/// exista su adaptador).</param>
public sealed record AiModelDto(string Id, string Label, string Description, bool IsDefault, bool Available);

public sealed class ListAiModelsHandler(IExecutionContext executionContext, ITenantModules tenantModules, IAiModelCatalog catalog)
    : IQueryHandler<ListAiModelsQuery, AiModelCatalogDto>
{
    public async Task<AiModelCatalogDto> HandleAsync(ListAiModelsQuery query, CancellationToken cancellationToken)
    {
        await AiAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, AiPermissions.AgentRead, cancellationToken);
        return ModelCatalog.ToDto(catalog);
    }
}

/// <summary>La lista curada como la ve la pantalla, la misma en <c>GET /models</c> y en el editor del agente.</summary>
public static class ModelCatalog
{
    public static AiModelCatalogDto ToDto(IAiModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return new AiModelCatalogDto(
            catalog.DefaultProvider.Value,
            [
                .. catalog.Providers.Select(provider => new AiProviderModelsDto(
                    provider.Provider.Value,
                    [.. provider.Models.Select(model => new AiModelDto(model.Id, model.Label, model.Description, model.IsDefault, model.Available))])),
            ]);
    }

    /// <summary>El modelo que toma un borrador nuevo: el predeterminado del proveedor predeterminado (<c>Ai:DefaultProvider</c>).</summary>
    public static AgentModelRef DefaultFor(IAiModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var provider = catalog.Providers.Single(entry => entry.Provider == catalog.DefaultProvider);
        var model = provider.Models.FirstOrDefault(candidate => candidate.IsDefault) ?? provider.Models[0];
        return new AgentModelRef(provider.Provider.Value, model.Id);
    }
}
