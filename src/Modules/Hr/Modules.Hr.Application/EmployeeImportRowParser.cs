using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;
using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <summary>
/// Lo que el parser de una fila necesita y que se lee <b>una sola vez por request</b>: «hoy» del tenant, su país (ya resuelto con
/// <c>EmployeeCountry.Effective</c>), los catálogos armados y la tabla de valores legales entera (no un <c>FloorAtAsync</c> por fila).
/// </summary>
public sealed record EmployeeImportContext(
    DateOnly Today,
    string TenantCountry,
    HrCountryIndex Countries,
    HrCityIndex Cities,
    HrSettingsIndex Settings,
    IHrPhoneNormalizer Phones,
    IReadOnlyList<LegalParameterValue> LegalTable);

/// <summary>
/// Una fila válida ya resuelta: <c>Profile.Country</c> y <c>Terms.CompanyId</c> son los efectivos. <c>CompanyName</c> alimenta la
/// vista previa. Con <c>RetirementDate</c> el contrato queda terminado en esa fecha.
/// </summary>
public sealed record ParsedEmployeeRow(
    int RowNumber,
    EmployeeProfile Profile,
    PaymentDetails? Payment,
    ContractTerms Terms,
    string CompanyName,
    decimal Salary,
    bool Teleworker,
    bool BelowMinimum,
    PositionId PositionId,
    AreaId? AreaId,
    Workplace Workplace,
    TerminationReason? RetirementReason,
    DateOnly? RetirementDate);

/// <summary>Sólo hay <c>Row</c> sin errores; los avisos de una fila con error no se listan.</summary>
public sealed record EmployeeImportRowResult(
    ParsedEmployeeRow? Row,
    IReadOnlyList<EmployeeImportRowIssueDto> Errors,
    IReadOnlyList<EmployeeImportRowIssueDto> Warnings);

/// <summary>
/// Las reglas de una fila del Excel de empleados, puras y sin base de datos: las de <c>EmployeeRequestValidator</c>,
/// <c>PaymentRequestValidator</c>, <c>ContractTermsRequestValidator</c> y del dominio, con el código de fila del contrato. Junta
/// <b>todos</b> los errores de la fila. Los que dependen de otras filas o de la base (documento repetido o ya tomado) los pone
/// <see cref="EmployeeImportAnalyzer"/>.
/// </summary>
public static partial class EmployeeImportRowParser
{
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy"];

    [GeneratedRegex(@"^[0-9]{1,17}$")]
    private static partial Regex AccountNumber();

    public static EmployeeImportRowResult Parse(ExcelEmployeeRow row, EmployeeImportContext context) =>
        new Reader(row, context).Run();

    private sealed class Reader(ExcelEmployeeRow source, EmployeeImportContext context)
    {
        private readonly List<EmployeeImportRowIssueDto> _errors = [];
        private readonly List<EmployeeImportRowIssueDto> _warnings = [];

        public EmployeeImportRowResult Run()
        {
            var firstName = Name(HrImportColumns.FirstName, "firstName", HrErrorCodes.ImportRow.FirstNameRequired, "Escribe los nombres.", 100, "Los nombres");
            var lastName = Name(HrImportColumns.LastName, "lastName", HrErrorCodes.ImportRow.LastNameRequired, "Escribe los apellidos.", 100, "Los apellidos");
            var documentType = Label<HrDocumentType>(
                HrImportColumns.DocumentType, "documentType", HrErrorCodes.ImportRow.DocumentTypeInvalid, "Elige un tipo de documento de la lista.", required: true);
            var documentNumber = DocumentNumber();
            var exempt = YesNo(HrImportColumns.ForeignPensionExempt, "foreignPensionExempt");
            var country = Country(documentType, exempt);
            var birthDate = BirthDate();
            var phone = Phone();
            var personalEmail = Email(HrImportColumns.PersonalEmail, "personalEmail", HrErrorCodes.ImportRow.PersonalEmailInvalid);
            var workEmail = Email(HrImportColumns.WorkEmail, "workEmail", HrErrorCodes.ImportRow.WorkEmailInvalid);
            var address = Optional(HrImportColumns.Address, "address", 200, "La dirección");
            var cityId = City();
            var health = Optional(HrImportColumns.HealthProvider, "healthProvider", 120, "La EPS");
            var pensionFund = Optional(HrImportColumns.PensionFund, "pensionFund", 120, "El fondo de pensiones");
            var severanceFund = Optional(HrImportColumns.SeveranceFund, "severanceFund", 120, "El fondo de cesantías");
            var riskProvider = Optional(HrImportColumns.OccupationalRiskProvider, "occupationalRiskProvider", 120, "La ARL");
            var subtype = Label<ContributorSubtype>(
                HrImportColumns.ContributorSubtype, "contributorSubtype", HrErrorCodes.ImportRow.ContributorSubtypeInvalid,
                "Elige un subtipo de cotizante de la lista.", required: false);
            var restDays = RestDays();
            var payment = Payment();

            var company = Company();
            var workplace = company is null ? null : ResolveWorkplace(company);
            var contractType = Label<ContractType>(
                HrImportColumns.ContractType, "contractType", HrErrorCodes.ImportRow.ContractTypeInvalid, "Elige un tipo de contrato de la lista.", required: true);
            var duration = Label<ContractDuration>(
                HrImportColumns.Duration, "duration", HrErrorCodes.ImportRow.DurationInvalid, "Elige una duración de la lista.", required: true);
            var salaryType = Label<SalaryType>(
                HrImportColumns.SalaryType, "salaryType", HrErrorCodes.ImportRow.SalaryTypeInvalid, "Elige un tipo de salario de la lista.", required: true);
            if (salaryType == SalaryType.Integral && contractType is not null and not ContractType.Employee)
            {
                Error(HrErrorCodes.ImportRow.IntegralNotAllowed, "salaryType", "El salario integral es sólo para empleados.");
            }

            var startDate = Date(HrImportColumns.StartDate, "startDate", HrErrorCodes.ImportRow.StartDateInvalid, "Escribe la fecha de ingreso, por ejemplo 2026-01-15.", required: true);
            var endDate = EndDate(duration, startDate);
            var salary = Salary();
            var belowMinimum = Floors(startDate, salary, contractType, salaryType);
            var teleworker = YesNo(HrImportColumns.Teleworker, "teleworker");

            var positionText = Cell(HrImportColumns.Position);
            var positionId = positionText is null ? null : context.Settings.FindPosition(positionText);
            if (positionId is null)
            {
                Error(HrErrorCodes.ImportRow.PositionNotFound, "position", "Elige un cargo de la lista.");
            }

            AreaId? areaId = null;
            if (Cell(HrImportColumns.Area) is { } areaText)
            {
                areaId = context.Settings.FindArea(areaText);
                if (areaId is null)
                {
                    Error(HrErrorCodes.ImportRow.AreaNotFound, "area", "Elige un área de la lista o deja la celda vacía.");
                }
            }

            var (retirementDate, retirementReason) = Retirement(startDate);

            if (_errors.Count > 0)
            {
                return new EmployeeImportRowResult(null, Sorted(_errors), []);
            }

            var profile = new EmployeeProfile(
                firstName!, lastName!, documentType!.Value, documentNumber!, country!, birthDate, phone, personalEmail, workEmail, address,
                cityId, null, health, pensionFund, severanceFund, riskProvider,
                subtype ?? ContributorSubtype.Standard, exempt ?? false, restDays!);
            var terms = new ContractTerms(
                company!.Id, contractType!.Value, duration!.Value, salaryType!.Value, startDate!.Value, endDate);
            var parsed = new ParsedEmployeeRow(
                source.RowNumber, profile, payment, terms, company.Name, salary!.Value, teleworker ?? false, belowMinimum,
                positionId!.Value, areaId, workplace!, retirementReason, retirementDate);
            return new EmployeeImportRowResult(parsed, [], _warnings);
        }

        private static List<EmployeeImportRowIssueDto> Sorted(List<EmployeeImportRowIssueDto> issues) =>
            issues.OrderBy(issue => issue.Field, StringComparer.Ordinal).ToList();

        private void Error(string code, string? field, string message) =>
            _errors.Add(new EmployeeImportRowIssueDto(source.RowNumber, code, message, field));

        private string? Cell(string header) =>
            source.Cells.TryGetValue(header, out var raw) && !string.IsNullOrWhiteSpace(raw) ? raw.Trim() : null;

        private string? Name(string header, string field, string requiredCode, string requiredMessage, int max, string label)
        {
            var text = Cell(header);
            if (text is null)
            {
                Error(requiredCode, field, requiredMessage);
                return null;
            }

            return TooLong(text, field, max, label) ? null : text;
        }

        private string? Optional(string header, string field, int max, string label)
        {
            var text = Cell(header);
            return text is null || TooLong(text, field, max, label) ? null : text;
        }

        private bool TooLong(string text, string field, int max, string label)
        {
            if (text.Length <= max)
            {
                return false;
            }

            Error(HrErrorCodes.ImportRow.TextTooLong, field, $"{label} no puede pasar de {max} caracteres.");
            return true;
        }

        /// <summary>La etiqueta del enum (o el nombre del miembro): vacía es «sin valor» salvo que sea requerida.</summary>
        private TEnum? Label<TEnum>(string header, string field, string code, string message, bool required)
            where TEnum : struct, Enum
        {
            var text = Cell(header);
            if (text is null)
            {
                if (required)
                {
                    Error(code, field, message);
                }

                return null;
            }

            if (HrLabels.TryParse<TEnum>(text, out var value))
            {
                return value;
            }

            Error(code, field, message);
            return null;
        }

        private bool? YesNo(string header, string field)
        {
            var text = Cell(header);
            if (text is null)
            {
                return false;
            }

            switch (HrText.Fold(text))
            {
                case "si":
                    return true;
                case "no":
                    return false;
                default:
                    Error(HrErrorCodes.ImportRow.YesNoInvalid, field, "Escribe Sí o No.");
                    return null;
            }
        }

        private string? DocumentNumber()
        {
            var text = Cell(HrImportColumns.DocumentNumber);
            if (text is null)
            {
                Error(HrErrorCodes.ImportRow.DocumentNumberRequired, "documentNumber", "Escribe el número de documento.");
                return null;
            }

            if (text.Any(char.IsWhiteSpace))
            {
                Error(HrErrorCodes.ImportRow.DocumentNumberRequired, "documentNumber", "El número de documento no puede tener espacios.");
                return null;
            }

            return TooLong(text, "documentNumber", 30, "El número de documento") ? null : text;
        }

        // Vacía es el país del tenant; si no, el catálogo de Geografía por código o nombre. El cruce con el documento y con el exento
        // de pensión es la misma tabla del alta (EmployeeCountry), sólo cuando lo que se cruza es válido.
        private string? Country(HrDocumentType? documentType, bool? exempt)
        {
            var text = Cell(HrImportColumns.Country);
            string? code;
            if (text is null)
            {
                code = context.TenantCountry;
            }
            else
            {
                code = context.Countries.FindByCodeOrName(text)?.Code;
                if (code is null)
                {
                    Error(HrErrorCodes.ImportRow.CountryInvalid, "country", "Elige un país de la lista.");
                    return null;
                }
            }

            if (documentType is { } type && EmployeeCountry.DocumentMismatch(code, type) is { } mismatch)
            {
                Error(HrErrorCodes.ImportRow.CountryDocumentMismatch, "country", mismatch);
            }

            if (exempt is { } flag && EmployeeCountry.PensionExemptionNotAllowed(code, flag) is { } notAllowed)
            {
                Error(HrErrorCodes.ImportRow.ForeignPensionExemptNotAllowed, "foreignPensionExempt", notAllowed);
            }

            return code;
        }

        private DateOnly? Date(string header, string field, string code, string message, bool required)
        {
            var text = Cell(header);
            if (text is null)
            {
                if (required)
                {
                    Error(code, field, message);
                }

                return null;
            }

            if (TryParseDate(text) is { } date)
            {
                return date;
            }

            Error(code, field, message);
            return null;
        }

        private static DateOnly? TryParseDate(string text) =>
            DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

        private DateOnly? BirthDate()
        {
            var date = Date(
                HrImportColumns.BirthDate, "birthDate", HrErrorCodes.ImportRow.BirthDateInvalid,
                "Escribe la fecha de nacimiento, por ejemplo 1990-05-20.", required: false);
            if (date is { } value && value >= context.Today)
            {
                Error(HrErrorCodes.ImportRow.BirthDateInvalid, "birthDate", "La fecha de nacimiento tiene que ser anterior a hoy.");
                return null;
            }

            return date;
        }

        private string? Phone()
        {
            var text = Cell(HrImportColumns.Phone);
            if (text is null)
            {
                return null;
            }

            var e164 = context.Phones.ToE164(text);
            if (e164 is null)
            {
                Error(HrErrorCodes.ImportRow.PhoneInvalid, "phone", "El celular no es válido; escríbelo con 10 dígitos, por ejemplo 3001234567.");
            }

            return e164;
        }

        private string? Email(string header, string field, string code)
        {
            var text = Cell(header);
            if (text is null)
            {
                return null;
            }

            if (text.Length > 254 || !new EmailAddressAttribute().IsValid(text))
            {
                Error(code, field, "El correo no es válido.");
                return null;
            }

            return text;
        }

        private Guid? City()
        {
            var text = Cell(HrImportColumns.City);
            if (text is null)
            {
                return null;
            }

            var match = context.Cities.Match(text);
            switch (match.Outcome)
            {
                case HrCityOutcome.Found:
                    return match.CityId;
                case HrCityOutcome.Ambiguous:
                    Error(HrErrorCodes.ImportRow.CityAmbiguous, "city", "Hay varias ciudades con ese nombre; escríbela como «ciudad - departamento».");
                    return null;
                default:
                    Error(HrErrorCodes.ImportRow.CityNotFound, "city", "No encontramos esa ciudad; revisa el nombre o escríbela como «ciudad - departamento».");
                    return null;
            }
        }

        private List<DayOfWeek>? RestDays()
        {
            var text = Cell(HrImportColumns.RestDays);
            if (text is null)
            {
                return [DayOfWeek.Sunday];
            }

            var days = new List<DayOfWeek>();
            foreach (var token in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!HrLabels.TryParse<DayOfWeek>(token, out var day))
                {
                    days.Clear();
                    break;
                }

                days.Add(day);
            }

            if (days.Count is < 1 or > 6 || days.Distinct().Count() != days.Count)
            {
                Error(HrErrorCodes.ImportRow.RestDaysInvalid, "restDays", "Escribe de uno a seis días de descanso distintos, separados por coma.");
                return null;
            }

            // Lunes a domingo, como el alta.
            return days.OrderBy(day => ((int)day + 6) % 7).ToList();
        }

        private PaymentDetails? Payment()
        {
            var methodText = Cell(HrImportColumns.PaymentMethod);
            var bank = Cell(HrImportColumns.BankName);
            var typeText = Cell(HrImportColumns.BankAccountType);
            var numberText = Cell(HrImportColumns.BankAccountNumber);
            var anyBankData = bank is not null || typeText is not null || numberText is not null;

            PaymentMethod method;
            if (methodText is null)
            {
                if (anyBankData)
                {
                    // Sin medio de pago los datos del banco no significan nada: el contrato no infiere uno.
                    Error(HrErrorCodes.ImportRow.PaymentMethodInvalid, "paymentMethod", "Elige el medio de pago: hay datos del banco sin medio de pago.");
                }

                return null;
            }
            else if (!HrLabels.TryParse(methodText, out method))
            {
                Error(HrErrorCodes.ImportRow.PaymentMethodInvalid, "paymentMethod", "Elige un medio de pago de la lista.");
                return null;
            }

            if (method == PaymentMethod.Other)
            {
                if (anyBankData)
                {
                    Error(HrErrorCodes.ImportRow.BankNotAllowed, "bankName", "Con «Otro» no se escriben banco, tipo ni número de cuenta.");
                    return null;
                }

                return new PaymentDetails(PaymentMethod.Other, null, null, null);
            }

            string? bankName = null;
            if (bank is null)
            {
                Error(HrErrorCodes.ImportRow.BankIncomplete, "bankName", "Escribe el banco.");
            }
            else if (!TooLong(bank, "bankName", 80, "El banco"))
            {
                bankName = bank;
            }

            BankAccountType? accountType = null;
            if (typeText is not null && HrLabels.TryParse<BankAccountType>(typeText, out var parsedType))
            {
                accountType = parsedType;
            }
            else
            {
                Error(HrErrorCodes.ImportRow.BankAccountTypeInvalid, "bankAccountType", "Elige el tipo de cuenta: Ahorros o Corriente.");
            }

            string? number = null;
            if (numberText is not null && AccountNumber().IsMatch(numberText))
            {
                number = numberText;
            }
            else
            {
                Error(HrErrorCodes.ImportRow.BankAccountNumberInvalid, "bankAccountNumber", "El número de cuenta tiene de 1 a 17 dígitos.");
            }

            return bankName is null || accountType is null || number is null
                ? null
                : new PaymentDetails(PaymentMethod.BankTransfer, bankName, accountType, number);
        }

        private HrCompanyRef? Company()
        {
            var text = Cell(HrImportColumns.Company);
            if (text is null)
            {
                var single = context.Settings.SingleCompany;
                if (single is null)
                {
                    Error(HrErrorCodes.ImportRow.CompanyRequired, "company", "Elige la empresa: hay más de una activa.");
                }

                return single;
            }

            var company = context.Settings.FindCompany(text);
            if (company is null)
            {
                Error(HrErrorCodes.ImportRow.CompanyNotFound, "company", "Elige una empresa activa de la lista, con el formato «nombre - NIT».");
            }

            return company;
        }

        private Workplace? ResolveWorkplace(HrCompanyRef company)
        {
            var match = context.Settings.FindWorkplace(Cell(HrImportColumns.Workplace) ?? string.Empty, company.Id);
            switch (match.Outcome)
            {
                case HrWorkplaceOutcome.Found:
                    return match.Workplace;
                case HrWorkplaceOutcome.OtherCompany:
                    Error(HrErrorCodes.ImportRow.WorkplaceOtherCompany, "workplace", "El centro de trabajo es de otra empresa; elige uno de la empresa del contrato.");
                    return null;
                default:
                    Error(HrErrorCodes.ImportRow.WorkplaceNotFound, "workplace", "Elige un centro de trabajo de la lista, con el formato «centro - sede».");
                    return null;
            }
        }

        private DateOnly? EndDate(ContractDuration? duration, DateOnly? startDate)
        {
            var text = Cell(HrImportColumns.EndDate);
            DateOnly? end = null;
            var readable = true;
            if (text is not null)
            {
                end = TryParseDate(text);
                if (end is null)
                {
                    readable = false;
                    Error(HrErrorCodes.ImportRow.EndDateInvalid, "endDate", "Escribe la fecha de fin, por ejemplo 2026-12-31.");
                }
            }

            if (duration is ContractDuration.FixedTerm or ContractDuration.WorkOrLabor && text is null)
            {
                Error(HrErrorCodes.ImportRow.EndDateRequired, "endDate", "Escribe la fecha de fin del contrato.");
            }
            else if (duration == ContractDuration.Indefinite && readable && end is not null)
            {
                Error(HrErrorCodes.ImportRow.EndDateForbidden, "endDate", "Un contrato a término indefinido no tiene fecha de fin.");
            }

            if (end is { } value && startDate is { } start)
            {
                if (value < start)
                {
                    Error(HrErrorCodes.ImportRow.EndDateBeforeStart, "endDate", "La fecha de fin no puede ser anterior a la de ingreso.");
                }
                else if (duration == ContractDuration.FixedTerm && value > FixedTermLimits.MaxEndDate(start))
                {
                    Error(HrErrorCodes.ImportRow.FixedTermCapExceeded, "endDate", "Un contrato a término fijo no puede durar más de cuatro años.");
                }
            }

            return end;
        }

        private decimal? Salary()
        {
            var text = Cell(HrImportColumns.Salary);
            if (text is not null
                && decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
                && amount > 0
                && decimal.Round(amount, 2) == amount
                && amount < HrRequestRules.MaxMoney)
            {
                return amount;
            }

            Error(HrErrorCodes.ImportRow.SalaryInvalid, "salary", "El salario debe ser un número mayor que cero, con máximo dos decimales.");
            return null;
        }

        // Los pisos son los de la fecha de ingreso, de la tabla leída una vez. Integral bajo su piso es error; bajo el SMMLV en un tipo
        // no exento es un aviso (la fila vale y la confirmación la guarda con belowMinimumAcknowledged).
        private bool Floors(DateOnly? startDate, decimal? salary, ContractType? contractType, SalaryType? salaryType)
        {
            if (startDate is not { } start)
            {
                return false;
            }

            var smmlv = LegalParameters.Resolve(context.LegalTable, LegalParameterKey.Smmlv, start);
            var integralFloor = LegalParameters.Resolve(context.LegalTable, LegalParameterKey.IntegralSalaryFloor, start);
            if (smmlv is null || integralFloor is null)
            {
                Error(HrErrorCodes.ImportRow.LegalParameterMissing, "startDate", "No hay valores legales cargados para la fecha de ingreso.");
                return false;
            }

            if (salary is not { } amount)
            {
                return false;
            }

            if (salaryType == SalaryType.Integral && amount < integralFloor)
            {
                Error(HrErrorCodes.ImportRow.BelowIntegralFloor, "salary", "El salario integral no puede estar por debajo del piso legal de la fecha de ingreso.");
                return false;
            }

            if (contractType is { } type && amount < smmlv && !SalaryFloorPolicy.BelowMinimumAllowed.Contains(type))
            {
                _warnings.Add(new EmployeeImportRowIssueDto(
                    source.RowNumber,
                    HrErrorCodes.ImportRow.BelowMinimum,
                    "El salario está por debajo del mínimo legal de la fecha de ingreso; al confirmar se guarda como confirmado.",
                    "salary"));
                return true;
            }

            return false;
        }

        private (DateOnly? Date, TerminationReason? Reason) Retirement(DateOnly? startDate)
        {
            var dateText = Cell(HrImportColumns.RetirementDate);
            var reasonText = Cell(HrImportColumns.RetirementReason);
            if (dateText is null && reasonText is null)
            {
                return (null, null);
            }

            DateOnly? date = null;
            var dateIsFine = false;
            if (dateText is null)
            {
                Error(HrErrorCodes.ImportRow.RetirementDateInvalid, "retirementDate", "Escribe la fecha de retiro o quita el motivo.");
            }
            else if (TryParseDate(dateText) is not { } parsed)
            {
                Error(HrErrorCodes.ImportRow.RetirementDateInvalid, "retirementDate", "Escribe la fecha de retiro, por ejemplo 2026-10-31.");
            }
            else if (parsed > context.Today)
            {
                Error(HrErrorCodes.ImportRow.RetirementDateInFuture, "retirementDate", "La fecha de retiro no puede ser futura; el fin previsto va en la fecha de fin.");
            }
            else if (startDate is { } start && parsed < start)
            {
                Error(HrErrorCodes.ImportRow.RetirementBeforeStart, "retirementDate", "La fecha de retiro no puede ser anterior a la de ingreso.");
            }
            else
            {
                date = parsed;
                dateIsFine = true;
            }

            TerminationReason? reason = null;
            if (reasonText is null)
            {
                if (dateIsFine)
                {
                    Error(HrErrorCodes.ImportRow.RetirementReasonRequired, "retirementReason", "Elige el motivo del retiro.");
                }
            }
            else if (HrLabels.TryParse<TerminationReason>(reasonText, out var parsedReason))
            {
                reason = parsedReason;
            }
            else
            {
                Error(HrErrorCodes.ImportRow.RetirementReasonInvalid, "retirementReason", "Elige un motivo de retiro de la lista.");
            }

            return (date, reason);
        }
    }
}
