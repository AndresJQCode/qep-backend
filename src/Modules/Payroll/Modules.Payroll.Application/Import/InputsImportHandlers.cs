using BuildingBlocks.Application;
using Modules.Payroll.Domain;
using Modules.Tenancy.Application;

namespace Modules.Payroll.Application;

public sealed record GetInputsImportTemplateQuery(Guid TenantId, Guid PeriodId) : IQuery<InputsImportTemplateFile>;

/// <summary>El Excel subido a <c>POST {p}/inputs/import/preview</c>. <c>Content</c> es el stream del <c>IFormFile</c>, vivo mientras dura el request.</summary>
public sealed record PreviewInputsImportCommand(Guid TenantId, Guid PeriodId, string FileName, long SizeInBytes, Stream Content)
    : ICommand<InputsImportPreviewDto>;

/// <summary>El Excel subido a <c>POST {p}/inputs/import</c>, con la versión del período que la pantalla cargó.</summary>
public sealed record ImportInputsCommand(
    Guid TenantId, Guid PeriodId, string FileName, long SizeInBytes, Stream Content, string? IfMatch)
    : ICommand<PayrollPeriodDetailDto>;

/// <summary>
/// La plantilla de la quincena: los empleados que se pueden capturar y los conceptos vivos de la empresa. Sólo en un período que se
/// puede editar (si no, 422 <c>not_editable</c>).
/// </summary>
public sealed class GetInputsImportTemplateHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IPayrollPeriodRepository periods,
    IPayrollHrDirectory hr,
    IPayrollLegalValues legalValues,
    ConceptCatalog catalog,
    IInputsImportTemplateBuilder builder)
    : IQueryHandler<GetInputsImportTemplateQuery, InputsImportTemplateFile>
{
    public async Task<InputsImportTemplateFile> HandleAsync(GetInputsImportTemplateQuery query, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken,
            PayrollPermissions.PeriodRead, PayrollPermissions.PeriodManage);

        var period = await periods.FindAsync(query.TenantId, new PayrollPeriodId(query.PeriodId), cancellationToken)
            ?? throw PayrollNotFound.Period(query.PeriodId);
        period.EnsureEditable();

        var smmlv = (PayrollResultMapper.ParseLegal(period.LegalValuesJson)
            ?? await legalValues.ResolveAsync(period.StartDate, cancellationToken)).Smmlv.Value;
        var employees = (await hr.ListEmployeesAsync(
                query.TenantId, period.CompanyId, period.StartDate, period.EndDate, cancellationToken))
            .Where(employee => PayrollEligibility.Evaluate(employee, period.StartDate, period.EndDate, smmlv).Status != EligibilityStatus.Excluded)
            .OrderBy(employee => LiquidationPlanner.FullNameOf(employee), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(employee => employee.EmployeeId)
            .Select(employee => new InputsTemplateEmployee(employee.DocumentNumber, LiquidationPlanner.FullNameOf(employee)))
            .ToList();

        var concepts = (await catalog.LoadAsync(query.TenantId, period.CompanyId, cancellationToken))
            .ToDtos()
            .Where(concept => !concept.Archived)
            .Select(concept => concept.Name)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new InputsImportTemplateFile(
            builder.Build(new InputsImportTemplateData(employees, concepts)), InputsImportColumns.TemplateFileName);
    }
}

/// <summary>La vista previa: el mismo análisis que la confirmación, sin escribir nada.</summary>
public sealed class PreviewInputsImportHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IPayrollPeriodRepository periods,
    InputsImportAnalyzer analyzer)
    : ICommandHandler<PreviewInputsImportCommand, InputsImportPreviewDto>
{
    public async Task<InputsImportPreviewDto> HandleAsync(PreviewInputsImportCommand command, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            PayrollPermissions.PeriodRead, PayrollPermissions.PeriodManage);

        var period = await periods.FindAsync(command.TenantId, new PayrollPeriodId(command.PeriodId), cancellationToken)
            ?? throw PayrollNotFound.Period(command.PeriodId);
        period.EnsureEditable();

        var analysis = await analyzer.AnalyzeAsync(period, command.FileName, command.SizeInBytes, command.Content, cancellationToken);

        var preview = analysis.ValidRows.Select(row => new InputsImportRowPreviewDto(
            row.RowNumber,
            analysis.Employees[row.EmployeeId].DocumentNumber,
            row.FullName,
            row.ConceptName,
            row.Draft.Kind.ToString(),
            row.Draft.Hours,
            row.Draft.Amount,
            row.Draft.SalaryConstitutive))
            .ToList();

        return new InputsImportPreviewDto(
            analysis.FileName,
            analysis.TotalRows,
            preview,
            analysis.Errors,
            new InputsImportSummaryDto(
                analysis.ValidRows.Select(row => row.EmployeeId).Distinct().Count(),
                analysis.ValidRows.Count(row => row.Draft.Kind == ConceptKind.Overtime),
                analysis.ValidRows.Count(row => row.Draft.Kind == ConceptKind.Income),
                analysis.ValidRows.Count(row => row.Draft.Kind == ConceptKind.Deduction)));
    }
}

/// <summary>
/// La confirmación, todo o nada (R-24): corre el mismo análisis y, con alguna fila con error, responde 422
/// <c>payroll.import.rows_invalid</c> sin escribir. Si no, reemplaza las capturas de cada empleado del archivo (los que no aparecen no
/// se tocan), sube la versión del período una vez y deja el evento <c>InputsImported</c>.
/// </summary>
public sealed class ImportInputsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IPayrollPeriodRepository periods,
    InputsImportAnalyzer analyzer,
    PeriodActors actors,
    PeriodComposer composer,
    IPayrollUnitOfWork unitOfWork,
    IPayrollAuditPublisher auditPublisher)
    : ICommandHandler<ImportInputsCommand, PayrollPeriodDetailDto>
{
    public async Task<PayrollPeriodDetailDto> HandleAsync(ImportInputsCommand command, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken,
            PayrollPermissions.PeriodRead, PayrollPermissions.PeriodManage);
        var expectedVersion = PayrollIfMatch.Require(command.IfMatch);

        var period = await periods.FindAsync(command.TenantId, new PayrollPeriodId(command.PeriodId), cancellationToken)
            ?? throw PayrollNotFound.Period(command.PeriodId);
        PayrollSupport.EnsureVersion(period.Version, expectedVersion);
        period.EnsureEditable();

        var analysis = await analyzer.AnalyzeAsync(period, command.FileName, command.SizeInBytes, command.Content, cancellationToken);
        if (analysis.Errors.Count > 0)
        {
            throw new PayrollDomainException(
                PayrollErrorCodes.ImportRowsInvalid, "El archivo tiene filas con errores. Corrígelas y vuelve a intentar.");
        }

        var byEmployee = new Dictionary<Guid, List<PeriodInputDraft>>();
        foreach (var row in analysis.ValidRows)
        {
            if (!byEmployee.TryGetValue(row.EmployeeId, out var lines))
            {
                lines = [];
                byEmployee[row.EmployeeId] = lines;
            }

            lines.Add(row.Draft);
        }

        var actor = await actors.CurrentAsync(command.TenantId, cancellationToken);
        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        period.ReplaceInputsBulk(
            byEmployee.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<PeriodInputDraft>)entry.Value), actor, now);

        PayrollSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId, PayrollAuditActions.InputsImported,
            PayrollAuditActions.Resources.Period, period.Id.ToString(), ["overtime", "incomes", "deductions"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeAsync(period, actor, actors.CallerPermissions(), cancellationToken);
    }
}
