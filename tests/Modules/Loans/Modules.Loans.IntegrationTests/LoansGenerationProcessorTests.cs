using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Loans.Application;
using Modules.Loans.Domain;
using Modules.Loans.Infrastructure.Persistence;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;
using static Modules.Loans.IntegrationTests.LoansApiHarness;

namespace Modules.Loans.IntegrationTests;

/// <summary>
/// La generación diaria de cuotas de un tenant contra Postgres real: la deduplicación vive en un índice único y sólo la base la
/// prueba. El reloj del tenant es movible (America/Bogota, UTC-5 sin horario de verano) y cada prueba lo fija: nada depende del
/// día en que corre.
/// </summary>
public sealed class LoansGenerationProcessorTests : IAsyncLifetime
{
    private static readonly Guid Tenant = Guid.Parse(LoansApiHarness.TenantId);

    // Todo DateTimeOffset que viaja a Npgsql va en UTC. 00:05 locales del 2026-11-01: el período de octubre ya cerró.
    private static readonly DateTimeOffset FirstOfNovember = new(2026, 11, 1, 5, 5, 0, TimeSpan.Zero);

    private readonly MutableTenantClock clock = new(FirstOfNovember);
    private readonly InstallmentWrites writes = new();
    private PostgreSqlContainer database = null!;
    private QepApiFactory factory = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        database = await StartDatabaseAsync();
        factory = new QepApiFactory(database.GetConnectionString(), services =>
        {
            services.RemoveAll<ITenantClock>();
            services.AddSingleton<ITenantClock>(clock);

            // Transparente salvo que una prueba arme `writes`: ahí mete la escritura de «otra réplica», o lanza, justo antes de que
            // la generación agregue sus cuotas, el único hueco en que IX_installments_loan_period puede chocar.
            var installments = services.Single(descriptor => descriptor.ServiceType == typeof(ILoanInstallmentRepository));
            services.Remove(installments);
            services.AddScoped<ILoanInstallmentRepository>(provider => new InterceptingInstallmentRepository(
                (ILoanInstallmentRepository)ActivatorUtilities.CreateInstance(provider, installments.ImplementationType!), writes));
        });
        await SeedTenantAsync(factory);
    }

    public async ValueTask DisposeAsync()
    {
        await factory.DisposeAsync();
        await database.DisposeAsync();
    }

    [Fact]
    public async Task AClosedPeriodBecomesItsInstallmentOnce()
    {
        var loan = await SeedLoanAsync("2026-10-01");

        await ProcessAsync();
        await ProcessAsync();

        var installment = Assert.Single(await InstallmentsOfAsync(loan));
        Assert.Equal((Date("2026-10-01"), Date("2026-10-31")), (installment.PeriodStart, installment.PeriodEnd));
        Assert.Equal(Date("2026-10-31"), (await LoanAsync(loan)).LastGeneratedPeriodEnd);
        Assert.NotNull(Assert.Single(await RunsAsync()).FinishedAt);
    }

    [Fact]
    public async Task APeriodWhoseInterestRoundsToZeroIsMarkedGeneratedWithoutAnInstallment()
    {
        // 0,01 al 1,5 % mensual causa 0,00015: sin cuota, pero el avance del período tiene que persistirse o el préstamo
        // quedaría «cerrándose» para siempre y bloquearía los abonos.
        var loan = await SeedLoanAsync("2026-10-01", principal: 0.01m);

        await ProcessAsync();

        Assert.Empty(await InstallmentsOfAsync(loan));
        Assert.Equal(Date("2026-10-31"), (await LoanAsync(loan)).LastGeneratedPeriodEnd);
        using var manager = CreateManager(factory);
        var response = await manager.GetAsync($"{LoansUrl()}/{loan.Value}", Ct);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var detail = System.Text.Json.JsonDocument.Parse(await BodyAsync(response));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, detail.RootElement.GetProperty("capitalPaymentBlock").ValueKind);
    }

    /// <summary>Review Focus 1: la cuota se genera el día siguiente al corte, no el último día del período.</summary>
    [Fact]
    public async Task APeriodIsNotGeneratedOnItsLastDay()
    {
        var loan = await SeedLoanAsync("2026-10-01");
        clock.UtcNow = new DateTimeOffset(2026, 11, 1, 4, 50, 0, TimeSpan.Zero); // 23:50 del 2026-10-31 en Bogotá

        await ProcessAsync();

        Assert.Empty(await InstallmentsOfAsync(loan));
        Assert.Null((await LoanAsync(loan)).LastGeneratedPeriodEnd);
    }

    [Fact]
    public async Task ADownWorkerCatchesUpEveryMissedPeriod()
    {
        var loan = await SeedLoanAsync("2026-10-01");
        clock.UtcNow = new DateTimeOffset(2027, 1, 1, 5, 5, 0, TimeSpan.Zero); // 00:05 del 2027-01-01 en Bogotá

        await ProcessAsync();

        Assert.Equal(
            [
                (Date("2026-10-01"), Date("2026-10-31")),
                (Date("2026-11-01"), Date("2026-11-30")),
                (Date("2026-12-01"), Date("2026-12-31")),
            ],
            (await InstallmentsOfAsync(loan)).Select(installment => (installment.PeriodStart, installment.PeriodEnd)));
        Assert.Equal(Date("2026-12-31"), (await LoanAsync(loan)).LastGeneratedPeriodEnd);
    }

    [Fact]
    public async Task ALoanGeneratedByAnotherReplicaIsNotDuplicated()
    {
        var loan = await SeedLoanAsync("2026-10-01");
        writes.ArmReplicaClash(database.GetConnectionString());

        await ProcessAsync();

        // Una sola cuota —la de la otra réplica— y sin excepción: el choque se relee y no hay nada más que generar.
        var installment = Assert.Single(await InstallmentsOfAsync(loan));
        Assert.Equal(Date("2026-10-01"), installment.PeriodStart);
        Assert.Equal(Date("2026-10-31"), (await LoanAsync(loan)).LastGeneratedPeriodEnd);
        Assert.NotNull(Assert.Single(await RunsAsync()).FinishedAt);
    }

    [Fact]
    public async Task AFailedLoanDoesNotStopTheOthers()
    {
        // El que falla es el más antiguo: ListAccruingAsync ordena por desembolso, así que corre primero.
        var failing = await SeedLoanAsync("2026-09-01");
        var healthy = await SeedLoanAsync("2026-10-01");
        writes.FailFor(failing);

        await ProcessAsync();

        Assert.Empty(await InstallmentsOfAsync(failing));
        Assert.Null((await LoanAsync(failing)).LastGeneratedPeriodEnd);
        Assert.Single(await InstallmentsOfAsync(healthy));
        Assert.NotNull(Assert.Single(await RunsAsync()).FinishedAt);
    }

    /// <summary>Review Focus 5: sin capital pendiente no hay interés que cobrar, y el préstamo ni siquiera entra a la corrida.</summary>
    [Fact]
    public async Task ALoanWithoutCapitalGeneratesNothing()
    {
        var loan = await SeedLoanAsync("2026-10-01", outstanding: 0m);

        await ProcessAsync();

        Assert.Empty(await InstallmentsOfAsync(loan));
        Assert.Null((await LoanAsync(loan)).LastGeneratedPeriodEnd);
    }

    [Fact]
    public async Task ASuspendedTenantGeneratesNothing()
    {
        var loan = await SeedLoanAsync("2026-10-01");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            var tenant = await tenancy.Tenants.FindAsync([new Modules.Tenancy.Domain.TenantId(Tenant)], Ct);
            tenant!.Suspend(ChangeReason.Nonpayment, FirstOfNovember);
            await tenancy.SaveChangesAsync(Ct);
        }

        await ProcessAsync();

        Assert.Empty(await InstallmentsOfAsync(loan));
        Assert.Empty(await RunsAsync());
    }

    /// <summary>
    /// El módulo apagado con préstamos ya creados no genera ni reclama el día: como no queda fila de <c>tenant_runs</c>, la corrida
    /// que sigue a prenderlo recupera el período con su fecha real.
    /// </summary>
    [Fact]
    public async Task WithTheModuleOffNothingIsGeneratedAndTheNextRunCatchesUp()
    {
        var loan = await SeedLoanAsync("2026-10-01");
        await ExecuteSqlAsync("DELETE FROM tenancy.tenant_modules WHERE tenant_id = '" + LoansApiHarness.TenantId + "' AND module_key = 'loans';");

        await ProcessAsync();

        Assert.Empty(await InstallmentsOfAsync(loan));
        Assert.Empty(await RunsAsync());

        await SeedTenantAsync(factory, modules: [TenantModuleKeys.Loans]);
        await ProcessAsync();

        var installment = Assert.Single(await InstallmentsOfAsync(loan));
        Assert.Equal((Date("2026-10-01"), Date("2026-10-31")), (installment.PeriodStart, installment.PeriodEnd));
    }

    [Fact]
    public async Task GenerationSendsNothing()
    {
        var loan = await SeedLoanAsync("2026-10-01");
        var before = await OutboxCountAsync();

        await ProcessAsync();

        Assert.Single(await InstallmentsOfAsync(loan));
        Assert.Equal(before, await OutboxCountAsync());
    }

    private static DateOnly Date(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private async Task ProcessAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ILoansGenerationProcessor>().ProcessTenantAsync(Tenant, Ct);
    }

    private async Task<T> WithDbAsync<T>(Func<LoansDbContext, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<LoansDbContext>());
    }

    private Task<int> ExecuteSqlAsync(string sql) => WithDbAsync(db => db.Database.ExecuteSqlRawAsync(sql, Ct));

    private Task<long> OutboxCountAsync() =>
        WithDbAsync(db => db.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM platform.outbox_messages").SingleAsync(Ct));

    private Task<List<TenantRun>> RunsAsync() => WithDbAsync(db => db.TenantRuns.AsNoTracking().ToListAsync(Ct));

    private Task<List<LoanInstallment>> InstallmentsOfAsync(LoanId loanId) =>
        WithDbAsync(db => db.Installments.AsNoTracking().Where(i => i.LoanId == loanId).OrderBy(i => i.PeriodStart).ToListAsync(Ct));

    private Task<Loan> LoanAsync(LoanId loanId) => WithDbAsync(db => db.Loans.AsNoTracking().SingleAsync(l => l.Id == loanId, Ct));

    /// <summary>
    /// Un préstamo de sólo interés de 100.000.000 al 1,5 % que cierra a fin de mes. Se crea «hoy» = su desembolso (la fecha de la
    /// prueba no importa para sembrar) y, con <paramref name="outstanding"/> en cero, queda sin capital como tras un abono total.
    /// </summary>
    private Task<LoanId> SeedLoanAsync(string disbursedOn, decimal? outstanding = null, decimal principal = 100_000_000m) =>
        WithDbAsync(async db =>
        {
            var now = new DateTimeOffset(2026, 10, 31, 15, 0, 0, TimeSpan.Zero);
            var terms = new LoanTerms(
                Guid.NewGuid(), principal, "COP", 1.5m, Date(disbursedOn), LoanCutoffRule.EndOfMonth);
            var loan = Loan.Create(LoanId.New(), Tenant, terms, "Novasteel", null, null, Date(disbursedOn), now);
            if (outstanding is { } remaining)
            {
                loan.SetOutstanding(remaining, now);
            }

            db.Loans.Add(loan);
            await db.SaveChangesAsync(Ct);
            return loan.Id;
        });

    /// <summary>
    /// Decorador de <see cref="ILoanInstallmentRepository"/>: justo antes de que la generación agregue sus cuotas escribe, en otra
    /// conexión y como lo haría otra réplica, la cuota del primer período y avanza el préstamo hasta él sin tocar su versión; así
    /// el SaveChanges de esta réplica choca con IX_installments_loan_period y no con el token. O lanza para un préstamo concreto.
    /// </summary>
    private sealed class InterceptingInstallmentRepository(ILoanInstallmentRepository inner, InstallmentWrites writes)
        : ILoanInstallmentRepository
    {
        public void AddRange(IEnumerable<LoanInstallment> installments)
        {
            var list = installments.ToList();
            writes.BeforeAdd(list);
            inner.AddRange(list);
        }

        public Task<LoanInstallment?> FindAsync(Guid tenantId, LoanId loanId, Guid installmentId, CancellationToken cancellationToken) =>
            inner.FindAsync(tenantId, loanId, installmentId, cancellationToken);

        public Task<IReadOnlyList<LoanInstallment>> ListByLoanAsync(Guid tenantId, LoanId loanId, CancellationToken cancellationToken) =>
            inner.ListByLoanAsync(tenantId, loanId, cancellationToken);

        public Task<int> CountPendingAsync(Guid tenantId, LoanId loanId, CancellationToken cancellationToken) =>
            inner.CountPendingAsync(tenantId, loanId, cancellationToken);

        public Task<IReadOnlyDictionary<LoanId, PendingTotal>> PendingTotalsAsync(
            Guid tenantId, IReadOnlyCollection<LoanId> loanIds, CancellationToken cancellationToken) =>
            inner.PendingTotalsAsync(tenantId, loanIds, cancellationToken);

        public Task<(IReadOnlyList<LoanInstallment> Items, int Total)> ListPendingAsync(
            PendingInstallmentFilter filter, CancellationToken cancellationToken) =>
            inner.ListPendingAsync(filter, cancellationToken);

        public Task<InstallmentTotals> PendingSummaryAsync(Guid tenantId, CancellationToken cancellationToken) =>
            inner.PendingSummaryAsync(tenantId, cancellationToken);
    }

    /// <summary>Lo que el decorador hace antes de agregar: nada, la escritura de «la otra réplica» (una sola vez) o lanzar.</summary>
    private sealed class InstallmentWrites
    {
        private string? connectionString;
        private LoanId? failingLoan;

        public void ArmReplicaClash(string connection) => connectionString = connection;

        public void FailFor(LoanId loanId) => failingLoan = loanId;

        public void BeforeAdd(List<LoanInstallment> installments)
        {
            if (installments.Count == 0)
            {
                return;
            }

            if (failingLoan is { } failing && installments[0].LoanId == failing)
            {
                throw new InvalidOperationException("Simulated failure while saving the installments of one loan.");
            }

            if (Interlocked.Exchange(ref connectionString, null) is not { } connection)
            {
                return;
            }

            var first = installments.MinBy(installment => installment.PeriodStart)!;
            using var npgsql = new NpgsqlConnection(connection);
            npgsql.Open();
            using var insert = new NpgsqlCommand(
                """
                INSERT INTO loans.installments (id, tenant_id, loan_id, period_start, period_end, period_days, amount, currency, status, is_imported, version, created_at)
                VALUES (@id, @tenant, @loan, @start, @end, @days, @amount, @currency, 'Pending', false, 1, now());
                UPDATE loans.loans SET last_generated_period_end = @end WHERE id = @loan;
                """,
                npgsql);
            insert.Parameters.AddWithValue("id", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("tenant", first.TenantId);
            insert.Parameters.AddWithValue("loan", first.LoanId.Value);
            insert.Parameters.AddWithValue("start", first.PeriodStart);
            insert.Parameters.AddWithValue("end", first.PeriodEnd);
            insert.Parameters.AddWithValue("days", first.PeriodDays ?? 31);
            insert.Parameters.AddWithValue("amount", first.Amount);
            insert.Parameters.AddWithValue("currency", first.Currency);
            insert.ExecuteNonQuery();
        }
    }
}
