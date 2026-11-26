using BuildingBlocks.Application;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application;

/// <summary>
/// Escribe las entradas del historial de acciones (<c>hr.employee_activity</c>) en la misma unidad de trabajo que el cambio: se
/// guardan juntas o ninguna. El actor es quien llama, con el nombre que tiene su membresía <b>al escribir</b> («Sin nombre» si no
/// tiene), y las referencias de los cambios van por el nombre que tenían entonces (<see cref="NamesAsync"/>).
/// </summary>
public sealed class ActivityWriter(
    IExecutionContext executionContext,
    IHrMemberDirectory members,
    IEmployeeActivityLog log,
    IClock clock,
    IHrSettingsRepository settings,
    HrCompanyRefs companies)
{
    private const string NoName = "Sin nombre";

    public async Task AppendAsync(
        Guid tenantId,
        Employee employee,
        HrActivityAction action,
        HrActivityCategory category,
        Guid? contractId,
        IReadOnlyList<ActivityChange> changes,
        CancellationToken cancellationToken)
    {
        var actor = await members.FindBySubjectAsync(tenantId, executionContext.SubjectId, cancellationToken);
        log.Append(new EmployeeActivityEntry(
            Guid.CreateVersion7(),
            tenantId,
            employee.Id,
            clock.UtcNow,
            executionContext.SubjectId,
            string.IsNullOrWhiteSpace(actor?.DisplayName) ? NoName : actor.DisplayName,
            action,
            category,
            contractId,
            changes));
    }

    /// <summary>
    /// Los nombres de toda la configuración del tenant y de las empresas pedidas, por id: lo que las referencias del historial
    /// guardan. Se lee una vez por escritura, antes de mutar.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(
        Guid tenantId, IEnumerable<Guid> companyIds, CancellationToken cancellationToken)
    {
        var snapshot = await settings.ListAsync(tenantId, cancellationToken);
        var names = new Dictionary<Guid, string>();
        foreach (var site in snapshot.Sites)
        {
            names[site.Id.Value] = site.Name;
        }

        foreach (var workplace in snapshot.Workplaces)
        {
            names[workplace.Id.Value] = workplace.Name;
        }

        foreach (var area in snapshot.Areas)
        {
            names[area.Id.Value] = area.Name;
        }

        foreach (var position in snapshot.Positions)
        {
            names[position.Id.Value] = position.Name;
        }

        foreach (var (id, company) in await companies.ResolveAsync(tenantId, companyIds, cancellationToken))
        {
            names[id] = company.Name;
        }

        return names;
    }
}
