using BuildingBlocks.Application;
using FluentValidation;
using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <param name="IfMatch">La versión que la pantalla cargó; llega por <c>If-Match</c>, crudo: el caso de uso lo lee ya autorizado.</param>
public sealed record RewriteAssignmentCommand(
    Guid TenantId, Guid EmployeeId, Guid ContractId, Guid RecordId, AssignmentRewriteRequest Request, string? IfMatch)
    : ICommand<EmployeeDetailDto>;

/// <summary>
/// Reescribe un registro de cargo y lugar en su lugar (spec 2026-11-26): fecha, cargo, área y centro. Exige la confirmación y el
/// <c>If-Match</c>, como «Editar» salario. El agregado revisa los vecinos; acá se revisan los campos y la nómina congelada, que
/// alcanza desde la menor entre la fecha vieja y la nueva.
/// </summary>
public sealed class RewriteAssignmentHandler(
    ContractWrites writes,
    IHrSettingsRepository settings,
    IFrozenPayrollPeriods frozenPayroll,
    IValidator<AssignmentRewriteRequest> validator)
    : ICommandHandler<RewriteAssignmentCommand, EmployeeDetailDto>
{
    private static readonly AssignmentKeys Keys = new("positionId", "areaId", "workplaceId");

    public async Task<EmployeeDetailDto> HandleAsync(RewriteAssignmentCommand command, CancellationToken cancellationToken)
    {
        await writes.AuthorizeAsync(command.TenantId, cancellationToken);
        var expectedVersion = HrIfMatch.Require(command.IfMatch);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var request = command.Request;
        var employee = await writes.LoadEmployeeAsync(command.TenantId, command.EmployeeId, cancellationToken);
        HrSettingsSupport.EnsureVersion(employee.Version, expectedVersion);
        var contract = ContractRules.FindContract(employee, command.ContractId);
        var record = ContractRules.FindAssignment(contract, command.RecordId);
        ContractRules.EnsureActive(contract);
        var effectiveDate = request.EffectiveDate!.Value;
        ContractRules.EnsureEffectiveDateWithinContract(contract, effectiveDate);

        var (position, area, workplace, site) = await AssignmentLookups.ResolveAsync(
            settings, command.TenantId, request.PositionId!.Value, request.AreaId, request.WorkplaceId!.Value, Keys, record, cancellationToken);
        ContractRules.EnsureWorkplaceOfCompany(site, contract.CompanyId, Keys.Workplace);

        var calendar = await writes.CalendarAsync(command.TenantId, cancellationToken);
        var names = await writes.NamesAsync(command.TenantId, [contract.CompanyId], cancellationToken);
        var before = record.ToEntry();

        ContractRules.Run(() => employee.RewriteAssignment(
            contract.Id,
            record.Id,
            new AssignmentEntry(effectiveDate, position.Id, area?.Id, site.Id, workplace.Id),
            request.ConfirmRewrite == true,
            calendar.UtcNow));

        // Con el registro ya reescrito: el rango llega hasta el fin de lo que rige ahora (el siguiente no cambia).
        var frozen = await frozenPayroll.ListForEmployeeAsync(command.TenantId, employee.Id.Value, cancellationToken);
        AssignmentPayrollGuard.EnsureFree(
            frozen, contract.CompanyId, AssignmentPayrollGuard.RewriteRange(before.EffectiveDate, contract, record));

        return await writes.CommitAsync(
            command.TenantId,
            employee,
            HrActivityAction.AssignmentRewritten,
            HrActivityCategory.Assignment,
            contract.Id.Value,
            ActivityChanges.AssignmentRewritten(before, record, names),
            HrAuditActions.ContractAssignmentRewritten,
            HrAuditActions.Resources.Contract,
            contract.Id.Value,
            calendar,
            cancellationToken);
    }
}
