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

    private static EmployeeResult ResultOf(Guid employeeId, string name, decimal salary)
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
            employeeId, 15, true, false, lines, PayrollSummary.From(lines), MonthAccumulators.Empty with { WorkedDays = 15 }, "2026.11.1");
        return new EmployeeResult(
            employeeId, "CC", "1020304050", name, "Analista", "Bodega - Principal", liquidation, "fp-" + name,
            new Dictionary<Guid, decimal> { [Commissions] = 100_000m });
    }

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
}
