using Modules.Payroll.Application;
using Modules.Payroll.Domain;

namespace Modules.Payroll.UnitTests;

/// <summary>Las reglas de una fila de la importación de novedades (contrato HTTP, «Importación de novedades»).</summary>
public sealed class InputsImportRowParserTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid Company = Guid.Parse("01900000-0000-7000-8000-0000000000c1");
    private static readonly DateOnly Start = new(2026, 11, 1);
    private static readonly DateOnly End = new(2026, 11, 15);
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 15, 0, 0, TimeSpan.Zero);

    private sealed class FakeConceptRepository : IPayrollConceptRepository
    {
        public List<PayrollConcept> Stored { get; } = [];

        public Task<PayrollConcept?> FindAsync(Guid tenantId, Guid companyId, PayrollConceptId id, CancellationToken cancellationToken) =>
            Task.FromResult(Stored.FirstOrDefault(concept => concept.Id == id));

        public Task<IReadOnlyList<PayrollConcept>> ListAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PayrollConcept>>(Stored.ToList());

        public void Add(PayrollConcept concept) => Stored.Add(concept);
    }

    private static HrEmployeeSource Employee(string document, string salaryType = "Monthly", string first = "Ana") =>
        new(
            Guid.NewGuid(),
            first,
            "Pérez",
            "CC",
            document,
            "Standard",
            false,
            [
                new HrContractSource(
                    Guid.NewGuid(),
                    "Employee",
                    salaryType,
                    new DateOnly(2025, 1, 1),
                    null,
                    [new HrSalarySource(new DateOnly(2025, 1, 1), 25_000_000m, false)],
                    [new HrAssignmentSource(new DateOnly(2025, 1, 1), "Analista", "Principal", "Bodega", Modules.Payroll.Domain.Engine.ArlClass.I, false, null)]),
            ],
            [DayOfWeek.Sunday]);

    private static InputsImportContext Context(params PayrollConcept[] own)
    {
        var repository = new FakeConceptRepository();
        repository.Stored.AddRange(own);
        var catalog = new ConceptCatalog(repository).LoadAsync(Tenant, Company, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        var liquidable = Employee("1020304050");
        var integral = Employee("2000", salaryType: "Integral", first: "Beto");
        var sources = new Dictionary<string, HrEmployeeSource> { ["1020304050"] = liquidable, ["2000"] = integral };
        var eligibility = sources.ToDictionary(
            entry => entry.Key,
            entry => PayrollEligibility.Evaluate(entry.Value, Start, End, 1_750_905m));
        return new InputsImportContext(eligibility, sources, catalog);
    }

    private static ExcelInputsRow Row(
        string? document = "1020304050", string? concept = "Hora extra diurna", string? hours = "4.5", string? amount = null, string? constitutive = null) =>
        new(
            2,
            new Dictionary<string, string?>
            {
                [InputsImportColumns.DocumentNumber] = document,
                [InputsImportColumns.Employee] = "Ana Pérez",
                [InputsImportColumns.Concept] = concept,
                [InputsImportColumns.Hours] = hours,
                [InputsImportColumns.Amount] = amount,
                [InputsImportColumns.SalaryConstitutive] = constitutive,
            });

    private static InputsImportRowResult Parse(ExcelInputsRow row, InputsImportContext? context = null) =>
        InputsImportRowParser.Parse(row, context ?? Context());

    [Fact]
    public void AValidOvertimeRowParses()
    {
        var result = Parse(Row());

        Assert.Empty(result.Errors);
        var row = result.Row!;
        Assert.Equal(2, row.RowNumber);
        Assert.Equal(ConceptKind.Overtime, row.Draft.Kind);
        Assert.Equal(4.5m, row.Draft.Hours);
        Assert.Null(row.Draft.Amount);
        Assert.Equal("Hora extra diurna", row.ConceptName);
        Assert.Equal("Ana Pérez", row.FullName);
    }

    public static TheoryData<string, string, string> EachRule() => new()
    {
        // code, field, which row
        { PayrollErrorCodes.ImportRowDocumentRequired, "documentNumber", "NoDocument" },
        { PayrollErrorCodes.ImportRowEmployeeNotInPeriod, "documentNumber", "Stranger" },
        { PayrollErrorCodes.ImportRowEmployeeNotLiquidable, "documentNumber", "Excluded" },
        { PayrollErrorCodes.ImportRowConceptRequired, "concept", "NoConcept" },
        { PayrollErrorCodes.ImportRowConceptNotFound, "concept", "UnknownConcept" },
        { PayrollErrorCodes.ImportRowHoursRequired, "hours", "NoHours" },
        { PayrollErrorCodes.ImportRowHoursInvalid, "hours", "BadHours" },
        { PayrollErrorCodes.ImportRowHoursNotAllowed, "hours", "HoursOnIncome" },
        { PayrollErrorCodes.ImportRowAmountRequired, "amount", "NoAmount" },
        { PayrollErrorCodes.ImportRowAmountInvalid, "amount", "BadAmount" },
        { PayrollErrorCodes.ImportRowAmountNotAllowed, "amount", "AmountOnOvertime" },
        { PayrollErrorCodes.ImportRowYesNoInvalid, "salaryConstitutive", "BadYesNo" },
        { PayrollErrorCodes.ImportRowSalaryConstitutiveRequired, "salaryConstitutive", "NoClassification" },
        { PayrollErrorCodes.ImportRowSalaryConstitutiveNotAllowed, "salaryConstitutive", "ClassificationOnDeduction" },
    };

    private static ExcelInputsRow RowFor(string name) => name switch
    {
        "NoDocument" => Row(document: null),
        "Stranger" => Row(document: "9999"),
        "Excluded" => Row(document: "2000"),
        "NoConcept" => Row(concept: null),
        "UnknownConcept" => Row(concept: "Concepto que no existe"),
        "NoHours" => Row(hours: null),
        "BadHours" => Row(hours: "abc"),
        "HoursOnIncome" => Row(concept: "Comisiones", hours: "2", amount: "1000", constitutive: "Sí"),
        "NoAmount" => Row(concept: "Comisiones", hours: null, amount: null, constitutive: "Sí"),
        "BadAmount" => Row(concept: "Comisiones", hours: null, amount: "-5", constitutive: "Sí"),
        "AmountOnOvertime" => Row(hours: "2", amount: "1000"),
        "BadYesNo" => Row(concept: "Comisiones", hours: null, amount: "1000", constitutive: "Quizás"),
        "NoClassification" => Row(concept: "Otros ingresos", hours: null, amount: "1000", constitutive: null),
        "ClassificationOnDeduction" => Row(concept: "Otra deducción", hours: null, amount: "1000", constitutive: "No"),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(EachRule))]
    public void EachRuleReportsItsCodeAndField(string code, string field, string row)
    {
        var result = Parse(RowFor(row));

        Assert.Null(result.Row);
        var issue = Assert.Single(result.Errors, error => error.Code == code);
        Assert.Equal(field, issue.Field);
        Assert.Equal(2, issue.RowNumber);
        Assert.False(string.IsNullOrWhiteSpace(issue.Message));
    }

    [Fact]
    public void ConceptsMatchWithoutAccents()
    {
        Assert.Equal("Hora extra diurna", Parse(Row(concept: "hora extra diurna")).Row!.ConceptName);
        Assert.Equal("Comisiones", Parse(Row(concept: "COMISIONES", hours: null, amount: "5000", constitutive: "sí")).Row!.ConceptName);
        Assert.Equal("Bonificación prestacional", Parse(Row(concept: "  bonificacion   PRESTACIONAL ", hours: null, amount: "5000")).Row!.ConceptName);
    }

    [Fact]
    public void AnEmptyClassificationTakesTheSuggestion()
    {
        var commissions = Parse(Row(concept: "Comisiones", hours: null, amount: "500000", constitutive: null));

        Assert.True(commissions.Row!.Draft.SalaryConstitutive);

        var explicitNo = Parse(Row(concept: "Comisiones", hours: null, amount: "500000", constitutive: "No"));

        Assert.False(explicitNo.Row!.Draft.SalaryConstitutive);
    }

    [Fact]
    public void WithoutSuggestionTheClassificationIsRequired()
    {
        var result = Parse(Row(concept: "Otros ingresos", hours: null, amount: "1000", constitutive: null));

        Assert.Equal(PayrollErrorCodes.ImportRowSalaryConstitutiveRequired, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void AnExcludedEmployeeIsNotLiquidable()
    {
        var result = Parse(Row(document: "2000"));

        Assert.Equal(PayrollErrorCodes.ImportRowEmployeeNotLiquidable, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void ARowCanCarrySeveralErrors()
    {
        var result = Parse(Row(document: "9999", concept: "Comisiones", hours: "x", amount: "no", constitutive: "tal vez"));

        Assert.Equal(
            [
                PayrollErrorCodes.ImportRowEmployeeNotInPeriod,
                PayrollErrorCodes.ImportRowHoursNotAllowed,
                PayrollErrorCodes.ImportRowAmountInvalid,
                PayrollErrorCodes.ImportRowYesNoInvalid,
            ],
            result.Errors.Select(error => error.Code));
    }

    [Fact]
    public void ACustomOvertimeConceptMatchesToo()
    {
        var custom = PayrollConcept.Create(PayrollConceptId.New(), Tenant, Company, ConceptKind.Overtime, "Hora de turno", null, 1.5m, Now);

        var result = Parse(Row(concept: "hora de turno", hours: "3"), Context(custom));

        Assert.Equal(custom.Id.Value, result.Row!.Draft.ConceptId);
    }

    [Fact]
    public void AnArchivedConceptIsNotFound()
    {
        var archived = PayrollConcept.Create(PayrollConceptId.New(), Tenant, Company, ConceptKind.Deduction, "Libranza vieja", null, null, Now);
        archived.Archive(Now);

        var result = Parse(Row(concept: "Libranza vieja", hours: null, amount: "1000"), Context(archived));

        Assert.Equal(PayrollErrorCodes.ImportRowConceptNotFound, Assert.Single(result.Errors).Code);
    }
}
