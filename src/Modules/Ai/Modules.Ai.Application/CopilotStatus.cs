using BuildingBlocks.Application;
using Modules.Ai.Domain;
using Modules.Tenancy.Application;

namespace Modules.Ai.Application;

public sealed record GetCopilotStatusQuery(Guid TenantId) : IQuery<CopilotStatusDto>;

/// <summary>
/// Backend for frontend (spec 2026-11-17 copiloto §8): el frontend decide con esto si muestra el botón del copiloto y qué puede preguntar,
/// sin deducir nada de los permisos ni de la configuración (que el miembro con <c>ai.copilot.use</c> ni siquiera puede leer).
/// </summary>
/// <param name="Reason"><c>null</c> con <paramref name="Available"/>; si no, el primero que aplica de <see cref="CopilotStatusReasons"/>.</param>
/// <param name="Tools">Las herramientas que el copiloto le ofrece a <b>quien llama</b>, por permiso y módulo. Las calcula el registro de
/// herramientas del plan C2.4; hasta entonces la lista va vacía (todavía no hay herramientas).</param>
public sealed record CopilotStatusDto(bool Available, string? Reason, IReadOnlyList<string> Tools);

/// <summary>§8: los motivos de <c>GET /copilot/status</c>, en el orden en que se evalúan.</summary>
public static class CopilotStatusReasons
{
    /// <summary>Nunca se guardó proveedor y modelo, o el modelo elegido ya no está disponible en la lista curada.</summary>
    public const string NotConfigured = "not_configured";

    /// <summary>Configurado pero apagado.</summary>
    public const string Disabled = "disabled";

    /// <summary>La cuenta del proveedor elegido no está <c>Active</c> (sin conexión, pausada o en <c>NeedsAttention</c>).</summary>
    public const string ProviderIssue = "provider_issue";

    /// <summary>El tenant gastó su tope mensual de tokens.</summary>
    public const string BudgetExhausted = "budget_exhausted";
}

public sealed class GetCopilotStatusHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ICopilotRepository repository,
    IAiProviderCredentials credentials,
    IAiModelCatalog catalog,
    ITenantClock tenantClock) : IQueryHandler<GetCopilotStatusQuery, CopilotStatusDto>
{
    public async Task<CopilotStatusDto> HandleAsync(GetCopilotStatusQuery query, CancellationToken cancellationToken)
    {
        await CopilotAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, AiPermissions.CopilotUse, cancellationToken);
        var settings = await repository.FindSettingsAsync(query.TenantId, cancellationToken);
        var reason = await CopilotAvailability.ReasonAsync(settings, repository, credentials, catalog, tenantClock, cancellationToken);
        return new CopilotStatusDto(reason is null, reason, []);
    }
}

/// <summary>Por qué el copiloto no puede contestar ahora, con el mismo orden en <c>GET /status</c> y al preguntar.</summary>
internal static class CopilotAvailability
{
    public static async Task<string?> ReasonAsync(
        CopilotSettings? settings,
        ICopilotRepository repository,
        IAiProviderCredentials credentials,
        IAiModelCatalog catalog,
        ITenantClock tenantClock,
        CancellationToken cancellationToken)
    {
        if (settings is not { Provider: { } provider, Model: { } model } || !ModelAvailable(catalog, provider, model))
        {
            return CopilotStatusReasons.NotConfigured;
        }

        if (!settings.Enabled)
        {
            return CopilotStatusReasons.Disabled;
        }

        var states = await credentials.DescribeAsync(settings.TenantId, cancellationToken);
        if (ModelCatalog.IssueOf(states.FirstOrDefault(state => state.Provider.Value == provider)) is not null)
        {
            return CopilotStatusReasons.ProviderIssue;
        }

        if (settings.MonthlyTokenBudget is { } budget)
        {
            var (monthStart, _) = await CopilotSettingsSupport.MonthAsync(tenantClock, settings.TenantId, cancellationToken);
            if ((await repository.SumTokensAsync(settings.TenantId, monthStart, cancellationToken)).Total >= budget)
            {
                return CopilotStatusReasons.BudgetExhausted;
            }
        }

        return null;
    }

    private static bool ModelAvailable(IAiModelCatalog catalog, string provider, string model) =>
        catalog.Providers.Any(entry => entry.Provider.Value == provider && entry.Models.Any(candidate => candidate.Id == model && candidate.Available));
}
