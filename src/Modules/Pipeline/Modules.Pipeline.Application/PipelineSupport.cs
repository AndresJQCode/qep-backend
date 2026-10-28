using BuildingBlocks.Application;
using Modules.Pipeline.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Pipeline.Application;

/// <summary>Copia de <c>MessagingAuthorization</c>: tenant de la ruta distinto del activo, o permiso faltante → 403
/// <c>authorization.denied</c>, nunca 404. Después, la capacidad <c>pipeline</c> → 403 <c>tenancy.module_not_enabled</c> (P1).</summary>
internal static class PipelineAuthorization
{
    public static async Task EnsureAsync(
        IExecutionContext executionContext, ITenantModules tenantModules, Guid tenantId, string permission, CancellationToken cancellationToken)
    {
        if (executionContext.TenantId.Value != tenantId || !executionContext.HasPermission(permission))
        {
            throw Denied();
        }

        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, tenantId, TenantModuleKeys.Pipeline, cancellationToken);
    }

    public static RequestForbiddenException Denied() =>
        new("authorization.denied", "The subject cannot perform this pipeline operation for this tenant.");
}

/// <summary>La membresía activa de quien llama en el tenant, resuelta una vez por request (scoped), como <c>CallerMembership</c>
/// de Messaging. La usan el dueño por defecto, <c>owner=me</c> y el actor del historial.</summary>
public sealed class PipelineCaller(IMembershipDirectory membershipDirectory, IExecutionContext executionContext)
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

    /// <summary>Sin membresía activa no hay a quién atribuir el cambio: 403, como <c>QuotationAdvisorResolver</c>.</summary>
    public async Task<Guid> RequireAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await FindAsync(tenantId, cancellationToken) ?? throw PipelineAuthorization.Denied();
}

internal static class PipelineNotFound
{
    public static ResourceNotFoundException Deal(Guid id) => new(PipelineErrorCodes.DealNotFound, $"Deal '{id}' was not found.");

    public static ResourceNotFoundException Customer(Guid id) => new(PipelineErrorCodes.CustomerNotFound, $"Customer '{id}' was not found.");

    public static ResourceNotFoundException Conversation(Guid id) =>
        new(PipelineErrorCodes.TriageConversationNotFound, $"Conversation '{id}' was not found.");
}

internal static class PipelineVersions
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
