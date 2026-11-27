using BuildingBlocks.Application;
using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <param name="IfMatch">La versión que la pantalla cargó; llega por <c>If-Match</c>, crudo: el caso de uso lo lee ya autorizado.</param>
public sealed record RemoveAssignmentCommand(Guid TenantId, Guid EmployeeId, Guid ContractId, Guid RecordId, string? IfMatch)
    : ICommand<EmployeeDetailDto>;

/// <summary>
/// Elimina un registro de cargo y lugar (spec 2026-11-26). El agregado revisa los vecinos; acá se revisa la nómina congelada sobre los
/// días que regía el registro, que pasan a regir el anterior. Si el registro tenía PDF, el aviso a Storage viaja en el mismo
/// <c>SaveChanges</c> que el borrado.
/// </summary>
public sealed class RemoveAssignmentHandler(
    ContractWrites writes,
    IFrozenPayrollPeriods frozenPayroll,
    IHrDocumentReleases documentReleases)
    : ICommandHandler<RemoveAssignmentCommand, EmployeeDetailDto>
{
    public async Task<EmployeeDetailDto> HandleAsync(RemoveAssignmentCommand command, CancellationToken cancellationToken)
    {
        await writes.AuthorizeAsync(command.TenantId, cancellationToken);
        var expectedVersion = HrIfMatch.Require(command.IfMatch);

        var employee = await writes.LoadEmployeeAsync(command.TenantId, command.EmployeeId, cancellationToken);
        HrSettingsSupport.EnsureVersion(employee.Version, expectedVersion);
        var contract = ContractRules.FindContract(employee, command.ContractId);
        var record = ContractRules.FindAssignment(contract, command.RecordId);
        ContractRules.EnsureActive(contract);

        // Antes de quitarlo: después ya no hay siguiente contra el cual medir su rango.
        var range = AssignmentPayrollGuard.RangeOf(contract, record);
        var calendar = await writes.CalendarAsync(command.TenantId, cancellationToken);
        var names = await writes.NamesAsync(command.TenantId, [contract.CompanyId], cancellationToken);

        var removed = ContractRules.Run(() => employee.RemoveAssignment(contract.Id, record.Id, calendar.UtcNow));

        var frozen = await frozenPayroll.ListForEmployeeAsync(command.TenantId, employee.Id.Value, cancellationToken);
        AssignmentPayrollGuard.EnsureFree(frozen, contract.CompanyId, range);

        if (removed.DocumentFileId is { } fileId)
        {
            documentReleases.Publish(command.TenantId, [fileId], calendar.UtcNow);
        }

        return await writes.CommitAsync(
            command.TenantId,
            employee,
            HrActivityAction.AssignmentDeleted,
            HrActivityCategory.Assignment,
            contract.Id.Value,
            ActivityChanges.AssignmentDeleted(removed, names),
            HrAuditActions.ContractAssignmentDeleted,
            HrAuditActions.Resources.Contract,
            contract.Id.Value,
            calendar,
            cancellationToken);
    }
}
