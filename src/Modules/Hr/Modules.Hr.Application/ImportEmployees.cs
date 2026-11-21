using BuildingBlocks.Application;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application;

/// <summary>El Excel subido a <c>POST /employees/import</c>.</summary>
public sealed record ImportEmployeesCommand(Guid TenantId, string FileName, long SizeInBytes, Stream Content)
    : ICommand<EmployeeImportResultDto>;

/// <summary>
/// La confirmación, todo o nada: corre el mismo análisis que la vista previa y, con alguna fila con <b>error</b>, responde 422
/// <c>hr.import.rows_invalid</c> sin escribir. Los avisos no bloquean. Si no, da de alta cada empleado con su contrato, salario y
/// cargo (y lo termina si trae retiro), deja un historial y una auditoría por empleado y guarda <b>una sola vez</b>: una violación
/// única en la carrera (alguien creó el documento después del análisis) sale 409 <c>hr.employee.document_taken</c> desde
/// <c>HrUnitOfWork</c> y no queda nada.
/// </summary>
public sealed class ImportEmployeesHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IClock clock,
    EmployeeImportAnalyzer analyzer,
    IEmployeeRepository employees,
    ActivityWriter activity,
    IHrUnitOfWork unitOfWork,
    IHrAuditPublisher auditPublisher)
    : ICommandHandler<ImportEmployeesCommand, EmployeeImportResultDto>
{
    public async Task<EmployeeImportResultDto> HandleAsync(ImportEmployeesCommand command, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext,
            tenantModules,
            command.TenantId,
            cancellationToken,
            HrPermissions.EmployeeRead,
            HrPermissions.EmployeeManage,
            HrPermissions.ContractRead,
            HrPermissions.ContractManage);

        var analysis = await analyzer.AnalyzeAsync(
            command.TenantId, command.FileName, command.SizeInBytes, command.Content, cancellationToken);
        if (analysis.Errors.Count > 0)
        {
            throw new HrDomainException(HrErrorCodes.ImportRowsInvalid, "El archivo tiene filas con errores. Corrígelas y vuelve a intentar.");
        }

        var tenantId = command.TenantId;
        var receivedAt = clock.UtcNow;
        var names = await activity.NamesAsync(tenantId, analysis.ValidRows.Select(row => row.Terms.CompanyId), cancellationToken);

        var hired = new List<(ParsedEmployeeRow Row, Employee Employee)>(analysis.ValidRows.Count);
        foreach (var row in analysis.ValidRows)
        {
            var employee = Hire(tenantId, row, analysis, receivedAt, names, out var changes, out var contractId);
            hired.Add((row, employee));
            await activity.AppendAsync(
                tenantId, employee, HrActivityAction.EmployeeImported, HrActivityCategory.Lifecycle, contractId, changes, cancellationToken);
            HrSettingsSupport.PublishAudit(
                auditPublisher, executionContext, tenantId, HrAuditActions.EmployeeImported, HrAuditActions.Resources.Employee,
                employee.Id.Value, changes.Select(change => change.Field).Distinct().ToList(), receivedAt);
        }

        employees.AddRange(hired.Select(entry => entry.Employee));
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new EmployeeImportResultDto(
            analysis.FileName,
            receivedAt,
            hired.Count,
            hired.Select(entry => new EmployeeImportedRowDto(entry.Row.RowNumber, entry.Employee.Id.Value)).ToList());
    }

    private static Employee Hire(
        Guid tenantId,
        ParsedEmployeeRow row,
        EmployeeImportAnalysis analysis,
        DateTimeOffset now,
        IReadOnlyDictionary<Guid, string> names,
        out IReadOnlyList<ActivityChange> changes,
        out Guid contractId)
    {
        var terms = row.Terms;
        var contract = new NewContract(
            EmploymentContractId.New(),
            terms,
            null,
            SalaryRecordId.New(),
            row.Salary,
            row.Teleworker,
            row.BelowMinimum,
            AssignmentRecordId.New(),
            row.PositionId,
            row.AreaId,
            row.Workplace.SiteId,
            row.Workplace.Id);

        // Los pisos de la fecha de ingreso salen de la tabla que el análisis ya leyó: el parser comprobó que existen.
        var floor = new SalaryFloor(
            terms.StartDate,
            LegalParameters.Resolve(analysis.LegalTable, LegalParameterKey.Smmlv, terms.StartDate)!.Value,
            LegalParameters.Resolve(analysis.LegalTable, LegalParameterKey.IntegralSalaryFloor, terms.StartDate)!.Value);

        var employee = HrConflicts.Run(() => Employee.Hire(
            EmployeeId.New(), tenantId, row.Profile, row.Payment, contract, floor, now));
        var created = ActivityChanges.EmployeeCreated(row.Profile, row.Payment, employee.ActiveContract!, names);
        contractId = contract.Id.Value;

        if (row.RetirementDate is { } date && row.RetirementReason is { } reason)
        {
            ContractRules.Run(() => employee.Terminate(contract.Id, reason, date, null, now));
            changes = [.. created, .. ActivityChanges.Terminated(reason, date)];
        }
        else
        {
            changes = created;
        }

        return employee;
    }
}
