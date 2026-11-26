using Modules.Payroll.Application;
using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;
using Modules.Payroll.UnitTests.Engine;

namespace Modules.Payroll.UnitTests;

/// <summary>De lo que Hr, las capturas y los recurrentes dicen al <see cref="EngineInput"/> que el motor lee.</summary>
public sealed class EngineInputFactoryTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid Company = Guid.Parse("01900000-0000-7000-8000-0000000000c1");
    private static readonly Guid EmployeeId = Guid.Parse("01900000-0000-7000-8000-0000000000e1");
    private static readonly PayrollActor Ana = new(Guid.Parse("01900000-0000-7000-8000-0000000000a1"), "Ana Gómez");
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 15, 0, 0, TimeSpan.Zero);
    private static readonly PeriodInfo Period = new(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 15), PayrollHalf.First);
    private static readonly EngineSettings Settings = new(TransportFrequency.Biweekly, TransportCapBase.SalaryOnly, OvertimeBase.SalaryOnly, true);
    private static readonly LegalValueSet Legal = EngineTestData.ForDate(Period.Start);

    private static readonly Guid ExtraDay = SystemConcepts.All.Single(item => item.Key == SystemConceptKey.ExtraDay).Id;
    private static readonly Guid ExtraNight = SystemConcepts.All.Single(item => item.Key == SystemConceptKey.ExtraNight).Id;
    private static readonly Guid Commissions = SystemConcepts.All.Single(item => item.Key == SystemConceptKey.Commissions).Id;
    private static readonly Guid OtherIncome = SystemConcepts.All.Single(item => item.Key == SystemConceptKey.OtherIncome).Id;
    private static readonly Guid OtherDeduction = SystemConcepts.All.Single(item => item.Key == SystemConceptKey.OtherDeduction).Id;

    private sealed class FakeConceptRepository : IPayrollConceptRepository
    {
        public List<PayrollConcept> Stored { get; } = [];

        public Task<PayrollConcept?> FindAsync(Guid tenantId, Guid companyId, PayrollConceptId id, CancellationToken cancellationToken) =>
            Task.FromResult(Stored.FirstOrDefault(concept => concept.Id == id));

        public Task<IReadOnlyList<PayrollConcept>> ListAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PayrollConcept>>(Stored.ToList());

        public void Add(PayrollConcept concept) => Stored.Add(concept);
    }

    private static async Task<ConceptCatalogSnapshot> CatalogAsync(params PayrollConcept[] own)
    {
        var repository = new FakeConceptRepository();
        repository.Stored.AddRange(own);
        return await new ConceptCatalog(repository).LoadAsync(Tenant, Company, TestContext.Current.CancellationToken);
    }

    private static HrAssignmentSource Assignment(ArlClass riskClass = ArlClass.III, bool highRisk = false) =>
        new(new DateOnly(2025, 1, 1), "Analista", "Principal", "Bodega", riskClass, highRisk, null);

    private static HrEmployeeSource Employee(
        IReadOnlyList<HrSalarySource>? salaries = null, DateOnly? endedOn = null, HrAssignmentSource? assignment = null) =>
        new(
            EmployeeId,
            "Ana",
            "Pérez",
            "CC",
            "1020304050",
            "Standard",
            false,
            [
                new HrContractSource(
                    Guid.NewGuid(),
                    "Employee",
                    "Monthly",
                    new DateOnly(2025, 1, 1),
                    endedOn,
                    salaries ?? [new HrSalarySource(new DateOnly(2025, 1, 1), 2_500_000m, false)],
                    [assignment ?? Assignment()]),
            ],
            [DayOfWeek.Sunday]);

    private static IReadOnlyList<PeriodInput> Inputs(params PeriodInputDraft[] drafts)
    {
        var period = PayrollPeriod.Open(PayrollPeriodId.New(), Tenant, Company, (Period.Start, Period.End, Period.Half), Ana, Now);
        period.ReplaceInputs(EmployeeId, drafts, Ana, Now);
        return period.Inputs;
    }

    private static RecurringConcept Recurring(
        Guid conceptId, RecurringMode mode = RecurringMode.Fixed, decimal amount = 100_000m, bool? constitutive = false, decimal? cap = null) =>
        RecurringConcept.Create(RecurringConceptId.New(), Tenant, Company, EmployeeId, conceptId, mode, amount, constitutive, cap, Now);

    private static EngineInput Build(
        HrEmployeeSource employee,
        ConceptCatalogSnapshot catalog,
        IReadOnlyList<PeriodInput>? inputs = null,
        IReadOnlyList<RecurringConcept>? recurring = null,
        IReadOnlyDictionary<Guid, decimal>? applied = null,
        IReadOnlySet<Guid>? retired = null,
        IReadOnlyList<VacationInput>? vacations = null) =>
        EngineInputFactory.Build(
            employee,
            PayrollEligibility.Evaluate(employee, Period.Start, Period.End, EngineTestData.Smmlv),
            Period,
            Settings,
            Legal,
            catalog,
            inputs ?? [],
            recurring ?? [],
            applied ?? new Dictionary<Guid, decimal>(),
            retired ?? new HashSet<Guid>(),
            MonthAccumulators.Empty,
            vacations ?? []);

    [Fact]
    public async Task ARecurringOfARetiredConceptIsLeftOut()
    {
        var bonus = PayrollConcept.Create(PayrollConceptId.New(), Tenant, Company, ConceptKind.Income, "Bono", false, null, Now);
        var catalog = await CatalogAsync(bonus);
        var kept = Recurring(OtherIncome);
        var retired = Recurring(bonus.Id.Value);

        var input = Build(Employee(), catalog, recurring: [kept, retired], retired: new HashSet<Guid> { bonus.Id.Value });

        Assert.Equal([kept.Id.Value], input.Recurring.Select(item => item.RecurringId));
    }

    [Fact]
    public async Task CapturesBecomeEngineLinesInOrder()
    {
        var catalog = await CatalogAsync();
        var inputs = Inputs(
            new PeriodInputDraft(ExtraNight, ConceptKind.Overtime, 2m, null, null),
            new PeriodInputDraft(ExtraDay, ConceptKind.Overtime, 4.5m, null, null),
            new PeriodInputDraft(Commissions, ConceptKind.Income, null, 500_000m, true),
            new PeriodInputDraft(OtherIncome, ConceptKind.Income, null, 100_000m, false),
            new PeriodInputDraft(OtherDeduction, ConceptKind.Deduction, null, 20_000m, null));

        var input = Build(Employee(), catalog, inputs);

        Assert.Equal([ExtraNight, ExtraDay], input.Overtime.Select(line => line.ConceptId));
        Assert.Equal([2m, 4.5m], input.Overtime.Select(line => line.Hours));
        Assert.Equal([Commissions, OtherIncome], input.Incomes.Select(line => line.ConceptId));
        Assert.Equal(["Comisiones", "Otros ingresos"], input.Incomes.Select(line => line.ConceptName));
        Assert.Equal([true, false], input.Incomes.Select(line => line.SalaryConstitutive));
        var deduction = Assert.Single(input.Deductions);
        Assert.Equal(OtherDeduction, deduction.ConceptId);
        Assert.Equal(20_000m, deduction.Amount);
        Assert.Equal(EmployeeId, input.EmployeeId);
        Assert.Equal(Period, input.Period);
        Assert.Equal(Settings, input.Settings);
        Assert.Same(Legal, input.Legal);
    }

    [Fact]
    public async Task SystemOvertimeCarriesItsKeyAndCustomItsMultiplier()
    {
        var custom = PayrollConcept.Create(PayrollConceptId.New(), Tenant, Company, ConceptKind.Overtime, "Hora especial", null, 1.4m, Now);
        var catalog = await CatalogAsync(custom);
        var inputs = Inputs(
            new PeriodInputDraft(ExtraDay, ConceptKind.Overtime, 3m, null, null),
            new PeriodInputDraft(custom.Id.Value, ConceptKind.Overtime, 2m, null, null));

        var input = Build(Employee(), catalog, inputs);

        var system = input.Overtime[0];
        Assert.Equal(SystemConceptKey.ExtraDay, system.SystemKey);
        Assert.Null(system.CustomMultiplier);
        var own = input.Overtime[1];
        Assert.Null(own.SystemKey);
        Assert.Equal(1.4m, own.CustomMultiplier);
        Assert.Equal("Hora especial", own.ConceptName);
    }

    [Fact]
    public async Task ExhaustedAndInactiveRecurringAreLeftOut()
    {
        var catalog = await CatalogAsync();
        var active = Recurring(OtherIncome, cap: 1_200_000m);
        var inactive = Recurring(OtherIncome);
        inactive.Deactivate(Now);
        var exhausted = Recurring(OtherIncome, cap: 500_000m);
        var applied = new Dictionary<Guid, decimal> { [exhausted.Id.Value] = 500_000m, [active.Id.Value] = 100_000m };

        var input = Build(Employee(), catalog, recurring: [active, inactive, exhausted], applied: applied);

        var kept = Assert.Single(input.Recurring);
        Assert.Equal(active.Id.Value, kept.RecurringId);
    }

    [Fact]
    public async Task TheRemainingCapSubtractsWhatWasApplied()
    {
        var catalog = await CatalogAsync();
        var capped = Recurring(OtherIncome, cap: 1_200_000m);
        var open = Recurring(OtherDeduction, constitutive: null);
        var applied = new Dictionary<Guid, decimal> { [capped.Id.Value] = 300_000m };

        var input = Build(Employee(), catalog, recurring: [capped, open], applied: applied);

        var first = input.Recurring.Single(item => item.RecurringId == capped.Id.Value);
        Assert.Equal(900_000m, first.RemainingCap);
        Assert.Equal(ConceptKind.Income, first.Kind);
        Assert.Equal("Otros ingresos", first.ConceptName);
        var second = input.Recurring.Single(item => item.RecurringId == open.Id.Value);
        Assert.Null(second.RemainingCap);
        Assert.Equal(ConceptKind.Deduction, second.Kind);
        Assert.False(second.SalaryConstitutive);
    }

    [Fact]
    public async Task HrNamesBecomeTheWorkplaceInfo()
    {
        var input = Build(Employee(assignment: Assignment(ArlClass.V, highRisk: true)), await CatalogAsync());

        Assert.Equal(new WorkplaceInfo("Bodega", ArlClass.V, true), input.Workplace);
        Assert.Equal(new DateOnly(2025, 1, 1), input.ContractStart);
    }

    // I-10: lo programado en Hr para después no entra y la huella no cambia.
    [Fact]
    public async Task FutureSalariesAndEndsAreLeftOut()
    {
        var catalog = await CatalogAsync();
        var current = new HrSalarySource(new DateOnly(2025, 1, 1), 2_500_000m, false);
        var future = new HrSalarySource(new DateOnly(2026, 12, 1), 3_000_000m, false);

        var plain = Build(Employee([current]), catalog);
        var withFuture = Build(Employee([current, future], endedOn: new DateOnly(2026, 12, 31)), catalog);

        Assert.Equal([new SalarySegmentSource(new DateOnly(2025, 1, 1), 2_500_000m, false)], withFuture.Salaries);
        Assert.Null(withFuture.ContractEndedOn);
        Assert.Equal(InputFingerprint.Of(plain), InputFingerprint.Of(withFuture));
    }

    [Fact]
    public async Task AnEndInsideThePeriodIsKeptWithASalaryChangeOnTheLastDay()
    {
        var catalog = await CatalogAsync();
        var raise = new HrSalarySource(new DateOnly(2026, 11, 15), 3_000_000m, true);

        var input = Build(
            Employee([new HrSalarySource(new DateOnly(2025, 1, 1), 2_500_000m, false), raise], endedOn: new DateOnly(2026, 11, 10)),
            catalog);

        Assert.Equal(new DateOnly(2026, 11, 10), input.ContractEndedOn);
        Assert.Equal(2, input.Salaries.Count);
        Assert.True(input.Salaries[1].Teleworker);
    }

    [Fact]
    public async Task VacationsTravelInTheInput()
    {
        var catalog = await CatalogAsync();
        var vacation = EngineTestData.Vacation(new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 6), paidHere: true);

        var withVacation = Build(Employee(), catalog, vacations: [vacation]);
        var without = Build(Employee(), catalog);

        Assert.Equal([vacation], withVacation.Vacations);
        Assert.Empty(without.Vacations);
        Assert.NotEqual(InputFingerprint.Of(without), InputFingerprint.Of(withVacation));
    }
}
