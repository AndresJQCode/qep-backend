using BuildingBlocks.Application;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application;

/// <summary>
/// Lo que repiten las escrituras de contrato: cargar el empleado y el contrato, y al final escribir el historial de acciones, auditar,
/// guardar y responder el detalle entero. Todas autorizan <c>EmployeeRead + ContractRead + ContractManage</c>.
/// </summary>
public sealed class ContractWrites(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IEmployeeRepository employees,
    ActivityWriter activity,
    IHrUnitOfWork unitOfWork,
    IHrAuditPublisher auditPublisher,
    EmployeeDetailComposer composer)
{
    public Task AuthorizeAsync(Guid tenantId, CancellationToken cancellationToken) => HrAuthorization.EnsureAsync(
        executionContext,
        tenantModules,
        tenantId,
        cancellationToken,
        HrPermissions.EmployeeRead,
        HrPermissions.ContractRead,
        HrPermissions.ContractManage);

    public async Task<Employee> LoadEmployeeAsync(Guid tenantId, Guid employeeId, CancellationToken cancellationToken) =>
        await employees.FindAsync(tenantId, new EmployeeId(employeeId), cancellationToken) ?? throw HrNotFound.Employee(employeeId);

    public Task<TenantCalendar> CalendarAsync(Guid tenantId, CancellationToken cancellationToken) =>
        tenantClock.GetAsync(tenantId, cancellationToken);

    public Task<IReadOnlyDictionary<Guid, string>> NamesAsync(
        Guid tenantId, IEnumerable<Guid> companyIds, CancellationToken cancellationToken) =>
        activity.NamesAsync(tenantId, companyIds, cancellationToken);

    /// <summary>
    /// Escribe el historial y la auditoría (sólo los <b>nombres</b> de los campos), guarda todo junto y responde el detalle: la pantalla
    /// se repinta de la respuesta. <paramref name="contractId"/> es el contrato de la acción (el nuevo, en una conversión).
    /// </summary>
    public async Task<EmployeeDetailDto> CommitAsync(
        Guid tenantId,
        Employee employee,
        HrActivityAction action,
        HrActivityCategory category,
        Guid contractId,
        IReadOnlyList<ActivityChange> changes,
        string auditAction,
        string auditResource,
        Guid auditResourceId,
        TenantCalendar calendar,
        CancellationToken cancellationToken)
    {
        await activity.AppendAsync(tenantId, employee, action, category, contractId, changes, cancellationToken);
        HrSettingsSupport.PublishAudit(
            auditPublisher, executionContext, tenantId, auditAction, auditResource, auditResourceId,
            changes.Select(change => change.Field).ToList(), calendar.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeAsync(
            employee, executionContext.HasPermission(HrPermissions.ContractRead), calendar.Today, cancellationToken);
    }
}
