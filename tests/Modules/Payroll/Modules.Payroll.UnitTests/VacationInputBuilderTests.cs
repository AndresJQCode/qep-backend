using Modules.Payroll.Application;
using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;
using Modules.Payroll.UnitTests.Engine;

namespace Modules.Payroll.UnitTests;

public sealed class VacationInputBuilderTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid Company = Guid.Parse("01900000-0000-7000-8000-0000000000c1");
    private static readonly Guid EmployeeId = Guid.Parse("01900000-0000-7000-8000-0000000000e1");
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 15, 0, 0, TimeSpan.Zero);
    private static readonly PayrollActor Ana = new(Guid.Parse("01900000-0000-7000-8000-0000000000a1"), "Ana Gómez");
    private static readonly IReadOnlyDictionary<Guid, VacationPhoto> NoPhotos = new Dictionary<Guid, VacationPhoto>();

    private readonly FakePeriods _periods = new();
    private readonly FakeResults _results = new();

    private VacationInputBuilder Builder() => new(_periods, _results);

    private static HrContractSource Contract(DateOnly? endedOn = null, DateOnly? startedOn = null, params HrSalarySource[] salaries) => new(
        Guid.NewGuid(),
        "Employee",
        "Monthly",
        startedOn ?? new DateOnly(2025, 1, 1),
        endedOn,
        salaries.Length == 0 ? [new HrSalarySource(new DateOnly(2025, 1, 1), 3_000_000m, false)] : salaries,
        [new HrAssignmentSource(new DateOnly(2025, 1, 1), "Analista", "Principal", "Bodega", ArlClass.I, false, null)]);

    private static HrEmployeeSource Employee(HrContractSource contract) => new(
        EmployeeId, "Ana", "Pérez", "CC", "1020304050", "Standard", false, [contract], [DayOfWeek.Saturday, DayOfWeek.Sunday]);

    private static Vacation Qep(DateOnly start, DateOnly end, decimal? typedIbc = null) => Vacation.Create(
        VacationId.New(), Tenant, Company, EmployeeId, VacationOrigin.Qep, start, end, 15, end.AddDays(1), typedIbc, [], Now);

    private static Vacation External(DateOnly start, DateOnly end, params ExternalVacationMonth[] months) => Vacation.Create(
        VacationId.New(), Tenant, Company, EmployeeId, VacationOrigin.External, start, end, 8, end.AddDays(1), 2_800_000m, months, Now);

    private static PayrollPeriod Period(DateOnly start)
    {
        var quincena = QuincenaCalendar.Containing(start);
        return PayrollPeriod.Open(PayrollPeriodId.New(), Tenant, Company, quincena, Ana, Now);
    }

    private PayrollPeriod Liquidated(DateOnly start, params EmployeeResult[] results)
    {
        var period = Period(start);
        period.RecordLiquidation(Ana, true, "{}", start, "2026.11.5", "fp", "[]", Now);
        _periods.Add(period);
        _results.Add(period, results);
        return period;
    }

    private static EmployeeResult ResultWith(decimal ibc, params VacationPhoto[] photos)
    {
        PayrollLine[] lines =
        [
            new("ibc", PayrollLineGroup.Info, "Ingreso base de cotización del mes", ibc, null, new LineTrace("ibc", [], [], null)),
        ];
        var liquidation = new EmployeeLiquidation(
            EmployeeId, 15, false, false, lines, PayrollSummary.From(lines), MonthAccumulators.Empty, "2026.11.5", 0, photos);
        return new EmployeeResult(EmployeeId, "CC", "1020304050", "Ana Pérez", "Analista", "Bodega", liquidation, "fp", new Dictionary<Guid, decimal>());
    }

    private static VacationPhoto PhotoOf(Vacation vacation, params VacationMonthAdvance[] months) => new(
        vacation.Id.Value,
        3_000_000m,
        new DateOnly(2025, 1, 1),
        2_800_000m,
        VacationIbcSource.Typed,
        VacationCalendar.PayrollDays(vacation.StartDate, vacation.EndDate),
        months);

    private async Task<VacationPlan> BuildAsync(
        PayrollPeriod period,
        HrContractSource contract,
        IReadOnlyList<Vacation> vacations,
        IReadOnlyDictionary<Guid, VacationPhoto>? photos = null) =>
        await Builder().BuildAsync(period, Employee(contract), contract, vacations, photos ?? NoPhotos, TestContext.Current.CancellationToken);

    [Fact]
    public async Task PreviousIbcFromQepWhenBothHalvesHaveResults()
    {
        Liquidated(new DateOnly(2026, 11, 1), ResultWith(2_700_000m));
        Liquidated(new DateOnly(2026, 11, 16), ResultWith(2_800_000m));
        var vacation = Qep(new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5), typedIbc: 2_500_000m);

        var plan = await BuildAsync(Period(new DateOnly(2026, 12, 1)), Contract(), [vacation]);

        var input = Assert.Single(plan.Inputs);
        Assert.Equal(2_800_000m, input.PreviousMonthIbc);
        Assert.Equal(VacationIbcSource.Qep, input.PreviousIbcSource);
        Assert.True(input.PaidHere);
        Assert.Equal((new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5), 15), (input.StartDate, input.EndDate, input.BusinessDays));
        Assert.Equal(VacationAdvance.Zero, input.MonthAdvance);
        Assert.Null(input.MonthVacationPay);
        Assert.Equal((3_000_000m, new DateOnly(2025, 1, 1)), (input.SalaryAtStart, input.SalaryRecordDate));
        Assert.Empty(plan.Unpaid);
    }

    [Fact]
    public async Task PreviousIbcTypedWhenAHalfIsMissing()
    {
        // Only the first half of November exists: the month cannot be trusted, so the typed value wins.
        Liquidated(new DateOnly(2026, 11, 1), ResultWith(2_700_000m));
        var vacation = Qep(new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5), typedIbc: 2_500_000m);

        var plan = await BuildAsync(Period(new DateOnly(2026, 12, 1)), Contract(), [vacation]);

        var input = Assert.Single(plan.Inputs);
        Assert.Equal((2_500_000m, VacationIbcSource.Typed), (input.PreviousMonthIbc, input.PreviousIbcSource));
    }

    [Fact]
    public async Task ADraftHalfDoesNotCountAsAQepIbc()
    {
        Liquidated(new DateOnly(2026, 11, 1), ResultWith(2_700_000m));
        _periods.Add(Period(new DateOnly(2026, 11, 16)));
        var vacation = Qep(new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5));

        var plan = await BuildAsync(Period(new DateOnly(2026, 12, 1)), Contract(), [vacation]);

        Assert.Equal(VacationIbcSource.Salary, Assert.Single(plan.Inputs).PreviousIbcSource);
    }

    [Fact]
    public async Task AReopenedOrOutdatedHalfDoesNotCountAsAQepIbc()
    {
        // Reopening keeps the old result rows until the next liquidation (PayrollPeriod.Reopen), so a stored result is not enough:
        // the spec says «Liquidada o más», and a half that is Reopened or Outdated is no longer one.
        Liquidated(new DateOnly(2026, 11, 1), ResultWith(2_700_000m));
        var second = Liquidated(new DateOnly(2026, 11, 16), ResultWith(2_800_000m));
        var vacation = Qep(new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5), typedIbc: 2_500_000m);

        second.MarkOutdated(Ana, Now);
        var outdated = Assert.Single((await BuildAsync(Period(new DateOnly(2026, 12, 1)), Contract(), [vacation])).Inputs);
        Assert.Equal((2_500_000m, VacationIbcSource.Typed), (outdated.PreviousMonthIbc, outdated.PreviousIbcSource));

        var reopened = Liquidated(new DateOnly(2026, 11, 16), ResultWith(2_800_000m));
        reopened.Approve(new ApprovalContext(Ana, false, false, false, true), Now);
        reopened.Reopen(Ana, "Corregir", Now);
        var afterReopen = Assert.Single((await BuildAsync(Period(new DateOnly(2026, 12, 1)), Contract(), [vacation])).Inputs);
        Assert.Equal((2_500_000m, VacationIbcSource.Typed), (afterReopen.PreviousMonthIbc, afterReopen.PreviousIbcSource));
    }

    [Fact]
    public async Task PreviousIbcFallsBackToTheSalary()
    {
        // A raise on the very day the vacation starts: the IBC before the vacation is the salary of the day before.
        var contract = Contract(
            salaries: [new HrSalarySource(new DateOnly(2025, 1, 1), 3_000_000m, false), new HrSalarySource(new DateOnly(2026, 12, 14), 3_300_000m, false)]);
        var vacation = Qep(new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5));

        var plan = await BuildAsync(Period(new DateOnly(2026, 12, 1)), contract, [vacation]);

        var input = Assert.Single(plan.Inputs);
        Assert.Equal((3_000_000m, VacationIbcSource.Salary), (input.PreviousMonthIbc, input.PreviousIbcSource));
        // The value of the vacation is the salary of the day it starts (CST art. 192).
        Assert.Equal((3_300_000m, new DateOnly(2026, 12, 14)), (input.SalaryAtStart, input.SalaryRecordDate));
    }

    [Fact]
    public async Task PaidElsewhereReadsThePhotoOfItsMonth()
    {
        var vacation = Qep(new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5));
        var photo = PhotoOf(
            vacation,
            new VacationMonthAdvance(new DateOnly(2026, 12, 1), 1_700_000m, 68_000m, 68_000m, 0m),
            new VacationMonthAdvance(new DateOnly(2027, 1, 1), 500_000m, 20_000m, 20_000m, 0m));
        var paying = Liquidated(new DateOnly(2026, 12, 1), ResultWith(3_000_000m, photo));

        var plan = await BuildAsync(Period(new DateOnly(2027, 1, 1)), Contract(), [vacation]);

        var input = Assert.Single(plan.Inputs);
        Assert.False(input.PaidHere);
        Assert.Equal(new VacationAdvance(20_000m, 20_000m, 0m), input.MonthAdvance);
        Assert.Equal((2_800_000m, VacationIbcSource.Typed), (input.PreviousMonthIbc, input.PreviousIbcSource));
        Assert.Equal(3_000_000m, input.SalaryAtStart);
        Assert.Empty(plan.Unpaid);
        Assert.Equal(PayrollPeriodStatus.Liquidated, paying.Status);
    }

    [Fact]
    public async Task PaidElsewhereNamesThePayingQuincenaButAnOverrideDoesNot()
    {
        // Liquidating re-reads what it just paid: an override is the photo about to be written, so there is no stored quincena to trust.
        var vacation = Qep(new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5));
        Liquidated(new DateOnly(2026, 12, 1), ResultWith(3_000_000m, PhotoOf(vacation)));
        var january = Period(new DateOnly(2027, 1, 1));

        var stored = await BuildAsync(january, Contract(), [vacation]);
        var overridden = await BuildAsync(
            january, Contract(), [vacation], new Dictionary<Guid, VacationPhoto> { [vacation.Id.Value] = PhotoOf(vacation) });

        Assert.Equal([new DateOnly(2026, 12, 1)], stored.PaidElsewhere);
        Assert.Empty(overridden.PaidElsewhere);
    }

    [Fact]
    public async Task PaidElsewhereWithoutAPhotoIsUnpaid()
    {
        var vacation = Qep(new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5));

        var plan = await BuildAsync(Period(new DateOnly(2027, 1, 1)), Contract(), [vacation]);

        var input = Assert.Single(plan.Inputs);
        Assert.Equal(0m, input.SalaryAtStart);
        Assert.Equal(0m, input.PreviousMonthIbc);
        Assert.Equal(VacationAdvance.Zero, input.MonthAdvance);
        Assert.Equal(
            new UnpaidVacation(EmployeeId, vacation.Id.Value, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 15)),
            Assert.Single(plan.Unpaid));
    }

    [Fact]
    public async Task APayingPeriodWithoutTheResultOfTheEmployeeIsUnpaid()
    {
        var vacation = Qep(new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5));
        Liquidated(new DateOnly(2026, 12, 1));

        var plan = await BuildAsync(Period(new DateOnly(2027, 1, 1)), Contract(), [vacation]);

        Assert.Single(plan.Unpaid);
    }

    [Fact]
    public async Task AnOutdatedPayingPeriodIsUnpaid()
    {
        var vacation = Qep(new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5));
        var paying = Liquidated(new DateOnly(2026, 12, 1), ResultWith(3_000_000m, PhotoOf(vacation)));
        paying.MarkOutdated(Ana, Now);

        var plan = await BuildAsync(Period(new DateOnly(2027, 1, 1)), Contract(), [vacation]);

        Assert.Equal(vacation.Id.Value, Assert.Single(plan.Unpaid).VacationId);
        Assert.Equal(0m, Assert.Single(plan.Inputs).SalaryAtStart);
    }

    [Fact]
    public async Task ADraftPayingPeriodIsUnpaid()
    {
        var vacation = Qep(new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5));
        _periods.Add(Period(new DateOnly(2026, 12, 1)));

        var plan = await BuildAsync(Period(new DateOnly(2027, 1, 1)), Contract(), [vacation]);

        Assert.Single(plan.Unpaid);
    }

    [Fact]
    public async Task ExternalUsesTheTypedMonth()
    {
        var vacation = External(
            new DateOnly(2026, 11, 23),
            new DateOnly(2026, 12, 4),
            new ExternalVacationMonth(new DateOnly(2026, 11, 1), 320_000m, 12_800m, 12_800m, 0m),
            new ExternalVacationMonth(new DateOnly(2026, 12, 1), 480_000m, 19_200m, 19_200m, 500m));

        var plan = await BuildAsync(Period(new DateOnly(2026, 12, 1)), Contract(), [vacation]);

        var input = Assert.Single(plan.Inputs);
        Assert.Equal(VacationOrigin.External, input.Origin);
        Assert.False(input.PaidHere);
        Assert.Equal(480_000m, input.MonthVacationPay);
        Assert.Equal(new VacationAdvance(19_200m, 19_200m, 500m), input.MonthAdvance);
        Assert.Equal((2_800_000m, VacationIbcSource.Typed), (input.PreviousMonthIbc, input.PreviousIbcSource));
        // The part before the month stays out and the typed days are only shown, not counted: zero business days here.
        Assert.Equal((new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 4), 0), (input.StartDate, input.EndDate, input.BusinessDays));
        Assert.Empty(plan.Unpaid);
    }

    [Fact]
    public async Task ExternalWithoutARowForTheMonthTypesNothing()
    {
        var vacation = External(
            new DateOnly(2026, 11, 23),
            new DateOnly(2026, 12, 4),
            new ExternalVacationMonth(new DateOnly(2026, 11, 1), 320_000m, 12_800m, 12_800m, 0m));

        var plan = await BuildAsync(Period(new DateOnly(2026, 12, 1)), Contract(), [vacation]);

        var input = Assert.Single(plan.Inputs);
        Assert.Null(input.MonthVacationPay);
        Assert.Equal(VacationAdvance.Zero, input.MonthAdvance);
    }

    [Fact]
    public async Task ATypedPayCutsTheEndAtTheEndOfTheMonth()
    {
        // The value of a day divides what Siigo paid by the days of the WHOLE month, so the later quincenas must be counted too.
        var vacation = External(
            new DateOnly(2026, 12, 14),
            new DateOnly(2027, 1, 5),
            new ExternalVacationMonth(new DateOnly(2026, 12, 1), 800_000m, 0m, 0m, 0m));

        var plan = await BuildAsync(Period(new DateOnly(2026, 12, 1)), Contract(), [vacation]);

        Assert.Equal(new DateOnly(2026, 12, 31), Assert.Single(plan.Inputs).EndDate);
    }

    [Fact]
    public async Task PhotoOverrideWins()
    {
        var vacation = Qep(new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5));
        Liquidated(new DateOnly(2026, 12, 1), ResultWith(
            3_000_000m,
            PhotoOf(vacation, new VacationMonthAdvance(new DateOnly(2027, 1, 1), 500_000m, 20_000m, 20_000m, 0m))));
        var overridden = PhotoOf(vacation, new VacationMonthAdvance(new DateOnly(2027, 1, 1), 500_000m, 11_000m, 12_000m, 0m)) with
        {
            SalaryAtStart = 3_500_000m,
        };

        var plan = await BuildAsync(
            Period(new DateOnly(2027, 1, 1)), Contract(), [vacation], new Dictionary<Guid, VacationPhoto> { [vacation.Id.Value] = overridden });

        var input = Assert.Single(plan.Inputs);
        Assert.Equal(new VacationAdvance(11_000m, 12_000m, 0m), input.MonthAdvance);
        Assert.Equal(3_500_000m, input.SalaryAtStart);
        Assert.Empty(plan.Unpaid);
    }

    [Fact]
    public async Task ANonPayingQuincenaGetsClippedDates()
    {
        // Review Focus 4: editing the end of a vacation must not change the input of a quincena whose calculation it does not change.
        var vacation = Qep(new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5));
        var photos = new Dictionary<Guid, VacationPhoto> { [vacation.Id.Value] = PhotoOf(vacation) };
        var january = Period(new DateOnly(2027, 1, 1));
        var decemberSecond = Period(new DateOnly(2026, 12, 16));

        var januaryBefore = Assert.Single((await BuildAsync(january, Contract(), [vacation], photos)).Inputs);
        var decemberBefore = Assert.Single((await BuildAsync(decemberSecond, Contract(), [vacation], photos)).Inputs);
        vacation.Update(VacationOrigin.Qep, vacation.StartDate, new DateOnly(2027, 1, 8), 17, new DateOnly(2027, 1, 12), null, [], Now);
        var januaryAfter = Assert.Single((await BuildAsync(january, Contract(), [vacation], photos)).Inputs);
        var decemberAfter = Assert.Single((await BuildAsync(decemberSecond, Contract(), [vacation], photos)).Inputs);

        Assert.Equal((new DateOnly(2027, 1, 1), new DateOnly(2027, 1, 5), 0), (januaryBefore.StartDate, januaryBefore.EndDate, januaryBefore.BusinessDays));
        Assert.Equal((new DateOnly(2027, 1, 1), new DateOnly(2027, 1, 8), 0), (januaryAfter.StartDate, januaryAfter.EndDate, januaryAfter.BusinessDays));
        Assert.NotEqual(januaryBefore, januaryAfter);
        Assert.Equal((new DateOnly(2026, 12, 14), new DateOnly(2026, 12, 31), 0), (decemberBefore.StartDate, decemberBefore.EndDate, decemberBefore.BusinessDays));
        Assert.Equal(decemberBefore, decemberAfter);
    }

    [Fact]
    public async Task AVacationOfAnotherMonthIsNotAnInput()
    {
        var past = Qep(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 20));
        var future = Qep(new DateOnly(2027, 1, 12), new DateOnly(2027, 2, 1));

        var plan = await BuildAsync(Period(new DateOnly(2026, 12, 1)), Contract(), [past, future]);

        Assert.Empty(plan.Inputs);
        Assert.Empty(plan.Unpaid);
    }

    [Fact]
    public async Task InputsComeInStartOrder()
    {
        var late = Qep(new DateOnly(2026, 12, 10), new DateOnly(2026, 12, 12));
        var early = Qep(new DateOnly(2026, 12, 2), new DateOnly(2026, 12, 4));

        var plan = await BuildAsync(Period(new DateOnly(2026, 12, 1)), Contract(), [late, early]);

        Assert.Equal([early.Id.Value, late.Id.Value], plan.Inputs.Select(input => input.VacationId));
    }

    [Fact]
    public async Task AVacationIsTrimmedToTheDaysTheContractCovers()
    {
        // A contract that ends before the vacation does (Hr shortened it later). The employee is blocked by
        // PayrollEligibility.WithVacations, but the input must never carry days that exceed the covered ones: net worked days
        // cannot go negative.
        var contract = Contract(endedOn: new DateOnly(2026, 12, 20));
        var vacation = Qep(new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5));
        var photos = new Dictionary<Guid, VacationPhoto> { [vacation.Id.Value] = PhotoOf(vacation) };
        var period = Period(new DateOnly(2026, 12, 16));

        var plan = await BuildAsync(period, contract, [vacation], photos);

        var input = Assert.Single(plan.Inputs);
        Assert.Equal((new DateOnly(2026, 12, 14), new DateOnly(2026, 12, 20)), (input.StartDate, input.EndDate));
        var engineInput = EngineTestData.Input(period.StartDate, 3_000_000m) with { ContractEndedOn = contract.EndedOn, Vacations = plan.Inputs };
        var worked = WorkedDaysRule.Compute(engineInput.Period, engineInput.ContractStart, engineInput.ContractEndedOn);
        var days = VacationDaysRule.Compute(engineInput, worked).Days;
        Assert.Equal(5, days);
        Assert.True(worked.Days - days >= 0);
    }

    [Fact]
    public async Task AVacationEntirelyOutsideTheContractIsDropped()
    {
        var contract = Contract(startedOn: new DateOnly(2026, 12, 20));
        var vacation = Qep(new DateOnly(2026, 12, 2), new DateOnly(2026, 12, 8));

        var plan = await BuildAsync(Period(new DateOnly(2026, 12, 1)), contract, [vacation]);

        Assert.Empty(plan.Inputs);
    }

    private sealed class FakePeriods : IPayrollPeriodRepository
    {
        private readonly Dictionary<DateOnly, PayrollPeriod> _byStart = [];

        public void Add(PayrollPeriod period) => _byStart[period.StartDate] = period;

        void IPayrollPeriodRepository.Add(PayrollPeriod period) => Add(period);

        public Task<PayrollPeriod?> FindByStartAsync(Guid tenantId, Guid companyId, DateOnly start, CancellationToken cancellationToken) =>
            Task.FromResult(_byStart.GetValueOrDefault(start));

        public Task<PayrollPeriod?> FindAsync(Guid tenantId, PayrollPeriodId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PayrollPeriod>> ListAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PayrollPeriod?> FindTrackedByStartAsync(Guid tenantId, Guid companyId, DateOnly start, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PeriodInput>> ListInputsAsync(Guid tenantId, PayrollPeriodId periodId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> ListCompanyIdsAsync(Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeResults : IPayrollResultStore
    {
        private readonly Dictionary<PayrollPeriodId, List<EmployeeResult>> _byPeriod = [];

        public void Add(PayrollPeriod period, EmployeeResult[] results) => _byPeriod[period.Id] = [.. results];

        public Task<EmployeeResult?> FindAsync(Guid tenantId, PayrollPeriodId periodId, Guid employeeId, CancellationToken cancellationToken) =>
            Task.FromResult(_byPeriod.GetValueOrDefault(periodId)?.FirstOrDefault(result => result.EmployeeId == employeeId));

        public Task<IReadOnlyList<EmployeeResult>> ListAsync(Guid tenantId, PayrollPeriodId periodId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReplaceAllAsync(
            Guid tenantId, PayrollPeriodId periodId, IReadOnlyList<EmployeeResult> results, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, decimal>> SumRecurringAppliedAsync(
            Guid tenantId, Guid companyId, Guid employeeId, DateOnly? before, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
