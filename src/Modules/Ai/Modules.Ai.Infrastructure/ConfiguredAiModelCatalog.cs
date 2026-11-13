using Microsoft.Extensions.Options;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Ai.Infrastructure.Options;

namespace Modules.Ai.Infrastructure;

/// <summary>La lista de <c>Ai:Providers</c>, ya validada al arrancar, en el orden de <see cref="AiProviders.All"/>. Singleton: la
/// configuración no cambia mientras corre el proceso.</summary>
internal sealed class ConfiguredAiModelCatalog : IAiModelCatalog
{
    public ConfiguredAiModelCatalog(IOptions<AiOptions> options)
    {
        var value = options.Value;
        DefaultProvider = AiProvider.Parse(value.DefaultProvider);
        Providers =
        [
            .. AiProviders.All.Select(provider => new AiProviderModels(
                provider,
                [
                    .. value.Providers[provider.Value].Models.Select(model => new AiModelDefinition(
                        model.Id, model.Label, model.Description, model.IsDefault, model.Available, model.Effort)),
                ])),
        ];
    }

    public AiProvider DefaultProvider { get; }

    public IReadOnlyList<AiProviderModels> Providers { get; }
}
