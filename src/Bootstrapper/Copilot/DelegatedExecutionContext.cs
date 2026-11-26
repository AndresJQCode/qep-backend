using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.Copilot;

/// <summary>
/// Spec 2026-11-17 (copiloto) D-C3: el <see cref="IExecutionContext"/> del miembro que le preguntó al copiloto, para correr una consulta
/// existente fuera de un request. Los permisos son los que <c>IAuthorizationService.ResolvePermissionsAsync</c> dio <b>para esta
/// llamada</b> (membresía activa, roles, máscara de módulos del tenant, filtro de <c>operator.*</c>), los mismos que un request de ese
/// miembro tendría en sus claims. Lo crea sólo <see cref="CopilotToolExecutor"/> (<c>CompositionRootTests</c>).
/// </summary>
internal sealed class DelegatedExecutionContext : IExecutionContext
{
    private readonly HashSet<string> _permissions;

    internal DelegatedExecutionContext(Guid subjectId, Guid tenantId, IEnumerable<string> permissions)
    {
        SubjectId = subjectId;
        TenantId = new TenantId(tenantId);
        _permissions = new HashSet<string>(permissions, StringComparer.Ordinal);
    }

    public Guid SubjectId { get; }

    public TenantId TenantId { get; }

    public bool HasPermission(string permission) => _permissions.Contains(permission);
}

/// <summary>El contexto delegado de un scope, si lo hay. Scoped: vive lo que dura el scope que abrió el ejecutor del copiloto y no se ve
/// desde ningún otro.</summary>
internal sealed class DelegatedExecutionScope
{
    public DelegatedExecutionContext? Context { get; private set; }

    /// <summary>Una sola vez y antes de resolver nada que lea <see cref="IExecutionContext"/>: un servicio ya resuelto en el scope se quedaría
    /// con el contexto de antes.</summary>
    public void Enter(DelegatedExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Context is not null)
        {
            throw new InvalidOperationException("The scope already runs as a delegated member.");
        }

        Context = context;
    }
}
