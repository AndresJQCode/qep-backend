using Modules.Payroll.Domain;
using Modules.Tenancy.Application;

namespace Modules.Payroll.Application;

/// <summary>Quién actúa: el usuario de la solicitud y el nombre de su membresía en el tenant, que se guarda con cada evento del historial.</summary>
public sealed class PeriodActors(IExecutionContext executionContext, IPayrollMemberDirectory members)
{
    /// <summary>El mismo rótulo que el panel de Personas cuando la membresía no tiene nombre.</summary>
    public const string NoName = "Sin nombre";

    /// <summary>El actor de esta solicitud.</summary>
    public async Task<PayrollActor> CurrentAsync(Guid tenantId, CancellationToken cancellationToken) =>
        new(executionContext.SubjectId, await members.FindDisplayNameAsync(tenantId, executionContext.SubjectId, cancellationToken) ?? NoName);

    /// <summary>Los permisos de nómina de quien llama: lo que el compositor cruza con el estado para armar <c>can</c>.</summary>
    public IReadOnlySet<string> CallerPermissions() =>
        new[]
        {
            PayrollPermissions.PeriodRead, PayrollPermissions.PeriodManage, PayrollPermissions.PeriodApprove,
            PayrollPermissions.SettingsManage,
        }
        .Where(executionContext.HasPermission)
        .ToHashSet(StringComparer.Ordinal);
}
