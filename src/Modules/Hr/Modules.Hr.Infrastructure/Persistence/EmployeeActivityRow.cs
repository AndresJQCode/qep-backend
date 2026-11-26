using Modules.Hr.Domain;

namespace Modules.Hr.Infrastructure.Persistence;

/// <summary>
/// La fila de <c>hr.employee_activity</c>. El registro de Application (<c>EmployeeActivityEntry</c>) lleva <c>JsonElement</c>s;
/// acá los cambios son el texto del jsonb, y el log traduce de ida y vuelta.
/// </summary>
internal sealed class EmployeeActivityRow
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public EmployeeId EmployeeId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public Guid ActorSubjectId { get; set; }

    public string ActorName { get; set; } = string.Empty;

    public HrActivityAction Action { get; set; }

    public HrActivityCategory Category { get; set; }

    public Guid? ContractId { get; set; }

    public string Changes { get; set; } = "[]";
}
