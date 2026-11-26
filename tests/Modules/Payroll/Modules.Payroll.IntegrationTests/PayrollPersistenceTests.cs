using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Payroll.Application;
using Modules.Payroll.Domain;
using Modules.Payroll.Domain.Engine;
using Npgsql;
using Testcontainers.PostgreSql;
using static Modules.Payroll.IntegrationTests.PayrollApiHarness;

namespace Modules.Payroll.IntegrationTests;

/// <summary>
/// El mapeo, los repositorios y la traducción de errores de Payroll contra Postgres real. Una sola base para la clase; cada prueba
/// trabaja en su propio tenant (los ids de tenant no tienen FK), así que no se estorban.
/// </summary>
public sealed class PayrollPersistenceTests : IClassFixture<PayrollPersistenceTests.World>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 15, 0, 0, TimeSpan.Zero);
    private static readonly PayrollActor Ana = new(Guid.Parse("01900000-0000-7000-8000-0000000000a1"), "Ana Gómez");
    private static readonly Guid Commissions = Guid.Parse("00000000-0000-0000-0002-000000000001");
    private static readonly (DateOnly Start, DateOnly End, PayrollHalf Half) FirstHalf =
        (new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 15), PayrollHalf.First);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly World _world;

    public PayrollPersistenceTests(World world) => _world = world;

    public sealed class World : IAsyncLifetime
    {
        private PostgreSqlContainer? _database;

        internal QepApiFactory Factory { get; private set; } = null!;

        public Guid CompanyId { get; private set; }

        public async ValueTask InitializeAsync()
        {
            _database = await StartDatabaseAsync();
            Factory = new QepApiFactory(_database.GetConnectionString());
            await SeedTenantAsync(Factory);
            using var client = CreateClient(
                Factory, SubjectId, TenantId, "companies.company.read", "companies.company.manage");
            CompanyId = (await CreateCompanyAsync(client, Guid.Parse(TenantId), "Empresa uno")).Id;
        }

        public async ValueTask DisposeAsync()
        {
            Factory?.Dispose();
            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }
    }

    // Corre una unidad de trabajo en su propio scope: lo que no se guardó no se ve, y cada lectura sale de la base.
    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _world.Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    private Task<object?> InScopeAsync(Func<IServiceProvider, Task> action) =>
        InScopeAsync<object?>(async provider =>
        {
            await action(provider);
            return null;
        });

    private static Task SaveAsync(IServiceProvider provider) =>
        provider.GetRequiredService<IPayrollUnitOfWork>().SaveChangesAsync(Ct);

    private PayrollPeriod NewPeriod(Guid tenantId, Guid? companyId = null) =>
        PayrollPeriod.Open(PayrollPeriodId.New(), tenantId, companyId ?? _world.CompanyId, FirstHalf, Ana, Now);

    private async Task AddAsync(PayrollPeriod period) =>
        await InScopeAsync(async provider =>
        {
            provider.GetRequiredService<IPayrollPeriodRepository>().Add(period);
            await SaveAsync(provider);
        });

    private Task<PayrollPeriod?> LoadAsync(Guid tenantId, PayrollPeriodId id) =>
        InScopeAsync(provider => provider.GetRequiredService<IPayrollPeriodRepository>().FindAsync(tenantId, id, Ct));

    private static EmployeeResult ResultOf(
        Guid employeeId, string name, decimal salary, int vacationDays = 0, IReadOnlyList<VacationPhoto>? photos = null)
    {
        PayrollLine[] lines =
        [
            new("salary", PayrollLineGroup.Earning, "Salario", salary, null,
                new LineTrace("salario × días ÷ 30", [new TraceInput("salario mensual", 1_750_905m), new TraceInput("modalidad", "Mensual")],
                    [new LegalValue("Smmlv", 1_750_905m, new DateOnly(2026, 1, 1))], null)),
            new("health_employee", PayrollLineGroup.Deduction, "Salud", 35_000m, null,
                new LineTrace("techo100(IBC × 4 %)", [], [], "Exonerado")),
        ];
        var liquidation = new EmployeeLiquidation(
            employeeId, 15, true, false, lines, PayrollSummary.From(lines), MonthAccumulators.Empty with { WorkedDays = 15 }, "2026.11.1", vacationDays, photos ?? []);
        return new EmployeeResult(
            employeeId, "CC", "1020304050", name, "Analista", "Bodega - Principal", liquidation, "fp-" + name,
            new Dictionary<Guid, decimal> { [Commissions] = 100_000m });
    }

    private Vacation NewVacation(
        Guid tenantId, Guid employeeId, DateOnly start, DateOnly end, VacationOrigin origin = VacationOrigin.Qep,
        decimal? previousIbc = null, IReadOnlyList<ExternalVacationMonth>? months = null) =>
        Vacation.Create(
            VacationId.New(), tenantId, _world.CompanyId, employeeId, origin, start, end,
            origin == VacationOrigin.External ? 12 : 15, end.AddDays(1), previousIbc, months ?? [], Now);

    private async Task AddAsync(Vacation vacation) =>
        await InScopeAsync(async provider =>
        {
            provider.GetRequiredService<IVacationRepository>().Add(vacation);
            await SaveAsync(provider);
        });

    [Fact]
    public async Task APeriodWithInputsAndEventsRoundTrips()
    {
        var tenant = Guid.NewGuid();
        var employee = Guid.NewGuid();
        var period = NewPeriod(tenant);
        period.ReplaceInputs(
            employee,
            [
                new PeriodInputDraft(SystemConcepts.All[0].Id, ConceptKind.Overtime, 4.5m, null, null),
                new PeriodInputDraft(Commissions, ConceptKind.Income, null, 500_000.50m, true),
                new PeriodInputDraft(Commissions, ConceptKind.Income, null, 1_000m, false),
                new PeriodInputDraft(SystemConcepts.All[11].Id, ConceptKind.Deduction, null, 20_000m, null),
            ],
            Ana,
            Now);
        period.RecordLiquidation(
            Ana, true, "{\"smmlv\":1750905}", new DateOnly(2026, 11, 1), "2026.11.1", "fp-1",
            "[{\"employeeId\":\"" + Guid.Empty + "\"}]", Now.AddMinutes(1));
        await AddAsync(period);

        var loaded = await LoadAsync(tenant, period.Id);

        Assert.NotNull(loaded);
        Assert.Equal(period.Version, loaded.Version);
        Assert.Equal(PayrollPeriodStatus.Liquidated, loaded.Status);
        Assert.Equal(new DateOnly(2026, 11, 1), loaded.StartDate);
        Assert.Equal(PayrollHalf.First, loaded.Half);
        Assert.Equal(Ana.SubjectId, loaded.LastLiquidatedBy);
        Assert.Equal("2026.11.1", loaded.EngineVersion);
        Assert.Equal(1_750_905, JsonDocument.Parse(loaded.LegalValuesJson!).RootElement.GetProperty("smmlv").GetInt32());
        Assert.Equal(new DateOnly(2026, 11, 1), loaded.LegalValuesDate);
        Assert.NotNull(loaded.ExcludedJson);
        Assert.Equal(4, loaded.Inputs.Count);
        var incomes = loaded.Inputs.Where(input => input.Kind == ConceptKind.Income).OrderBy(input => input.Position).ToList();
        Assert.Equal([500_000.50m, 1_000m], incomes.Select(input => input.Amount!.Value));
        Assert.Equal([0, 1], incomes.Select(input => input.Position));
        Assert.Equal([true, false], incomes.Select(input => input.SalaryConstitutive!.Value));
        Assert.Equal(4.5m, loaded.Inputs.Single(input => input.Kind == ConceptKind.Overtime).Hours);
        Assert.Equal(2, loaded.Events.Count);
        var liquidated = loaded.Events.Single(evt => evt.Action == PeriodAction.Liquidated);
        Assert.Equal(PayrollPeriodStatus.Draft, liquidated.FromStatus);
        Assert.Equal(PayrollPeriodStatus.Liquidated, liquidated.ToStatus);
        Assert.True(liquidated.AbsencesConfirmed);
        Assert.Equal("Ana Gómez", liquidated.ActorName);
    }

    [Fact]
    public async Task EmployeeResultsRoundTripAndAreReplacedInPlace()
    {
        var tenant = Guid.NewGuid();
        var period = NewPeriod(tenant);
        await AddAsync(period);
        var ana = Guid.NewGuid();
        var beto = Guid.NewGuid();

        await InScopeAsync(async provider =>
        {
            await provider.GetRequiredService<IPayrollResultStore>()
                .ReplaceAllAsync(tenant, period.Id, [ResultOf(ana, "Ana Pérez", 875_453m), ResultOf(beto, "Beto Ruiz", 800_000m)], Ct);
            await SaveAsync(provider);
        });
        await InScopeAsync(async provider =>
        {
            // Ana vuelve a salir con otro monto y Beto ya no está en el resultado.
            await provider.GetRequiredService<IPayrollResultStore>()
                .ReplaceAllAsync(tenant, period.Id, [ResultOf(ana, "Ana Pérez", 900_000m)], Ct);
            await SaveAsync(provider);
        });

        var listed = await InScopeAsync(provider => provider.GetRequiredService<IPayrollResultStore>().ListAsync(tenant, period.Id, Ct));
        var other = await InScopeAsync(provider => provider.GetRequiredService<IPayrollResultStore>().ListAsync(Guid.NewGuid(), period.Id, Ct));
        var found = await InScopeAsync(provider => provider.GetRequiredService<IPayrollResultStore>().FindAsync(tenant, period.Id, ana, Ct));

        var result = Assert.Single(listed);
        Assert.Empty(other);
        Assert.NotNull(found);
        Assert.Equal(ana, result.EmployeeId);
        Assert.Equal("Ana Pérez", result.FullName);
        Assert.Equal("Bodega - Principal", result.Workplace);
        Assert.Equal("fp-Ana Pérez", result.Fingerprint);
        Assert.Equal(100_000m, result.RecurringApplied[Commissions]);
        var liquidation = result.Liquidation;
        Assert.Equal(15, liquidation.WorkedDays);
        Assert.True(liquidation.FinalSettlementPending);
        Assert.Equal("2026.11.1", liquidation.EngineVersion);
        Assert.Equal(15, liquidation.Month.WorkedDays);
        Assert.Equal(2, liquidation.Lines.Count);
        var salary = liquidation.Lines[0];
        Assert.Equal("salary", salary.Code);
        Assert.Equal(PayrollLineGroup.Earning, salary.Group);
        Assert.Equal(900_000m, salary.Amount);
        Assert.Equal("salario × días ÷ 30", salary.Trace.Formula);
        Assert.Equal("Smmlv", salary.Trace.LegalValues[0].Key);
        Assert.Equal("Mensual", salary.Trace.Inputs[1].Value.ToString());
        Assert.Equal("Exonerado", liquidation.Lines[1].Trace.Note);
        Assert.Equal(900_000m - 35_000m, liquidation.Summary.Net);
    }

    [Fact]
    public async Task RecurringAppliedIsSummedOverTheNonDraftPeriodsBeforeADate()
    {
        var tenant = Guid.NewGuid();
        var employee = Guid.NewGuid();
        var draft = NewPeriod(tenant);
        var liquidated = PayrollPeriod.Open(
            PayrollPeriodId.New(), tenant, _world.CompanyId, (new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 30), PayrollHalf.Second), Ana, Now);
        liquidated.RecordLiquidation(Ana, true, "{}", new DateOnly(2026, 11, 16), "2026.11.1", "fp", "[]", Now);
        await AddAsync(draft);
        await AddAsync(liquidated);
        await InScopeAsync(async provider =>
        {
            var store = provider.GetRequiredService<IPayrollResultStore>();
            await store.ReplaceAllAsync(tenant, draft.Id, [ResultOf(employee, "Ana Pérez", 1m)], Ct);
            await store.ReplaceAllAsync(tenant, liquidated.Id, [ResultOf(employee, "Ana Pérez", 1m)], Ct);
            await SaveAsync(provider);
        });

        var all = await InScopeAsync(provider => provider.GetRequiredService<IPayrollResultStore>()
            .SumRecurringAppliedAsync(tenant, _world.CompanyId, employee, null, Ct));
        var before = await InScopeAsync(provider => provider.GetRequiredService<IPayrollResultStore>()
            .SumRecurringAppliedAsync(tenant, _world.CompanyId, employee, new DateOnly(2026, 11, 16), Ct));

        // El borrador no cuenta; la segunda quincena sí, salvo que se pida lo anterior a ella.
        Assert.Equal(100_000m, all[Commissions]);
        Assert.Empty(before);
    }

    [Fact]
    public async Task TwoPeriodsCannotShareAStart()
    {
        var tenant = Guid.NewGuid();
        await AddAsync(NewPeriod(tenant));

        var exception = await Assert.ThrowsAsync<ResourceConflictException>(() => AddAsync(NewPeriod(tenant)));

        Assert.Equal(PayrollErrorCodes.PeriodAlreadyExists, exception.Code);
        // El mismo inicio en otra empresa o en otro tenant sí vale.
        await AddAsync(NewPeriod(Guid.NewGuid()));
    }

    [Fact]
    public async Task AStaleVersionIsAConcurrencyConflict()
    {
        var tenant = Guid.NewGuid();
        var period = NewPeriod(tenant);
        await AddAsync(period);

        await using var firstScope = _world.Factory.Services.CreateAsyncScope();
        await using var secondScope = _world.Factory.Services.CreateAsyncScope();
        var first = (await firstScope.ServiceProvider.GetRequiredService<IPayrollPeriodRepository>().FindAsync(tenant, period.Id, Ct))!;
        var second = (await secondScope.ServiceProvider.GetRequiredService<IPayrollPeriodRepository>().FindAsync(tenant, period.Id, Ct))!;
        first.ReplaceInputs(Guid.NewGuid(), [new PeriodInputDraft(Commissions, ConceptKind.Income, null, 1m, true)], Ana, Now);
        second.ReplaceInputs(Guid.NewGuid(), [new PeriodInputDraft(Commissions, ConceptKind.Income, null, 2m, true)], Ana, Now);

        await SaveAsync(firstScope.ServiceProvider);
        var exception = await Assert.ThrowsAsync<RequestConcurrencyException>(() => SaveAsync(secondScope.ServiceProvider));

        Assert.Equal("concurrency.conflict", exception.Code);
        var loaded = await LoadAsync(tenant, period.Id);
        Assert.Equal(2, loaded!.Version);
        Assert.Equal(1m, Assert.Single(loaded.Inputs).Amount);
    }

    [Fact]
    public async Task APeriodOfAnUnknownCompanyViolatesTheForeignKey()
    {
        var period = NewPeriod(Guid.NewGuid(), companyId: Guid.NewGuid());

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => AddAsync(period));

        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal("FK_periods_companies_company_id", postgres.ConstraintName);
    }

    [Fact]
    public async Task ASettingsRowOfAnUnknownCompanyViolatesTheForeignKey()
    {
        var settings = CompanyPayrollSettings.Create(
            CompanyPayrollSettingsId.New(), Guid.NewGuid(), Guid.NewGuid(),
            TransportFrequency.Biweekly, TransportCapBase.SalaryOnly, OvertimeBase.SalaryOnly, true, false, Now);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => InScopeAsync(async provider =>
        {
            provider.GetRequiredService<ICompanyPayrollSettingsRepository>().Add(settings);
            await SaveAsync(provider);
        }));

        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal("FK_company_settings_companies_company_id", postgres.ConstraintName);
    }

    [Fact]
    public async Task AVacationRoundTripsWithItsExternalMonths()
    {
        var tenant = Guid.NewGuid();
        var employee = Guid.NewGuid();
        ExternalVacationMonth[] months =
        [
            new(new DateOnly(2026, 11, 1), 400_000.50m, 16_000m, 16_000m, 0m),
            new(new DateOnly(2026, 12, 1), null, 0m, 0m, 1_000m),
        ];
        var external = NewVacation(
            tenant, employee, new DateOnly(2026, 11, 23), new DateOnly(2026, 12, 4), VacationOrigin.External, 2_800_000.25m, months);
        var qep = NewVacation(tenant, employee, new DateOnly(2027, 1, 12), new DateOnly(2027, 2, 1));
        await AddAsync(external);
        await AddAsync(qep);

        var listed = await InScopeAsync(provider => provider.GetRequiredService<IVacationRepository>()
            .ListByEmployeeAsync(tenant, _world.CompanyId, employee, Ct));
        var found = await InScopeAsync(provider => provider.GetRequiredService<IVacationRepository>().FindAsync(tenant, external.Id, Ct));
        var otherTenant = await InScopeAsync(provider => provider.GetRequiredService<IVacationRepository>().FindAsync(Guid.NewGuid(), external.Id, Ct));

        Assert.Equal(2, listed.Count);
        Assert.Null(otherTenant);
        Assert.NotNull(found);
        Assert.Equal(VacationOrigin.External, found.Origin);
        Assert.Equal((new DateOnly(2026, 11, 23), new DateOnly(2026, 12, 4)), (found.StartDate, found.EndDate));
        Assert.Equal(12, found.BusinessDays);
        Assert.Equal(new DateOnly(2026, 12, 5), found.ReturnDate);
        Assert.Equal(2_800_000.25m, found.PreviousMonthIbc);
        Assert.Equal(1, found.Version);
        Assert.Equal(months, found.ExternalMonths);
        Assert.Empty(listed.Single(item => item.Id == qep.Id).ExternalMonths);
        Assert.Null(listed.Single(item => item.Id == qep.Id).PreviousMonthIbc);
    }

    [Fact]
    public async Task AnUpdateAndARemoveAreSaved()
    {
        var tenant = Guid.NewGuid();
        var employee = Guid.NewGuid();
        var vacation = NewVacation(tenant, employee, new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5));
        await AddAsync(vacation);

        await InScopeAsync(async provider =>
        {
            var tracked = (await provider.GetRequiredService<IVacationRepository>().FindAsync(tenant, vacation.Id, Ct))!;
            tracked.Update(VacationOrigin.Qep, tracked.StartDate, new DateOnly(2027, 1, 8), 17, new DateOnly(2027, 1, 12), 2_900_000m, [], Now.AddHours(1));
            await SaveAsync(provider);
        });
        var updated = await InScopeAsync(provider => provider.GetRequiredService<IVacationRepository>().FindAsync(tenant, vacation.Id, Ct));
        await InScopeAsync(async provider =>
        {
            var repository = provider.GetRequiredService<IVacationRepository>();
            repository.Remove((await repository.FindAsync(tenant, vacation.Id, Ct))!);
            await SaveAsync(provider);
        });
        var removed = await InScopeAsync(provider => provider.GetRequiredService<IVacationRepository>().FindAsync(tenant, vacation.Id, Ct));

        Assert.Equal(2, updated!.Version);
        Assert.Equal(new DateOnly(2027, 1, 8), updated.EndDate);
        Assert.Equal(2_900_000m, updated.PreviousMonthIbc);
        Assert.Null(removed);
    }

    [Fact]
    public async Task OverlappingListsTheVacationsThatShareAnyDayOfTheRange()
    {
        var tenant = Guid.NewGuid();
        var one = NewVacation(tenant, Guid.NewGuid(), new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5));
        var other = NewVacation(tenant, Guid.NewGuid(), new DateOnly(2027, 1, 12), new DateOnly(2027, 2, 1));
        await AddAsync(one);
        await AddAsync(other);

        var december = await InScopeAsync(provider => provider.GetRequiredService<IVacationRepository>()
            .ListOverlappingAsync(tenant, _world.CompanyId, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 15), Ct));
        var touchingTheEnd = await InScopeAsync(provider => provider.GetRequiredService<IVacationRepository>()
            .ListOverlappingAsync(tenant, _world.CompanyId, new DateOnly(2027, 1, 5), new DateOnly(2027, 1, 11), Ct));
        var nothing = await InScopeAsync(provider => provider.GetRequiredService<IVacationRepository>()
            .ListOverlappingAsync(tenant, _world.CompanyId, new DateOnly(2027, 1, 6), new DateOnly(2027, 1, 11), Ct));
        var otherTenant = await InScopeAsync(provider => provider.GetRequiredService<IVacationRepository>()
            .ListOverlappingAsync(Guid.NewGuid(), _world.CompanyId, new DateOnly(2026, 12, 1), new DateOnly(2027, 3, 1), Ct));

        Assert.Equal([one.Id], december.Select(item => item.Id));
        Assert.Equal([one.Id], touchingTheEnd.Select(item => item.Id));
        Assert.Empty(nothing);
        Assert.Empty(otherTenant);
    }

    [Fact]
    public async Task TwoOverlappingVacationsOfTheSameEmployeeConflict()
    {
        var tenant = Guid.NewGuid();
        var employee = Guid.NewGuid();
        await AddAsync(NewVacation(tenant, employee, new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5)));

        // The handler's check cannot see a concurrent insert: the database constraint is the one that stops it (Review Focus 5).
        var exception = await Assert.ThrowsAsync<ResourceConflictException>(
            () => AddAsync(NewVacation(tenant, employee, new DateOnly(2027, 1, 5), new DateOnly(2027, 1, 10))));

        Assert.Equal(PayrollErrorCodes.VacationOverlap, exception.Code);
        // The day after the end does not overlap.
        await AddAsync(NewVacation(tenant, employee, new DateOnly(2027, 1, 6), new DateOnly(2027, 1, 10)));
    }

    [Fact]
    public async Task ADifferentEmployeeDoesNotConflict()
    {
        var tenant = Guid.NewGuid();
        await AddAsync(NewVacation(tenant, Guid.NewGuid(), new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5)));

        await AddAsync(NewVacation(tenant, Guid.NewGuid(), new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5)));
        await AddAsync(NewVacation(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5)));
    }

    [Fact]
    public async Task AVacationOfAnUnknownCompanyViolatesTheForeignKey()
    {
        var vacation = Vacation.Create(
            VacationId.New(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), VacationOrigin.Qep,
            new DateOnly(2026, 12, 14), new DateOnly(2027, 1, 5), 15, new DateOnly(2027, 1, 6), null, [], Now);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => AddAsync(vacation));

        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal("FK_vacations_companies_company_id", postgres.ConstraintName);
    }

    [Fact]
    public async Task AResultKeepsItsVacationDaysAndPhotos()
    {
        var tenant = Guid.NewGuid();
        var period = NewPeriod(tenant);
        await AddAsync(period);
        var employee = Guid.NewGuid();
        var vacationId = Guid.NewGuid();
        VacationPhoto[] photos =
        [
            new(
                vacationId, 3_000_000m, new DateOnly(2026, 1, 15), 2_800_000m, VacationIbcSource.Typed,
                [
                    new VacationQuincenaDays(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 15), 2),
                    new VacationQuincenaDays(new DateOnly(2026, 12, 16), new DateOnly(2026, 12, 31), 9),
                ],
                [new VacationMonthAdvance(new DateOnly(2026, 12, 1), 900_000m, 36_000m, 36_000m, 0m)]),
        ];

        await InScopeAsync(async provider =>
        {
            await provider.GetRequiredService<IPayrollResultStore>()
                .ReplaceAllAsync(tenant, period.Id, [ResultOf(employee, "Ana Pérez", 875_453m, vacationDays: 2, photos: photos)], Ct);
            await SaveAsync(provider);
        });
        var read = await InScopeAsync(provider => provider.GetRequiredService<IPayrollResultStore>().FindAsync(tenant, period.Id, employee, Ct));

        // Block A left `0, []` placeholders when the row is read: a result with days and a photo must come back identical.
        Assert.NotNull(read);
        Assert.Equal(2, read.Liquidation.VacationDays);
        var photo = Assert.Single(read.Liquidation.VacationPhotos);
        Assert.Equal(vacationId, photo.VacationId);
        Assert.Equal(3_000_000m, photo.SalaryAtStart);
        Assert.Equal(new DateOnly(2026, 1, 15), photo.SalaryRecordDate);
        Assert.Equal(2_800_000m, photo.PreviousMonthIbc);
        Assert.Equal(VacationIbcSource.Typed, photo.PreviousIbcSource);
        Assert.Equal(
            [(new DateOnly(2026, 12, 1), 2), (new DateOnly(2026, 12, 16), 9)],
            photo.Quincenas.Select(item => (item.Start, item.Days)));
        var month = Assert.Single(photo.Months);
        Assert.Equal(new VacationMonthAdvance(new DateOnly(2026, 12, 1), 900_000m, 36_000m, 36_000m, 0m), month);
    }

    [Fact]
    public async Task AResultSavedBeforeVacationsReadsZeroAndNoPhotos()
    {
        var tenant = Guid.NewGuid();
        var period = NewPeriod(tenant);
        await AddAsync(period);
        var employee = Guid.NewGuid();
        await InScopeAsync(async provider =>
        {
            await provider.GetRequiredService<IPayrollResultStore>()
                .ReplaceAllAsync(tenant, period.Id, [ResultOf(employee, "Ana Pérez", 875_453m)], Ct);
            await SaveAsync(provider);
        });
        // The row as a pod from before this change wrote it: the new columns at their defaults and a summary without vacationPay.
        await InScopeAsync(async provider =>
        {
            var context = provider.GetRequiredService<Modules.Payroll.Infrastructure.Persistence.PayrollDbContext>();
            await context.Database.ExecuteSqlAsync(
                $"UPDATE payroll.employee_results SET vacation_photos = '[]'::jsonb, vacation_days = 0, summary = summary - 'vacationPay' WHERE period_id = {period.Id.Value}",
                Ct);
        });

        var read = await InScopeAsync(provider => provider.GetRequiredService<IPayrollResultStore>().FindAsync(tenant, period.Id, employee, Ct));

        Assert.NotNull(read);
        Assert.Equal(0, read.Liquidation.VacationDays);
        Assert.Empty(read.Liquidation.VacationPhotos);
        Assert.Equal(0m, read.Liquidation.Summary.VacationPay);
    }
}
