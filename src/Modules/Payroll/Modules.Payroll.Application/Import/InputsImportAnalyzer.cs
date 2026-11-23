using Modules.Payroll.Domain;

namespace Modules.Payroll.Application;

/// <summary>Lo que la vista previa y la confirmación ven del archivo. <c>TotalRows</c> son las filas con datos.</summary>
/// <param name="Employees">Los empleados de las filas válidas: el documento de la vista previa sale de acá.</param>
public sealed record InputsImportAnalysis(
    string FileName,
    int TotalRows,
    IReadOnlyList<ParsedInputRow> ValidRows,
    IReadOnlyList<InputsImportRowIssueDto> Errors,
    IReadOnlyDictionary<Guid, HrEmployeeSource> Employees);

/// <summary>
/// El camino compartido de la vista previa y la confirmación: archivo → lector → contexto de la quincena → reglas de fila → horas
/// repetidas en el archivo → topes por empleado. No escribe. Como no hay staging, la confirmación vuelve a correr todo sobre el mismo
/// archivo.
/// </summary>
public sealed class InputsImportAnalyzer(
    IExcelInputsImporter importer,
    IPayrollHrDirectory hr,
    IPayrollLegalValues legalValues,
    ConceptCatalog catalog)
{
    public async Task<InputsImportAnalysis> AnalyzeAsync(
        PayrollPeriod period, string? fileName, long sizeInBytes, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(period);

        var name = EnsureAcceptableFile(fileName, sizeInBytes);
        var rows = await importer.ReadAsync(content, cancellationToken);
        if (rows.Count == 0)
        {
            throw new PayrollDomainException(PayrollErrorCodes.ImportEmpty, "El archivo no tiene filas con datos.");
        }

        if (rows.Count > InputsImportColumns.MaxRows)
        {
            throw new PayrollDomainException(
                PayrollErrorCodes.ImportTooManyRows, $"El archivo tiene más de {InputsImportColumns.MaxRows} filas.");
        }

        var (context, sources) = await ContextAsync(period, cancellationToken);

        var errors = new List<InputsImportRowIssueDto>();
        var valid = new Dictionary<int, ParsedInputRow>();
        foreach (var row in rows)
        {
            var result = InputsImportRowParser.Parse(row, context);
            errors.AddRange(result.Errors);
            if (result.Row is not null)
            {
                valid[row.RowNumber] = result.Row;
            }
        }

        // The same overtime concept twice for one employee marks both rows: the capture holds one line per overtime concept.
        foreach (var group in valid.Values
                     .Where(row => row.Draft.Kind == ConceptKind.Overtime)
                     .GroupBy(row => (row.EmployeeId, row.Draft.ConceptId))
                     .Where(group => group.Count() > 1))
        {
            foreach (var row in group)
            {
                errors.Add(new InputsImportRowIssueDto(
                    row.RowNumber,
                    PayrollErrorCodes.ImportRowOvertimeDuplicatedInFile,
                    "Ese concepto de horas extras se repite para el mismo empleado: suma las horas en una sola fila.",
                    "concept"));
                valid.Remove(row.RowNumber);
            }
        }

        // Pre-flight M-13: the same limits as a manual capture, per employee; every row beyond the limit is marked, in row order.
        foreach (var byEmployee in valid.Values.OrderBy(row => row.RowNumber).GroupBy(row => row.EmployeeId).ToList())
        {
            foreach (var (kind, limit) in new[]
                     {
                         (ConceptKind.Overtime, InputLimits.MaxOvertimeLines),
                         (ConceptKind.Income, InputLimits.MaxIncomeLines),
                         (ConceptKind.Deduction, InputLimits.MaxDeductionLines),
                     })
            {
                foreach (var row in byEmployee.Where(row => row.Draft.Kind == kind).Skip(limit).ToList())
                {
                    errors.Add(new InputsImportRowIssueDto(
                        row.RowNumber,
                        PayrollErrorCodes.ImportRowTooManyLines,
                        $"Un empleado no puede tener más de {limit} líneas de este tipo en la quincena.",
                        "concept"));
                    valid.Remove(row.RowNumber);
                }
            }
        }

        return new InputsImportAnalysis(
            name,
            rows.Count,
            valid.Values.OrderBy(row => row.RowNumber).ToList(),
            errors.OrderBy(issue => issue.RowNumber).ThenBy(issue => issue.Field, StringComparer.Ordinal).ToList(),
            valid.Values.Select(row => row.EmployeeId).Distinct().ToDictionary(
                id => id,
                id => sources.Values.First(source => source.EmployeeId == id)));
    }

    private async Task<(InputsImportContext Context, IReadOnlyDictionary<string, HrEmployeeSource> Sources)> ContextAsync(
        PayrollPeriod period, CancellationToken cancellationToken)
    {
        var smmlv = (PayrollResultMapper.ParseLegal(period.LegalValuesJson)
            ?? await legalValues.ResolveAsync(period.StartDate, cancellationToken)).Smmlv.Value;
        var employees = await hr.ListEmployeesAsync(
            period.TenantId, period.CompanyId, period.StartDate, period.EndDate, cancellationToken);

        var eligibility = new Dictionary<string, EligibilityResult>(StringComparer.Ordinal);
        var sources = new Dictionary<string, HrEmployeeSource>(StringComparer.Ordinal);
        foreach (var employee in employees)
        {
            if (PayrollText.Fold(employee.DocumentNumber) is { } key && !sources.ContainsKey(key))
            {
                sources[key] = employee;
                eligibility[key] = PayrollEligibility.Evaluate(employee, period.StartDate, period.EndDate, smmlv);
            }
        }

        var concepts = await catalog.LoadAsync(period.TenantId, period.CompanyId, cancellationToken);
        return (new InputsImportContext(eligibility, sources, concepts), sources);
    }

    private static string EnsureAcceptableFile(string? fileName, long sizeInBytes)
    {
        if (string.IsNullOrWhiteSpace(fileName) || sizeInBytes <= 0)
        {
            throw new PayrollDomainException(PayrollErrorCodes.ImportFileRequired, "Adjunta un archivo .xlsx.");
        }

        if (!fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new PayrollDomainException(PayrollErrorCodes.ImportExtensionInvalid, "Sólo se aceptan archivos .xlsx.");
        }

        if (sizeInBytes > InputsImportColumns.MaxSizeInBytes)
        {
            throw new PayrollDomainException(
                PayrollErrorCodes.ImportTooLarge, $"El archivo pasa de {InputsImportColumns.MaxSizeInBytes / (1024 * 1024)} MB.");
        }

        return fileName.Trim();
    }
}
