using Modules.Hr.Application;
using Modules.Hr.Domain;

namespace Modules.Hr.UnitTests;

/// <summary>
/// La fila del Excel de empleados: puro, sin base ni HTTP. Hoy es 2026-11-20 y la tabla legal es la sembrada
/// (<see cref="HrTestData.LegalTable"/>). Cada prueba parte de una fila válida mínima y cambia lo suyo.
/// </summary>
public sealed class EmployeeImportRowParserTests
{
    private static readonly HrCompanyRef Qep = new(Guid.Parse("01900000-0000-7000-8000-0000000000c1"), "Comercializadora QEP S.A.S.", "900123456", true);
    private static readonly HrCompanyRef North = new(Guid.Parse("01900000-0000-7000-8000-0000000000c2"), "Servicios del Norte S.A.S.", "800555111", true);
    private static readonly Guid Antioquia = Guid.Parse("01900000-0000-7000-8000-00000000a001");
    private static readonly Guid Santander = Guid.Parse("01900000-0000-7000-8000-00000000a002");
    private static readonly Guid Medellin = Guid.Parse("01900000-0000-7000-8000-00000000b001");

    private sealed class FakePhones : IHrPhoneNormalizer
    {
        public string? ToE164(string phone) => phone == "3001234567" ? "+573001234567" : null;
    }

    private static HrCountryIndex Countries() => new(
    [
        new HrCountryRef("CO", "Colombia"),
        new HrCountryRef("VE", "Venezuela"),
        new HrCountryRef("PE", "Perú"),
        new HrCountryRef("MX", "México"),
    ]);

    private static HrCityIndex Cities() => new(
    [
        new HrCityRef(Medellin, "Medellín", "Medellín", Antioquia, "Antioquia"),
        new HrCityRef(Guid.NewGuid(), "Barbosa", "Barbosa", Antioquia, "Antioquia"),
        new HrCityRef(Guid.NewGuid(), "Barbosa", "Barbosa", Santander, "Santander"),
    ]);

    private static EmployeeImportContext Context(bool twoCompanies = false, string tenantCountry = "CO")
    {
        var tenant = HrTestData.TenantId;
        var now = HrTestData.Now;
        var main = Site.Create(SiteId.New(), tenant, Qep.Id, "Principal", null, null, null, now);
        var northSite = Site.Create(SiteId.New(), tenant, North.Id, "Norte", null, null, null, now);
        var snapshot = new HrSettingsSnapshot(
            twoCompanies ? [main, northSite] : [main],
            twoCompanies
                ? [Workplace.Create(WorkplaceId.New(), tenant, main, "Bodega", null, ArlRiskClass.III, false, now),
                   Workplace.Create(WorkplaceId.New(), tenant, northSite, "Planta", null, ArlRiskClass.III, false, now)]
                : [Workplace.Create(WorkplaceId.New(), tenant, main, "Bodega", null, ArlRiskClass.III, false, now)],
            [Area.Create(AreaId.New(), tenant, "Operaciones", now)],
            [Position.Create(PositionId.New(), tenant, "Analista", now)],
            new HashSet<Guid>());
        return new EmployeeImportContext(
            HrTestData.Today,
            tenantCountry,
            Countries(),
            Cities(),
            new HrSettingsIndex(snapshot, twoCompanies ? [Qep, North] : [Qep]),
            new FakePhones(),
            HrTestData.LegalTable);
    }

    /// <summary>Una fila válida: un empleado indefinido, mensual, 2 500 000, que ingresó el 2026-01-15, sin retiro ni pago.</summary>
    private static ExcelEmployeeRow Row(params (string Header, string? Value)[] overrides)
    {
        var cells = HrImportColumns.Ordered.ToDictionary(header => header, _ => (string?)null);
        cells["NOMBRES"] = "Ana";
        cells["APELLIDOS"] = "Pérez";
        cells["TIPO DE DOCUMENTO"] = "Cédula de ciudadanía";
        cells["NÚMERO DE DOCUMENTO"] = "1020304050";
        cells["TIPO DE CONTRATO"] = "Empleado";
        cells["DURACIÓN"] = "Término indefinido";
        cells["TIPO DE SALARIO"] = "Mensual";
        cells["FECHA DE INGRESO"] = "2026-01-15";
        cells["SALARIO"] = "2500000";
        cells["CARGO"] = "Analista";
        cells["CENTRO DE TRABAJO"] = "Bodega - Principal";
        foreach (var (header, value) in overrides)
        {
            cells[header] = string.IsNullOrEmpty(value) ? null : value;
        }

        return new ExcelEmployeeRow(2, cells);
    }

    private static EmployeeImportRowResult Parse(ExcelEmployeeRow row, EmployeeImportContext? context = null) =>
        EmployeeImportRowParser.Parse(row, context ?? Context());

    /// <summary>«ENCABEZADO=valor;OTRO=valor». <c>{long}</c> es un texto de 300 letras; <c>#companies=2</c> arma el contexto con dos empresas.</summary>
    private static (ExcelEmployeeRow Row, EmployeeImportContext Context) FromSpec(string spec)
    {
        var overrides = new List<(string, string?)>();
        var twoCompanies = false;
        foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var header = part[..separator];
            var value = part[(separator + 1)..].Replace("{long}", new string('a', 300), StringComparison.Ordinal);
            if (header == "#companies")
            {
                twoCompanies = value == "2";
                continue;
            }

            overrides.Add((header, value));
        }

        return (Row([.. overrides]), Context(twoCompanies));
    }

    [Theory]
    [InlineData("first_name_required", "firstName", "NOMBRES=")]
    [InlineData("last_name_required", "lastName", "APELLIDOS=")]
    [InlineData("document_type_invalid", "documentType", "TIPO DE DOCUMENTO=Libreta")]
    [InlineData("document_number_required", "documentNumber", "NÚMERO DE DOCUMENTO=")]
    [InlineData("document_number_not_numeric", "documentNumber", "NÚMERO DE DOCUMENTO=ABC")]
    [InlineData("country_invalid", "country", "PAÍS=Alemania")]
    [InlineData("country_document_mismatch", "country", "PAÍS=Venezuela")]
    [InlineData("birth_date_invalid", "birthDate", "FECHA DE NACIMIENTO=mañana")]
    [InlineData("birth_date_invalid", "birthDate", "FECHA DE NACIMIENTO=2026-11-20")]
    [InlineData("phone_invalid", "phone", "CELULAR=123")]
    [InlineData("personal_email_invalid", "personalEmail", "CORREO PERSONAL=no-es-un-correo")]
    [InlineData("work_email_invalid", "workEmail", "CORREO CORPORATIVO=otro")]
    [InlineData("city_not_found", "city", "CIUDAD=Atlantis")]
    [InlineData("city_ambiguous", "city", "CIUDAD=Barbosa")]
    [InlineData("contributor_subtype_invalid", "contributorSubtype", "SUBTIPO DE COTIZANTE=Raro")]
    [InlineData("yes_no_invalid", "foreignPensionExempt", "EXENTO DE PENSIÓN=quizás")]
    [InlineData("yes_no_invalid", "teleworker", "TELETRABAJADOR=quizás")]
    [InlineData("foreign_pension_exempt_not_allowed", "foreignPensionExempt", "EXENTO DE PENSIÓN=Sí")]
    [InlineData("rest_days_invalid", "restDays", "DÍAS DE DESCANSO=funday")]
    [InlineData("payment_method_invalid", "paymentMethod", "MEDIO DE PAGO=Cheque")]
    [InlineData("payment_method_invalid", "paymentMethod", "BANCO=Bancolombia")]
    [InlineData("payment_method_invalid", "paymentMethod", "NÚMERO DE CUENTA=123")]
    [InlineData("duration_invalid", "duration", "DURACIÓN=FixedTerm")]
    [InlineData("document_type_invalid", "documentType", "TIPO DE DOCUMENTO=CC")]
    [InlineData("bank_incomplete", "bankName", "MEDIO DE PAGO=Transferencia bancaria")]
    [InlineData("bank_not_allowed", "bankName", "MEDIO DE PAGO=Otro;BANCO=Bancolombia")]
    [InlineData("bank_account_type_invalid", "bankAccountType", "MEDIO DE PAGO=Transferencia bancaria;BANCO=Bancolombia;TIPO DE CUENTA=Fiducia;NÚMERO DE CUENTA=123")]
    [InlineData("bank_account_number_invalid", "bankAccountNumber", "MEDIO DE PAGO=Transferencia bancaria;BANCO=Bancolombia;TIPO DE CUENTA=Ahorros;NÚMERO DE CUENTA=abc")]
    [InlineData("company_required", "company", "#companies=2")]
    [InlineData("company_not_found", "company", "EMPRESA=Otra empresa - 1")]
    [InlineData("contract_type_invalid", "contractType", "TIPO DE CONTRATO=Voluntario")]
    [InlineData("duration_invalid", "duration", "DURACIÓN=Eterna")]
    [InlineData("salary_type_invalid", "salaryType", "TIPO DE SALARIO=Semanal")]
    [InlineData("integral_not_allowed", "salaryType", "TIPO DE CONTRATO=Contratista;TIPO DE SALARIO=Salario integral")]
    [InlineData("start_date_invalid", "startDate", "FECHA DE INGRESO=ayer")]
    [InlineData("end_date_invalid", "endDate", "DURACIÓN=Término fijo;FECHA DE FIN=pronto")]
    [InlineData("end_date_required", "endDate", "DURACIÓN=Término fijo")]
    [InlineData("end_date_forbidden", "endDate", "FECHA DE FIN=2027-01-14")]
    [InlineData("end_date_before_start", "endDate", "DURACIÓN=Término fijo;FECHA DE FIN=2025-12-31")]
    [InlineData("fixed_term_cap_exceeded", "endDate", "DURACIÓN=Término fijo;FECHA DE FIN=2030-01-15")]
    [InlineData("salary_invalid", "salary", "SALARIO=abc")]
    [InlineData("salary_invalid", "salary", "SALARIO=0")]
    [InlineData("salary_invalid", "salary", "SALARIO=1000.123")]
    [InlineData("below_integral_floor", "salary", "TIPO DE SALARIO=Salario integral;SALARIO=20000000")]
    [InlineData("legal_parameter_missing", "startDate", "FECHA DE INGRESO=2020-01-01")]
    [InlineData("position_not_found", "position", "CARGO=Fantasma")]
    [InlineData("position_not_found", "position", "CARGO=")]
    [InlineData("area_not_found", "area", "ÁREA=Fantasma")]
    [InlineData("workplace_not_found", "workplace", "CENTRO DE TRABAJO=Fantasma")]
    [InlineData("workplace_not_found", "workplace", "CENTRO DE TRABAJO=")]
    [InlineData("workplace_not_found", "workplace", "CENTRO DE TRABAJO=Bodega")]
    [InlineData("retirement_date_invalid", "retirementDate", "FECHA DE RETIRO=nunca")]
    [InlineData("retirement_date_in_future", "retirementDate", "FECHA DE RETIRO=2026-12-01;MOTIVO DE RETIRO=Renuncia")]
    [InlineData("retirement_before_start", "retirementDate", "FECHA DE RETIRO=2026-01-01;MOTIVO DE RETIRO=Renuncia")]
    [InlineData("retirement_reason_required", "retirementReason", "FECHA DE RETIRO=2026-10-31")]
    [InlineData("retirement_reason_invalid", "retirementReason", "FECHA DE RETIRO=2026-10-31;MOTIVO DE RETIRO=Aburrimiento")]
    [InlineData("text_too_long", "firstName", "NOMBRES={long}")]
    [InlineData("text_too_long", "address", "DIRECCIÓN={long}")]
    [InlineData("text_too_long", "bankName", "MEDIO DE PAGO=Transferencia bancaria;BANCO={long};TIPO DE CUENTA=Ahorros;NÚMERO DE CUENTA=123")]
    public void EachRuleReportsItsCodeAndField(string code, string field, string spec)
    {
        var (row, context) = FromSpec(spec);

        var result = Parse(row, context);

        var issue = Assert.Single(result.Errors, error => error.Code == $"hr.import.row.{code}" && error.Field == field);
        Assert.Equal(2, issue.RowNumber);
        Assert.False(string.IsNullOrWhiteSpace(issue.Message));
        Assert.Null(result.Row);
    }

    [Fact]
    public void AValidRowHasNoIssues()
    {
        var result = Parse(Row());

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        Assert.NotNull(result.Row);
        Assert.Equal(new DateOnly(2026, 1, 15), result.Row.Terms.StartDate);
        Assert.Equal(ContractDuration.Indefinite, result.Row.Terms.Duration);
        Assert.Equal(2_500_000m, result.Row.Salary);
        Assert.False(result.Row.Teleworker);
        Assert.Null(result.Row.Payment);
        Assert.Null(result.Row.RetirementDate);
        Assert.Equal(ContributorSubtype.Standard, result.Row.Profile.ContributorSubtype);
        Assert.Equal("CO", result.Row.Profile.Country);
    }

    [Fact]
    public void ALetteredNumberForANonNumericTypeHasNoIssue()
    {
        var result = Parse(Row(("TIPO DE DOCUMENTO", "Pasaporte"), ("NÚMERO DE DOCUMENTO", "AB123"), ("PAÍS", "Venezuela")));

        Assert.DoesNotContain(result.Errors, error => error.Field == "documentNumber");
    }

    [Fact]
    public void TheNotNumericCodeIsTheHrOne() =>
        Assert.Equal("hr.import.row.document_number_not_numeric", HrErrorCodes.ImportRow.DocumentNumberNotNumeric);

    [Fact]
    public void ARowCanCarrySeveralErrors()
    {
        var result = Parse(Row(("NOMBRES", ""), ("SALARIO", "abc"), ("CARGO", "Fantasma"), ("CIUDAD", "Atlantis")));

        Assert.Null(result.Row);
        Assert.Equal(
            ["city", "firstName", "position", "salary"],
            result.Errors.Select(error => error.Field).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void LabelsAreComparedWithoutAccents()
    {
        var result = Parse(Row(
            ("TIPO DE DOCUMENTO", "cedula de ciudadania"),
            ("TIPO DE CONTRATO", "aprendiz sena lectiva"),
            ("DURACIÓN", "termino fijo"),
            ("FECHA DE FIN", "2026-12-31"),
            ("MEDIO DE PAGO", "transferencia bancaria"),
            ("BANCO", "Bancolombia"),
            ("TIPO DE CUENTA", "AHORROS"),
            ("NÚMERO DE CUENTA", "123456")));

        Assert.Empty(result.Errors);
        var row = Assert.IsType<ParsedEmployeeRow>(result.Row);
        Assert.Equal(HrDocumentType.CC, row.Profile.DocumentType);
        Assert.Equal(ContractType.SenaApprenticeTeaching, row.Terms.ContractType);
        Assert.Equal(ContractDuration.FixedTerm, row.Terms.Duration);
        Assert.Equal(new DateOnly(2026, 12, 31), row.Terms.EndDate);
        Assert.Equal(PaymentMethod.BankTransfer, row.Payment!.Method);
        Assert.Equal(BankAccountType.Savings, row.Payment.BankAccountType);
    }

    [Fact]
    public void RestDaysAreACommaList()
    {
        var weekend = Parse(Row(("DÍAS DE DESCANSO", "sábado, domingo")));
        var none = Parse(Row());

        Assert.Equal([DayOfWeek.Saturday, DayOfWeek.Sunday], weekend.Row!.Profile.RestDays);
        Assert.Equal([DayOfWeek.Sunday], none.Row!.Profile.RestDays);
    }

    [Fact]
    public void DatesAcceptTheThreeFormats()
    {
        var iso = Parse(Row(("FECHA DE INGRESO", "2026-02-01")));
        var padded = Parse(Row(("FECHA DE INGRESO", "01/02/2026")));
        var short_ = Parse(Row(("FECHA DE INGRESO", "1/2/2026")));

        Assert.All([iso, padded, short_], result => Assert.Equal(new DateOnly(2026, 2, 1), result.Row!.Terms.StartDate));
    }

    [Fact]
    public void ABelowMinimumEmployeeIsAWarningNotAnError()
    {
        var result = Parse(Row(("SALARIO", "1500000"), ("FECHA DE INGRESO", "2026-02-01")));

        Assert.Empty(result.Errors);
        Assert.NotNull(result.Row);
        Assert.True(result.Row.BelowMinimum);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("hr.import.row.below_minimum", warning.Code);
        Assert.Equal("salary", warning.Field);
    }

    [Fact]
    public void AContractorBelowTheMinimumHasNoWarning()
    {
        var result = Parse(Row(("TIPO DE CONTRATO", "Contratista"), ("SALARIO", "1500000"), ("FECHA DE INGRESO", "2026-02-01")));

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        Assert.False(result.Row!.BelowMinimum);
    }

    [Fact]
    public void TheFloorIsTheOneOfTheStartDate()
    {
        var result = Parse(Row(("SALARIO", "1500000"), ("FECHA DE INGRESO", "2025-03-01")));

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        Assert.False(result.Row!.BelowMinimum);
    }

    [Fact]
    public void APastRetirementTerminates()
    {
        var result = Parse(Row(("FECHA DE RETIRO", "2026-10-31"), ("MOTIVO DE RETIRO", "Renuncia")));

        Assert.Empty(result.Errors);
        Assert.Equal(new DateOnly(2026, 10, 31), result.Row!.RetirementDate);
        Assert.Equal(TerminationReason.Resignation, result.Row.RetirementReason);
    }

    [Fact]
    public void ARetirementWithoutReasonIsAnError()
    {
        var result = Parse(Row(("FECHA DE RETIRO", "2026-10-31")));

        Assert.Null(result.Row);
        Assert.Equal("hr.import.row.retirement_reason_required", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void AFutureRetirementIsAnError()
    {
        var result = Parse(Row(("FECHA DE RETIRO", "2026-11-21"), ("MOTIVO DE RETIRO", "Renuncia")));

        Assert.Null(result.Row);
        Assert.Equal("hr.import.row.retirement_date_in_future", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void ThePhoneIsStoredAsE164()
    {
        var result = Parse(Row(("CELULAR", "3001234567")));

        Assert.Equal("+573001234567", result.Row!.Profile.Phone);
    }

    [Fact]
    public void AnEmptyCountryTakesTheTenants()
    {
        var result = Parse(Row(("TIPO DE DOCUMENTO", "Permiso por protección temporal")), Context(tenantCountry: "MX"));

        Assert.Empty(result.Errors);
        Assert.Equal("MX", result.Row!.Profile.Country);
    }

    [Theory]
    [InlineData("ve", "VE")]
    [InlineData("Venezuela", "VE")]
    [InlineData("PERÚ", "PE")]
    [InlineData("peru", "PE")]
    public void ACountryMatchesByCodeOrSpanishName(string text, string expected)
    {
        var result = Parse(Row(("TIPO DE DOCUMENTO", "Permiso por protección temporal"), ("PAÍS", text)));

        Assert.Empty(result.Errors);
        Assert.Equal(expected, result.Row!.Profile.Country);
    }

    [Theory]
    [InlineData("Alemania")]
    [InlineData("XX")]
    public void AnUnknownCountryIsCountryInvalid(string text)
    {
        var result = Parse(Row(("TIPO DE DOCUMENTO", "Permiso por protección temporal"), ("PAÍS", text)));

        var issue = Assert.Single(result.Errors);
        Assert.Equal("hr.import.row.country_invalid", issue.Code);
        Assert.Equal("country", issue.Field);
    }

    [Fact]
    public void ACcFromAnotherCountryIsAMismatch()
    {
        var result = Parse(Row(("TIPO DE DOCUMENTO", "Cédula de ciudadanía"), ("PAÍS", "Venezuela")));

        var issue = Assert.Single(result.Errors);
        Assert.Equal("hr.import.row.country_document_mismatch", issue.Code);
        Assert.Equal("country", issue.Field);
    }

    [Fact]
    public void PensionExemptionIsCheckedAgainstTheCountry()
    {
        var foreigner = Parse(Row(("TIPO DE DOCUMENTO", "Permiso por protección temporal"), ("PAÍS", "Venezuela"), ("EXENTO DE PENSIÓN", "Sí")));
        var colombian = Parse(Row(("TIPO DE DOCUMENTO", "Cédula de ciudadanía"), ("EXENTO DE PENSIÓN", "Sí")));

        Assert.Empty(foreigner.Errors);
        Assert.True(foreigner.Row!.Profile.ForeignPensionExempt);
        Assert.Equal("hr.import.row.foreign_pension_exempt_not_allowed", Assert.Single(colombian.Errors).Code);
    }

    [Fact]
    public void AnEmptyCompanyTakesTheOnlyOne()
    {
        var result = Parse(Row());

        Assert.Equal(Qep.Id, result.Row!.Terms.CompanyId);
        Assert.Equal("Comercializadora QEP S.A.S.", result.Row.CompanyName);
    }

    [Fact]
    public void AnEmptyCompanyWithSeveralIsCompanyRequired()
    {
        var result = Parse(Row(), Context(twoCompanies: true));

        var issue = Assert.Single(result.Errors);
        Assert.Equal("hr.import.row.company_required", issue.Code);
        Assert.Equal("company", issue.Field);
    }

    [Fact]
    public void AnUnknownCompanyIsCompanyNotFound()
    {
        var result = Parse(Row(("EMPRESA", "Fantasma S.A.S. - 1")));

        // Sin empresa resuelta el centro no se evalúa: sólo se reporta la empresa.
        var issue = Assert.Single(result.Errors);
        Assert.Equal("hr.import.row.company_not_found", issue.Code);
    }

    [Fact]
    public void AWorkplaceOfAnotherCompanyIsAnError()
    {
        var result = Parse(
            Row(("EMPRESA", "Comercializadora QEP S.A.S. - 900123456"), ("CENTRO DE TRABAJO", "Planta - Norte")),
            Context(twoCompanies: true));

        var issue = Assert.Single(result.Errors);
        Assert.Equal("hr.import.row.workplace_other_company", issue.Code);
        Assert.Equal("workplace", issue.Field);
    }
}
