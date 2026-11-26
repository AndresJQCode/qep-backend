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

/// <summary>Por qué un proveedor no se puede usar. <see cref="NotConfigured"/> también cubre un proveedor que Integrations no ofrece o
/// que no es visible: para el agente es lo mismo, no hay clave.</summary>
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

/// <param name="Connectable">Si Integrations ofrece hoy ese proveedor (los dos desde la Tarea 6 del plan 2026-11-12).</param>
/// <param name="Status">El estado de la conexión (<c>Active</c>, <c>Paused</c>, <c>NeedsAttention</c>) o <c>null</c> sin conexión.</param>
/// <param name="FailureCode">El motivo de <c>NeedsAttention</c> (<see cref="AiCredentialFailures"/>, <c>provider_unreachable</c>…), sólo en
/// ese estado; en cualquier otro, <c>null</c> (plan 2026-11-17 A1).</param>
public sealed record AiProviderConnectionState(
    AiProvider Provider, bool Connectable, Guid? ConnectionId, string? Status, string? FailureCode = null);

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

    /// <summary>
    /// Spec 2026-10-29 §4.5 y plan 2026-11-12 C-19: el proveedor rechazó la cuenta de forma definitiva (clave, permiso o saldo). La conexión de
    /// ese proveedor pasa de <c>Active</c> a <c>NeedsAttention</c> con <paramref name="failureCode"/> (uno de <see cref="AiCredentialFailures"/>),
    /// así que sus agentes dejan de tomar conversaciones y los del otro proveedor siguen. La saca una prueba exitosa de la clave en
    /// Integraciones. Si no estaba <c>Active</c>, no hace nada.
    /// </summary>
    Task MarkNeedsAttentionAsync(Guid tenantId, Guid connectionId, string failureCode, CancellationToken cancellationToken);
}

/// <summary>Los motivos con que una conexión de proveedor de IA queda en <c>NeedsAttention</c> (<c>last_failure_code</c> en Integrations).</summary>
public static class AiCredentialFailures
{
    public const string CredentialsRejected = "credentials_rejected";
    public const string PermissionDenied = "permission_denied";
    public const string QuotaExhausted = "quota_exhausted";
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
/// <param name="FailureCode">Backend for frontend (plan 2026-11-17 A1, F-3): la pantalla distingue «sin saldo» de «clave rechazada» sin
/// cruzar con Integraciones. Sólo con <c>Status = NeedsAttention</c>; en cualquier otro estado, <c>null</c>, porque Integrations no limpia
/// el código al verificar y sería un fallo viejo en una conexión sana.</param>
public sealed record AiProviderSettingsDto(string Provider, bool Connectable, Guid? ConnectionId, string? Status, string? FailureCode);

public sealed class GetAiSettingsHandler(IExecutionContext executionContext, ITenantModules tenantModules, IAiProviderCredentials credentials)
    : IQueryHandler<GetAiSettingsQuery, AiSettingsDto>
{
    public async Task<AiSettingsDto> HandleAsync(GetAiSettingsQuery query, CancellationToken cancellationToken)
    {
        await AiAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, AiPermissions.AgentRead, cancellationToken);
        var states = await credentials.DescribeAsync(query.TenantId, cancellationToken);
        return new AiSettingsDto(
            [.. states.Select(state => new AiProviderSettingsDto(
                state.Provider.Value, state.Connectable, state.ConnectionId, state.Status, state.FailureCode))]);
    }
}
