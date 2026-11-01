using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Loans.Application;
using Modules.Loans.Domain;
using Modules.Loans.Infrastructure.Persistence;
using Npgsql;
using static Modules.Loans.IntegrationTests.LoansApiHarness;

namespace Modules.Loans.IntegrationTests;

/// <summary>
/// El esquema <c>loans</c>: el ida y vuelta del agregado, las unicidades que sostienen la generación de cuotas,
/// los CHECK que la base impone aunque el dominio ya valide y los totales por moneda.
/// </summary>
public sealed class LoansPersistenceTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);
    private static readonly Guid OtherTenant = Guid.Parse(OtherTenantId);
    private static readonly DateOnly Today = new(2026, 3, 5);
    private static readonly DateTimeOffset Now = new(2026, 3, 5, 15, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ALoanRoundTripsWithItsInstallmentSegmentsAndPayments()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var loan = NewLoan(Tenant);
        var installments = LoanInstallmentGenerator.GenerateClosedPeriods(loan, [], Today, Now);
        var payment = LoanCapitalPayment.Create(Tenant, loan.Id, 100_000m, new DateOnly(2026, 3, 2), "abono", Now);
        payment.SetBalanceAfter(900_000m);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ILoanRepository>().Add(loan);
            scope.ServiceProvider.GetRequiredService<ILoanInstallmentRepository>().AddRange(installments);
            scope.ServiceProvider.GetRequiredService<ILoanCapitalPaymentRepository>().Add(payment);
            await scope.ServiceProvider.GetRequiredService<ILoansUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var found = await read.ServiceProvider.GetRequiredService<ILoanRepository>().FindAsync(Tenant, loan.Id, Ct);
        var stored = await read.ServiceProvider.GetRequiredService<ILoanInstallmentRepository>().ListByLoanAsync(Tenant, loan.Id, Ct);
        var payments = await read.ServiceProvider.GetRequiredService<ILoanCapitalPaymentRepository>().ListByLoanAsync(Tenant, loan.Id, Ct);

        Assert.NotNull(found);
        Assert.Equal(loan.Name, found.Name);
        Assert.Equal(loan.Version, found.Version);
        Assert.Equal(loan.LastGeneratedPeriodEnd, found.LastGeneratedPeriodEnd);
        Assert.Equal(LoanCutoffRule.EndOfMonth, found.CutoffRule);
        Assert.Equal(2, stored.Count);
        // Del más reciente al más viejo.
        Assert.Equal(installments.Select(value => value.PeriodStart).Order().Reverse(), stored.Select(value => value.PeriodStart));
        foreach (var expected in installments)
        {
            var actual = stored.Single(value => value.Id == expected.Id);
            Assert.Equal(expected.Amount, actual.Amount);
            Assert.Equal(expected.PeriodDays, actual.PeriodDays);
            Assert.Equal(
                expected.Segments.Select(segment => (segment.Ordinal, segment.StartOn, segment.EndOn, segment.Principal, segment.Days, segment.Interest)),
                actual.Segments.OrderBy(segment => segment.Ordinal)
                    .Select(segment => (segment.Ordinal, segment.StartOn, segment.EndOn, segment.Principal, segment.Days, segment.Interest)));
        }

        var storedPayment = Assert.Single(payments);
        Assert.Equal(900_000m, storedPayment.BalanceAfter);
        Assert.Equal("abono", storedPayment.Note);
    }

    [Fact]
    public async Task TheSameLoanAndPeriodCannotHaveTwoInstallments()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var (loan, first) = await SeedLoanWithFirstInstallmentAsync(factory);

        await using var again = factory.Services.CreateAsyncScope();
        var db = again.ServiceProvider.GetRequiredService<LoansDbContext>();
        db.Installments.Add(DuplicateOf(loan, first));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("IX_installments_loan_period", postgres.ConstraintName);
    }

    [Fact]
    public async Task DeletingALoanCascadesToItsInstallmentsSegmentsAndPayments()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var loan = NewLoan(Tenant);
        var installments = LoanInstallmentGenerator.GenerateClosedPeriods(loan, [], Today, Now);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LoansDbContext>();
            db.Loans.Add(loan);
            db.Installments.AddRange(installments);
            db.CapitalPayments.Add(LoanCapitalPayment.Create(Tenant, loan.Id, 50_000m, new DateOnly(2026, 3, 1), null, Now));
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<ILoanRepository>();
            var tracked = await repository.FindAsync(Tenant, loan.Id, Ct);
            repository.Remove(tracked!);
            await scope.ServiceProvider.GetRequiredService<ILoansUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var context = read.ServiceProvider.GetRequiredService<LoansDbContext>();
        Assert.Empty(await context.Loans.ToListAsync(Ct));
        Assert.Empty(await context.Installments.ToListAsync(Ct));
        Assert.Empty(await context.CapitalPayments.ToListAsync(Ct));
        var segments = await context.Database
            .SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM loans.installment_segments")
            .SingleAsync(Ct);
        Assert.Equal(0, segments);
    }

    [Fact]
    public async Task TheDatabaseRejectsARateOutOfRange()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var loan = NewLoan(Tenant);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LoansDbContext>();
        db.Loans.Add(loan);
        await db.SaveChangesAsync(Ct);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE loans.loans SET monthly_rate_percent = 150 WHERE id = {loan.Id.Value}", Ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_loans_monthly_rate", exception.ConstraintName);
    }

    [Fact]
    public async Task AnImportedInstallmentNeedsNoPeriodDays()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var loan = (await SeedLoanWithFirstInstallmentAsync(factory)).Loan;

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LoansDbContext>();
        await InsertInstallmentAsync(db, loan, new DateOnly(2025, 12, 1), isImported: true);

        var imported = await db.Installments.AsNoTracking().SingleAsync(value => value.PeriodStart == new DateOnly(2025, 12, 1), Ct);
        Assert.True(imported.IsImported);
        Assert.Null(imported.PeriodDays);

        // La contracara: sin importar, los días del período son obligatorios.
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => InsertInstallmentAsync(db, loan, new DateOnly(2025, 11, 1), isImported: false));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_installments_period_days", exception.ConstraintName);
    }

    [Fact]
    public async Task TheUnitOfWorkTranslatesADuplicatePeriodToAConcurrencyConflict()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var (loan, first) = await SeedLoanWithFirstInstallmentAsync(factory);

        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ILoanInstallmentRepository>().AddRange([DuplicateOf(loan, first)]);

        var exception = await Assert.ThrowsAsync<RequestConcurrencyException>(
            () => scope.ServiceProvider.GetRequiredService<ILoansUnitOfWork>().SaveChangesAsync(Ct));
        Assert.Equal("concurrency.conflict", exception.Code);
    }

    [Fact]
    public async Task TotalsNeverMixCurrencies()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var cop = NewLoan(Tenant, "COP", 1_000_000m);
        var usd = NewLoan(Tenant, "USD", 2_000m);
        var copInstallments = LoanInstallmentGenerator.GenerateClosedPeriods(cop, [], Today, Now);
        var usdInstallments = LoanInstallmentGenerator.GenerateClosedPeriods(usd, [], Today, Now);
        await SaveAsync(factory, [cop, usd], [.. copInstallments, .. usdInstallments]);

        await using var scope = factory.Services.CreateAsyncScope();
        var totals = await scope.ServiceProvider.GetRequiredService<ILoanRepository>().TotalsAsync(Tenant, Ct);

        Assert.Equal(2, totals.ActiveCount);
        Assert.Equal(
            [new CurrencyTotal("COP", 1_000_000m), new CurrencyTotal("USD", 2_000m)],
            totals.OutstandingByCurrency);
        Assert.Equal(
            [
                new CurrencyTotal("COP", copInstallments.Sum(value => value.Amount)),
                new CurrencyTotal("USD", usdInstallments.Sum(value => value.Amount)),
            ],
            totals.PendingByCurrency);
    }

    [Fact]
    public async Task TotalsCountThePendingInstallmentsOfTheWholeTenant()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var paidLoan = NewLoan(Tenant);
        var openLoan = NewLoan(Tenant);
        var foreign = NewLoan(OtherTenant);
        var paidInstallments = LoanInstallmentGenerator.GenerateClosedPeriods(paidLoan, [], Today, Now);
        var openInstallments = LoanInstallmentGenerator.GenerateClosedPeriods(openLoan, [], Today, Now);
        var foreignInstallments = LoanInstallmentGenerator.GenerateClosedPeriods(foreign, [], Today, Now);
        paidInstallments.Single(value => value.PeriodStart == new DateOnly(2026, 1, 10))
            .MarkPaid(Today, null, Guid.CreateVersion7(), Today, Now);
        await SaveAsync(
            factory,
            [paidLoan, openLoan, foreign],
            [.. paidInstallments, .. openInstallments, .. foreignInstallments]);

        await using var scope = factory.Services.CreateAsyncScope();
        var loans = scope.ServiceProvider.GetRequiredService<ILoanRepository>();
        var installments = scope.ServiceProvider.GetRequiredService<ILoanInstallmentRepository>();
        var totals = await loans.TotalsAsync(Tenant, Ct);
        var summary = await installments.PendingSummaryAsync(Tenant, Ct);

        // Una pendiente de paidLoan (la de febrero) y las dos de openLoan; ni la pagada ni las de otro tenant.
        Assert.Equal(3, totals.PendingInstallmentCount);
        Assert.Equal(3, summary.PendingCount);
        Assert.Equal(1, await installments.CountPendingAsync(Tenant, paidLoan.Id, Ct));
        var oldest = Assert.IsType<OldestInstallment>(summary.Oldest);
        Assert.Equal(openLoan.Id, oldest.LoanId);
        Assert.Equal(new DateOnly(2026, 1, 31), oldest.PeriodEnd);
    }

    private static Loan NewLoan(Guid tenantId, string currency = "COP", decimal principal = 1_000_000m) =>
        Loan.Create(
            LoanId.New(),
            tenantId,
            new LoanTerms(Guid.CreateVersion7(), principal, currency, 2m, new DateOnly(2026, 1, 10), LoanCutoffRule.EndOfMonth),
            "Préstamo de prueba",
            null,
            null,
            Today,
            Now);

    /// <summary>La misma cuota otra vez: mismo préstamo y mismo inicio de período, ids propios.</summary>
    private static LoanInstallment DuplicateOf(Loan loan, LoanInstallment first) =>
        LoanInstallment.Generate(
            loan,
            first.PeriodStart,
            first.PeriodEnd,
            first.PeriodDays!.Value,
            [new InterestSegment(first.PeriodStart, first.PeriodEnd, loan.PrincipalAmount, first.PeriodDays.Value, 100m)],
            Now);

    private static async Task<(Loan Loan, LoanInstallment First)> SeedLoanWithFirstInstallmentAsync(
        LoansApiHarness.QepApiFactory factory)
    {
        var loan = NewLoan(Tenant);
        var first = LoanInstallmentGenerator.GenerateClosedPeriods(loan, [], Today, Now)[0];
        await SaveAsync(factory, [loan], [first]);
        return (loan, first);
    }

    private static async Task SaveAsync(
        LoansApiHarness.QepApiFactory factory, IEnumerable<Loan> loans, IEnumerable<LoanInstallment> installments)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LoansDbContext>();
        db.Loans.AddRange(loans);
        db.Installments.AddRange(installments);
        await db.SaveChangesAsync(Ct);
    }

    private static Task<int> InsertInstallmentAsync(LoansDbContext db, Loan loan, DateOnly periodStart, bool isImported) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO loans.installments
                (id, tenant_id, loan_id, period_start, period_end, period_days, amount, currency, status, is_imported, version, created_at)
            VALUES
                ({Guid.CreateVersion7()}, {loan.TenantId}, {loan.Id.Value}, {periodStart}, {periodStart.AddDays(29)}, NULL, 1000, 'COP', 'Pending', {isImported}, 1, {Now})
            """,
            Ct);
}
