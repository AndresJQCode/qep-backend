using System.Globalization;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application.MedicalExams;

/// <summary>A row that passed every rule: the employee it belongs to and the exam it will create.</summary>
public sealed record ParsedMedicalExamRow(
    int RowNumber,
    MedicalExamEmployeeRef Employee,
    MedicalExamType Type,
    DateOnly ExamDate,
    string? Provider,
    string? Restrictions,
    string? Recommendations,
    DateOnly? RestrictionsUntil);

public sealed record MedicalExamImportAnalysis(
    string FileName,
    int TotalRows,
    IReadOnlyList<ParsedMedicalExamRow> ValidRows,
    IReadOnlyList<MedicalExamImportIssueDto> Errors);

/// <summary>
/// The shared path of the preview and the confirmation (spec 2026-11-23 §8): acceptable file, rows, the rules of each row, rows that
/// repeat inside the file and rows that already exist. It never writes. The confirmation runs it again on the same file, so a row
/// that became a duplicate between the two is an error of that row.
/// </summary>
public sealed class MedicalExamImportAnalyzer(
    IExcelMedicalExamImporter importer,
    IMedicalExamRepository medicalExams,
    ITenantClock tenantClock)
{
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy"];

    public async Task<MedicalExamImportAnalysis> AnalyzeAsync(
        Guid tenantId, string? fileName, long sizeInBytes, Stream content, CancellationToken cancellationToken)
    {
        var name = EnsureAcceptableFile(fileName, sizeInBytes);
        var rows = await importer.ReadAsync(content, cancellationToken);
        if (rows.Count == 0)
        {
            throw new HrDomainException(HrErrorCodes.ImportEmpty, "El archivo no tiene filas con datos.");
        }

        if (rows.Count > MedicalExamImportColumns.MaxRows)
        {
            throw new HrDomainException(
                HrErrorCodes.ImportTooManyRows, $"El archivo tiene más de {MedicalExamImportColumns.MaxRows} filas.");
        }

        var today = (await tenantClock.GetAsync(tenantId, cancellationToken)).Today;
        var errors = new List<MedicalExamImportIssueDto>();
        var parsed = new List<RowDraft>();
        foreach (var row in rows)
        {
            var draft = new Reader(row, today, errors).Run();
            if (draft is not null)
            {
                parsed.Add(draft);
            }
        }

        // The employees: by number, then the type has to match (the unique index of Hr is by number only).
        IReadOnlyList<MedicalExamEmployeeRef> employees = parsed.Count == 0
            ? []
            : await medicalExams.FindEmployeesByDocumentAsync(
                tenantId, parsed.Select(draft => draft.DocumentNumber).Distinct(StringComparer.Ordinal).ToList(), cancellationToken);
        var byNumber = employees.ToDictionary(employee => employee.DocumentNumber, StringComparer.Ordinal);
        var resolved = new List<ParsedMedicalExamRow>();
        foreach (var draft in parsed)
        {
            if (!byNumber.TryGetValue(draft.DocumentNumber, out var employee) || employee.DocumentType != draft.DocumentType)
            {
                errors.Add(new MedicalExamImportIssueDto(
                    draft.RowNumber,
                    HrErrorCodes.MedicalExamImportRow.EmployeeNotFound,
                    "No hay un empleado con ese tipo y número de documento.",
                    "documentNumber"));
                continue;
            }

            resolved.Add(new ParsedMedicalExamRow(
                draft.RowNumber, employee, draft.Type, draft.ExamDate, draft.Provider, draft.Restrictions, draft.Recommendations,
                draft.RestrictionsUntil));
        }

        // The same exam twice inside the file: every row of the group is marked, like the employee import does with a document.
        var valid = resolved.ToDictionary(row => row.RowNumber);
        foreach (var group in resolved.GroupBy(row => (row.Employee.Id, row.Type, row.ExamDate)).Where(group => group.Count() > 1))
        {
            foreach (var row in group)
            {
                errors.Add(new MedicalExamImportIssueDto(
                    row.RowNumber,
                    HrErrorCodes.MedicalExamImportRow.DuplicatedInFile,
                    "Hay otra fila del archivo con el mismo empleado, tipo y fecha de examen.",
                    "examDate"));
                valid.Remove(row.RowNumber);
            }
        }

        // And the ones that already exist: running the same file twice writes nothing the second time.
        if (valid.Count > 0)
        {
            var existing = await medicalExams.ListExamKeysAsync(
                tenantId, valid.Values.Select(row => row.Employee.Id).Distinct().ToList(), cancellationToken);
            foreach (var row in valid.Values.Where(row => existing.Contains((row.Employee.Id, row.Type, row.ExamDate))).ToList())
            {
                errors.Add(new MedicalExamImportIssueDto(
                    row.RowNumber,
                    HrErrorCodes.MedicalExamImportRow.Duplicate,
                    "Ya hay un examen de ese tipo registrado para ese empleado en esa fecha.",
                    "examDate"));
                valid.Remove(row.RowNumber);
            }
        }

        return new MedicalExamImportAnalysis(
            name,
            rows.Count,
            valid.Values.OrderBy(row => row.RowNumber).ToList(),
            errors.OrderBy(issue => issue.RowNumber).ThenBy(issue => issue.Field, StringComparer.Ordinal).ToList());
    }

    private static string EnsureAcceptableFile(string? fileName, long sizeInBytes)
    {
        if (string.IsNullOrWhiteSpace(fileName) || sizeInBytes <= 0)
        {
            throw new HrDomainException(HrErrorCodes.ImportFileRequired, "Adjunta un archivo .xlsx.");
        }

        if (!fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new HrDomainException(HrErrorCodes.ImportExtensionInvalid, "Sólo se aceptan archivos .xlsx.");
        }

        if (sizeInBytes > MedicalExamImportColumns.MaxSizeInBytes)
        {
            throw new HrDomainException(
                HrErrorCodes.ImportTooLarge, $"El archivo pasa de {MedicalExamImportColumns.MaxSizeInBytes / (1024 * 1024)} MB.");
        }

        return fileName.Trim();
    }

    private sealed record RowDraft(
        int RowNumber,
        HrDocumentType DocumentType,
        string DocumentNumber,
        MedicalExamType Type,
        DateOnly ExamDate,
        string? Provider,
        string? Restrictions,
        string? Recommendations,
        DateOnly? RestrictionsUntil);

    /// <summary>The rules of one row, pure: it gathers <b>all</b> the errors of the row. Only a row without errors gives a draft.</summary>
    private sealed class Reader(ExcelMedicalExamRow source, DateOnly today, List<MedicalExamImportIssueDto> sink)
    {
        private bool _failed;

        public RowDraft? Run()
        {
            var documentType = ReadDocumentType();
            var number = HrText.Clean(Cell(MedicalExamImportColumns.DocumentNumber));
            if (number is null)
            {
                Error(HrErrorCodes.MedicalExamImportRow.DocumentNumberRequired, "documentNumber", "Escribe el número de documento.");
            }

            MedicalExamType? type = null;
            if (MedicalExamLabels.TryParse(Cell(MedicalExamImportColumns.ExamType), out var parsedType))
            {
                type = parsedType;
            }
            else
            {
                Error(HrErrorCodes.MedicalExamImportRow.ExamTypeInvalid, "type", "Elige un tipo de examen de la lista.");
            }

            var examDate = ReadDate(MedicalExamImportColumns.ExamDate, required: true, "examDate", HrErrorCodes.MedicalExamImportRow.ExamDateInvalid);
            if (examDate > today)
            {
                Error(HrErrorCodes.MedicalExamImportRow.ExamDateInFuture, "examDate", "La fecha del examen no puede ser futura.");
            }

            var provider = Text(MedicalExamImportColumns.Provider, "provider", 200);
            var restrictions = Text(MedicalExamImportColumns.Restrictions, "restrictions", 2000);
            var recommendations = Text(MedicalExamImportColumns.Recommendations, "recommendations", 2000);
            var until = ReadDate(
                MedicalExamImportColumns.RestrictionsUntil, required: false, "restrictionsUntil", HrErrorCodes.MedicalExamImportRow.RestrictionsUntilInvalid);
            if (until is { } end && (restrictions is null || (examDate is { } date && end < date)))
            {
                Error(
                    HrErrorCodes.MedicalExamImportRow.RestrictionsUntilInvalid,
                    "restrictionsUntil",
                    "La fecha «restricciones hasta» sólo aplica si hay restricciones y no puede ser anterior a la del examen.");
            }

            return _failed || documentType is null || number is null || type is null || examDate is null
                ? null
                : new RowDraft(source.RowNumber, documentType.Value, number, type.Value, examDate.Value, provider, restrictions, recommendations, until);
        }

        private string? Cell(string header) => source.Cells.GetValueOrDefault(header);

        private void Error(string code, string field, string message)
        {
            _failed = true;
            sink.Add(new MedicalExamImportIssueDto(source.RowNumber, code, message, field));
        }

        // The label of the employee template («Cédula de ciudadanía»); the short name («CC») is accepted too.
        private HrDocumentType? ReadDocumentType()
        {
            var text = Cell(MedicalExamImportColumns.DocumentType);
            if (HrLabels.TryParse<HrDocumentType>(text, out var byLabel) || HrEnums.TryParse(text?.ToUpperInvariant(), out byLabel))
            {
                return byLabel;
            }

            Error(HrErrorCodes.MedicalExamImportRow.DocumentTypeInvalid, "documentType", "Elige un tipo de documento de la lista.");
            return null;
        }

        private string? Text(string header, string field, int max)
        {
            var text = HrText.Clean(Cell(header));
            if (text is not null && text.Length > max)
            {
                // The row is already failed: the text is returned as typed so that the rules that read it (the restrictions end date
                // needs restrictions) judge what the person wrote and not an invented gap. It never reaches an exam.
                Error(HrErrorCodes.MedicalExamImportRow.TextTooLong, field, $"Este campo no puede pasar de {max} caracteres.");
            }

            return text;
        }

        private DateOnly? ReadDate(string header, bool required, string field, string code)
        {
            var text = Cell(header);
            if (text is null)
            {
                if (required)
                {
                    Error(code, field, "Escribe la fecha, por ejemplo 2026-10-15 o 15/10/2026.");
                }

                return null;
            }

            if (DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return date;
            }

            Error(code, field, "La fecha no es válida: usa 2026-10-15 o 15/10/2026.");
            return null;
        }
    }
}
