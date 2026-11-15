using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Ai.Application;
using Modules.Ai.Domain;

namespace Modules.Ai.Infrastructure.Model;

/// <summary>
/// Plan 2026-11-12, 5.2: un adaptador por proveedor, registrado por clave (<c>AddKeyedScoped&lt;IAiModelClient&gt;(provider.Value)</c>).
/// Es el único lugar fuera de los adaptadores que distingue proveedores, y lo hace sin <c>switch</c>: agregar uno es registrar su
/// adaptador con su clave.
/// </summary>
internal sealed class AiModelClientResolver(IServiceProvider services) : IAiModelClientResolver
{
    public IAiModelClient For(AiProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return services.GetRequiredKeyedService<IAiModelClient>(provider.Value);
    }
}

/// <summary>Sin propiedades: existe para colgarle <see cref="AiModelClientRegistrationValidator"/> con <c>ValidateOnStart</c>.</summary>
internal sealed class AiModelClientRegistrationOptions;

/// <summary>
/// Un proveedor de <see cref="AiProviders.All"/> sin adaptador registrado frena el arranque, no el primer trabajo de un agente de ese
/// proveedor en medio de una conversación.
/// </summary>
internal sealed class AiModelClientRegistrationValidator(IServiceProviderIsKeyedService keyed)
    : IValidateOptions<AiModelClientRegistrationOptions>
{
    public ValidateOptionsResult Validate(string? name, AiModelClientRegistrationOptions options)
    {
        var missing = AiProviders.All
            .Where(provider => !keyed.IsKeyedService(typeof(IAiModelClient), provider.Value))
            .Select(provider => provider.Value)
            .ToArray();
        return missing.Length == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"No {nameof(IAiModelClient)} is registered for the AI providers: {string.Join(", ", missing)}.");
    }
}

