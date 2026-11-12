using BuildingBlocks.Application;
using Modules.Ai.Domain;
using Modules.Tenancy.Application;

namespace Modules.Ai.Application;

/// <summary>La clave de la cuenta del tenant en un proveedor (BYOK, owner 2026-11-12). En claro, en memoria, para una llamada: nadie la
/// guarda ni la registra, y <see cref="ToString"/> no la imprime.</summary>
public sealed record AiCredential(Guid ConnectionId, string ApiKey)
{
    public override string ToString() => $"AiCredential {{ ConnectionId = {ConnectionId} }}";
}

/// <summary>Por qué un proveedor no se puede usar. <see cref="NotConfigured"/> también cubre un proveedor que Integrations todavía no
/// ofrece (OpenAI hasta la Tarea 6) o que no es visible: para el agente es lo mismo, no hay clave.</summary>
public enum AiCredentialUnavailableReason
{
    NotConfigured,
    Paused,
    NeedsAttention,
    SecretUnreadable,
}

/// <summary><see cref="Credential"/> no es <c>null</c> sólo si <see cref="Unavailable"/> lo es: cada consumidor decide qué hace con la
/// causa en vez de recibir un <c>null</c> mudo (mismo criterio que <c>ConnectionResolution</c> de Integrations).</summary>
public sealed record AiCredentialResolution(AiCredential? Credential, AiCredentialUnavailableReason? Unavailable)
{
    public static AiCredentialResolution Resolved(AiCredential credential) => new(credential, null);

    public static AiCredentialResolution Unusable(AiCredentialUnavailableReason reason) => new(null, reason);
}

/// <param name="Connectable">Si Integrations ofrece hoy ese proveedor: <c>false</c> en OpenAI hasta su adaptador (plan 2026-11-12,
/// Tarea 6).</param>
/// <param name="Status">El estado de la conexión (<c>Active</c>, <c>Paused</c>, <c>NeedsAttention</c>) o <c>null</c> sin conexión.</param>
public sealed record AiProviderConnectionState(AiProvider Provider, bool Connectable, Guid? ConnectionId, string? Status);

/// <summary>
/// Plan 2026-11-12, C-1: la clave de cada proveedor es la única conexión de ese proveedor en Integrations. Ai no ve Integrations
/// (AiLayerTests): el adaptador vive en Bootstrapper. Una conexión de un proveedor nunca sirve para otro (C-19).
/// </summary>
public interface IAiProviderCredentials
{
    /// <summary>La clave de una conexión activa del proveedor, o la causa por la que no hay.</summary>
    Task<AiCredentialResolution> ResolveAsync(Guid tenantId, AiProvider provider, CancellationToken cancellationToken);

    /// <summary>Un estado por cada proveedor de <see cref="AiProviders.All"/>, en ese orden. Sin claves.</summary>
    Task<IReadOnlyList<AiProviderConnectionState>> DescribeAsync(Guid tenantId, CancellationToken cancellationToken);
}

public sealed record GetAiSettingsQuery(Guid TenantId) : IQuery<AiSettingsDto>;

/// <summary>
/// Backend for frontend: la pantalla de agentes dice, por proveedor, si falta conectar la clave, si está pausada o si hay que revisarla,
/// sin cruzar con Integraciones. Viajan todos los proveedores, en orden fijo, aunque uno no se pueda conectar todavía
/// (<c>connectable: false</c>): si faltara, la pantalla tendría que saber cuáles existen. La clave nunca viaja; sus últimos 4 los
/// muestra Integraciones, donde se administra.
/// </summary>
public sealed record AiSettingsDto(IReadOnlyList<AiProviderSettingsDto> Providers);

/// <param name="Provider">La clave del proveedor, que es también la de su proveedor en Integrations (para el enlace «Conectar»).</param>
public sealed record AiProviderSettingsDto(string Provider, bool Connectable, Guid? ConnectionId, string? Status);

public sealed class GetAiSettingsHandler(IExecutionContext executionContext, ITenantModules tenantModules, IAiProviderCredentials credentials)
    : IQueryHandler<GetAiSettingsQuery, AiSettingsDto>
{
    public async Task<AiSettingsDto> HandleAsync(GetAiSettingsQuery query, CancellationToken cancellationToken)
    {
        await AiAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, AiPermissions.AgentRead, cancellationToken);
        var states = await credentials.DescribeAsync(query.TenantId, cancellationToken);
        return new AiSettingsDto(
            [.. states.Select(state => new AiProviderSettingsDto(state.Provider.Value, state.Connectable, state.ConnectionId, state.Status))]);
    }
}
