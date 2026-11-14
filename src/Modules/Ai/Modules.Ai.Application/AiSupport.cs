using BuildingBlocks.Application;
using Modules.Ai.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Ai.Application;

/// <summary>Copia de <c>AutomationsAuthorization</c>: tenant de la ruta distinto del activo, o permiso faltante → 403
/// <c>authorization.denied</c>, nunca 404. Después, la capacidad <c>ai</c> → 403 <c>tenancy.module_not_enabled</c>.</summary>
internal static class AiAuthorization
{
    public static async Task EnsureAsync(
        IExecutionContext executionContext, ITenantModules tenantModules, Guid tenantId, string permission, CancellationToken cancellationToken)
    {
        if (executionContext.TenantId.Value != tenantId || !executionContext.HasPermission(permission))
        {
            throw Denied();
        }

        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, tenantId, TenantModuleKeys.Ai, cancellationToken);
    }

    public static RequestForbiddenException Denied() =>
        new("authorization.denied", "The subject cannot perform this AI operation for this tenant.");
}

/// <summary>La membresía activa de quien llama, resuelta una vez por request (scoped), como <c>AutomationsCaller</c>: el autor del borrador y
/// de cada publicación.</summary>
public sealed class AiCaller(IMembershipDirectory membershipDirectory, IExecutionContext executionContext)
{
    private Guid? _tenantId;
    private Guid? _memberId;

    /// <summary>Sin membresía activa no hay a quién atribuir el cambio: 403.</summary>
    public async Task<Guid> RequireAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (_tenantId != tenantId)
        {
            _memberId = await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, tenantId, cancellationToken);
            _tenantId = tenantId;
        }

        return _memberId ?? throw AiAuthorization.Denied();
    }
}

internal static class AiNotFound
{
    public static ResourceNotFoundException Agent(Guid id) => new(AiErrorCodes.AgentNotFound, $"Agent '{id}' was not found.");

    public static ResourceNotFoundException Connection(Guid id) => new(AiErrorCodes.ConnectionNotFound, $"Connection '{id}' was not found.");
}

internal static class AiVersions
{
    /// <summary>La versión que el cliente leyó tiene que ser la actual: si no, 412 sin tocar nada.</summary>
    public static void Ensure(long current, long received)
    {
        if (received != current)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The agent changed after it was loaded.");
        }
    }
}
