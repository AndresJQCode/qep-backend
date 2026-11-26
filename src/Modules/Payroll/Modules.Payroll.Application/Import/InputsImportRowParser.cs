using System.Globalization;
using Modules.Payroll.Domain;

namespace Modules.Payroll.Application;

/// <param name="EmployeesByFoldedDocument">Todos los empleados con contrato en la quincena, por documento sin mayúsculas ni espacios de más.</param>
/// <param name="SourcesByFoldedDocument">Lo que Hr dijo de cada uno (el nombre de la fila sale de acá).</param>
public sealed record InputsImportContext(
    IReadOnlyDictionary<string, EligibilityResult> EmployeesByFoldedDocument,
    IReadOnlyDictionary<string, HrEmployeeSource> SourcesByFoldedDocument,
    ConceptCatalogSnapshot Concepts);

public sealed record ParsedInputRow(int RowNumber, Guid EmployeeId, PeriodInputDraft Draft, string FullName, string ConceptName);

/// <summary><see cref="Row"/> sólo viene cuando la fila no tiene ningún error.</summary>
public sealed record InputsImportRowResult(ParsedInputRow? Row, IReadOnlyList<InputsImportRowIssueDto> Errors);

/// <summary>
/// Las reglas de una fila de novedades: pura, sin base de datos, y junta <b>todos</b> los errores de la fila (en el orden documento,
/// concepto, horas, valor, clasificación). Una fila es una línea. El concepto se compara sin mayúsculas, tildes ni espacios de más
/// con los nombres no archivados de la empresa.
/// </summary>
public static class InputsImportRowParser
{
    public static InputsImportRowResult Parse(ExcelInputsRow row, InputsImportContext context)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(context);

        var errors = new List<InputsImportRowIssueDto>();

        void Fail(string code, string message, string field) => errors.Add(new InputsImportRowIssueDto(row.RowNumber, code, message, field));

        string? Cell(string column) => row.Cells.GetValueOrDefault(column);

        // 1. El empleado: existe en la quincena y se liquida.
        HrEmployeeSource? source = null;
        var document = PayrollText.Fold(Cell(InputsImportColumns.DocumentNumber));
        if (document is null)
        {
            Fail(PayrollErrorCodes.ImportRowDocumentRequired, "Escribe el número de documento.", "documentNumber");
        }
        else if (!context.EmployeesByFoldedDocument.TryGetValue(document, out var eligibility))
        {
            Fail(PayrollErrorCodes.ImportRowEmployeeNotInPeriod, "Ese documento no es de un empleado de esta quincena.", "documentNumber");
        }
        else if (eligibility.Status == EligibilityStatus.Excluded)
        {
            Fail(PayrollErrorCodes.ImportRowEmployeeNotLiquidable, "Ese empleado no se liquida en esta quincena.", "documentNumber");
        }
        else
        {
            source = context.SourcesByFoldedDocument[document];
        }

        // 2. El concepto.
        ResolvedConcept? concept = null;
        var conceptText = Cell(InputsImportColumns.Concept);
        if (PayrollText.Fold(conceptText) is null)
        {
            Fail(PayrollErrorCodes.ImportRowConceptRequired, "Escribe el concepto.", "concept");
        }
        else
        {
            concept = context.Concepts.FindActiveByName(conceptText);
            if (concept is null)
            {
                Fail(PayrollErrorCodes.ImportRowConceptNotFound, "No existe un concepto con ese nombre.", "concept");
            }
        }

        // 3. Horas, valor y clasificación según el tipo del concepto (sin concepto no hay qué juzgar).
        decimal? hours = null;
        decimal? amount = null;
        bool? constitutive = null;
        if (concept is not null)
        {
            var hoursText = Cell(InputsImportColumns.Hours);
            var amountText = Cell(InputsImportColumns.Amount);
            var constitutiveText = Cell(InputsImportColumns.SalaryConstitutive);

            if (concept.Kind == ConceptKind.Overtime)
            {
                if (hoursText is null)
                {
                    Fail(PayrollErrorCodes.ImportRowHoursRequired, "Escribe las horas.", "hours");
                }
                else if (TryParseNumber(hoursText, out var parsedHours) && InputLimits.IsValidHours(parsedHours))
                {
                    hours = parsedHours;
                }
                else
                {
                    Fail(PayrollErrorCodes.ImportRowHoursInvalid, "Las horas deben ser mayores que 0, hasta 360 y con máximo 2 decimales.", "hours");
                }

                if (amountText is not null)
                {
                    Fail(PayrollErrorCodes.ImportRowAmountNotAllowed, "Las horas extras no llevan valor: déjalo vacío.", "amount");
                }

                if (constitutiveText is not null)
                {
                    Fail(
                        PayrollErrorCodes.ImportRowSalaryConstitutiveNotAllowed,
                        "Las horas extras no llevan clasificación: déjala vacía.", "salaryConstitutive");
                }
            }
            else
            {
                if (hoursText is not null)
                {
                    Fail(PayrollErrorCodes.ImportRowHoursNotAllowed, "Sólo las horas extras llevan horas: déjalas vacías.", "hours");
                }

                if (amountText is null)
                {
                    Fail(PayrollErrorCodes.ImportRowAmountRequired, "Escribe el valor.", "amount");
                }
                else if (TryParseNumber(amountText, out var parsedAmount) && InputLimits.IsValidAmount(parsedAmount))
                {
                    amount = parsedAmount;
                }
                else
                {
                    Fail(PayrollErrorCodes.ImportRowAmountInvalid, "El valor debe ser mayor que 0 y con máximo 2 decimales.", "amount");
                }

                if (concept.Kind == ConceptKind.Deduction)
                {
                    if (constitutiveText is not null)
                    {
                        Fail(
                            PayrollErrorCodes.ImportRowSalaryConstitutiveNotAllowed,
                            "Las deducciones no llevan clasificación: déjala vacía.", "salaryConstitutive");
                    }
                }
                else if (constitutiveText is not null)
                {
                    if (YesNo(constitutiveText) is { } answer)
                    {
                        constitutive = answer;
                    }
                    else
                    {
                        Fail(PayrollErrorCodes.ImportRowYesNoInvalid, "Escribe Sí o No.", "salaryConstitutive");
                    }
                }
                else if (concept.DefaultSalaryConstitutive is { } suggestion)
                {
                    constitutive = suggestion;
                }
                else
                {
                    Fail(
                        PayrollErrorCodes.ImportRowSalaryConstitutiveRequired,
                        "Indica si el ingreso constituye salario (Sí o No): ese concepto no tiene una clasificación sugerida.",
                        "salaryConstitutive");
                }
            }
        }

        if (errors.Count > 0 || source is null || concept is null)
        {
            return new InputsImportRowResult(null, errors);
        }

        var draft = new PeriodInputDraft(concept.Id, concept.Kind, hours, amount, constitutive);
        return new InputsImportRowResult(
            new ParsedInputRow(row.RowNumber, source.EmployeeId, draft, LiquidationPlanner.FullNameOf(source), concept.Name),
            errors);
    }

    private static bool? YesNo(string text)
    {
        var folded = PayrollText.Fold(text);
        if (folded == PayrollText.Fold(PayrollLabels.Yes))
        {
            return true;
        }

        return folded == PayrollText.Fold(PayrollLabels.No) ? false : null;
    }

    // Excel devuelve texto invariante; una celda de texto escrita a mano puede traer coma decimal.
    private static bool TryParseNumber(string text, out decimal value)
    {
        var normalized = text.Contains('.', StringComparison.Ordinal) ? text : text.Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }
}
