using BuildingBlocks.Application;
using Modules.Automations.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Automations.Application;

/// <summary>Copia de <c>PipelineAuthorization</c>: tenant de la ruta distinto del activo, o permiso faltante → 403
/// <c>authorization.denied</c>, nunca 404. Después, la capacidad <c>automations</c> → 403 <c>tenancy.module_not_enabled</c>. El
/// permiso llega por parámetro: las constantes las declara <c>AutomationsPermissions</c> junto con su definición y su política.</summary>
internal static class AutomationsAuthorization
{
    public static async Task EnsureAsync(
        IExecutionContext executionContext, ITenantModules tenantModules, Guid tenantId, string permission, CancellationToken cancellationToken)
    {
        if (executionContext.TenantId.Value != tenantId || !executionContext.HasPermission(permission))
        {
            throw Denied();
        }

        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, tenantId, TenantModuleKeys.Automations, cancellationToken);
    }

    public static RequestForbiddenException Denied() =>
        new("authorization.denied", "The subject cannot perform this automations operation for this tenant.");
}

/// <summary>La membresía activa de quien llama en el tenant, resuelta una vez por request (scoped), como <c>PipelineCaller</c>. La
/// usan el autor del borrador, el de cada publicación y el actor de la auditoría.</summary>
public sealed class AutomationsCaller(IMembershipDirectory membershipDirectory, IExecutionContext executionContext)
{
    private Guid? _tenantId;
    private Guid? _memberId;

    public async Task<Guid?> FindAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (_tenantId != tenantId)
        {
            _memberId = await membershipDirectory.FindActiveMembershipIdAsync(executionContext.SubjectId, tenantId, cancellationToken);
            _tenantId = tenantId;
        }

        return _memberId;
    }

    /// <summary>Sin membresía activa no hay a quién atribuir el cambio: 403, como <c>PipelineCaller.RequireAsync</c>.</summary>
    public async Task<Guid> RequireAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await FindAsync(tenantId, cancellationToken) ?? throw AutomationsAuthorization.Denied();
}

internal static class AutomationsNotFound
{
    public static ResourceNotFoundException Chatbot(Guid id) =>
        new(AutomationsErrorCodes.ChatbotNotFound, $"Chatbot '{id}' was not found.");

    /// <summary>El id llega como texto: el de una plantilla del sistema o el Guid de una del tenant.</summary>
    public static ResourceNotFoundException Template(string id) =>
        new(AutomationsErrorCodes.TemplateNotFound, $"Chatbot template '{id}' was not found.");

    public static ResourceNotFoundException Simulation(Guid id) =>
        new(AutomationsErrorCodes.SimulationNotFound, $"Simulation '{id}' was not found.");

    public static ResourceNotFoundException ExternalApi(Guid id) =>
        new(AutomationsErrorCodes.ExternalApiNotFound, $"External API connection '{id}' was not found.");

    public static ResourceNotFoundException Connection(Guid id) =>
        new(AutomationsErrorCodes.ConnectionNotFound, $"Connection '{id}' was not found.");
}

internal static class AutomationsVersions
{
    /// <summary>La versión que el cliente leyó tiene que ser la actual: si no, 412 sin tocar nada.</summary>
    public static void Ensure(long current, long? received)
    {
        if (received != current)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The resource changed after it was loaded.");
        }
    }
}
