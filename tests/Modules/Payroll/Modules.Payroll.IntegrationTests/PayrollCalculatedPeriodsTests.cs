using Microsoft.Extensions.DependencyInjection;
using Modules.Payroll.Application;
using Modules.Payroll.Domain;
using static Modules.Payroll.IntegrationTests.PayrollApiHarness;
using static Modules.Payroll.IntegrationTests.PayrollApiSeeds;

namespace Modules.Payroll.IntegrationTests;

/// <summary>
/// El puerto de lectura que usa la consola de valores legales del operador (spec 2026-11-22, «Uso»): las fechas con que se resolvieron
/// las nóminas ya calculadas, de todos los tenants.
/// </summary>
public sealed class PayrollCalculatedPeriodsTests : IClassFixture<PayrollApiFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 15, 0, 0, TimeSpan.Zero);
    private static readonly PayrollActor Ana = new(Guid.Parse("01900000-0000-7000-8000-0000000000a1"), "Ana Gómez");

    private static readonly (DateOnly Start, DateOnly End, PayrollHalf Half) First =
        (new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 15), PayrollHalf.First);

    private static readonly (DateOnly Start, DateOnly End, PayrollHalf Half) Second =
        (new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 30), PayrollHalf.Second);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PayrollApiFixture _fixture;

    public PayrollCalculatedPeriodsTests(PayrollApiFixture fixture) => _fixture = fixture;

    private async Task AddAsync(
        Guid tenantId, Guid companyId, (DateOnly Start, DateOnly End, PayrollHalf Half) half, DateOnly? liquidatedWith)
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var period = PayrollPeriod.Open(PayrollPeriodId.New(), tenantId, companyId, half, Ana, Now);
        if (liquidatedWith is { } date)
        {
            period.RecordLiquidation(Ana, true, "{}", date, "2026.11.1", "fp", "[]", Now.AddMinutes(1));
        }

        scope.ServiceProvider.GetRequiredService<IPayrollPeriodRepository>().Add(period);
        await scope.ServiceProvider.GetRequiredService<IPayrollUnitOfWork>().SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task OnlyPeriodsWithLegalValuesDateAcrossTenants()
    {
        using var tenantA = await NewTenantAsync(_fixture);
        using var tenantB = await NewTenantAsync(_fixture);
        var companyA = (await CreateCompanyAsync(tenantA.Admin, tenantA.Id, "Empresa A")).Id;
        var companyB = (await CreateCompanyAsync(tenantB.Admin, tenantB.Id, "Empresa B")).Id;
        await AddAsync(tenantA.Id, companyA, First, new DateOnly(2026, 11, 1));
        await AddAsync(tenantA.Id, companyA, Second, null);
        await AddAsync(tenantB.Id, companyB, Second, new DateOnly(2026, 11, 16));
        await AddAsync(tenantB.Id, companyB, First, null);

        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var stamps = await scope.ServiceProvider.GetRequiredService<IPayrollCalculatedPeriods>()
            .ListOfAllTenantsAsync(Ct);

        var mine = stamps.Where(stamp => stamp.CompanyId == companyA || stamp.CompanyId == companyB).ToList();
        Assert.Equal(2, mine.Count);
        Assert.Contains(new PayrollCalculatedPeriod(new DateOnly(2026, 11, 1), companyA), mine);
        Assert.Contains(new PayrollCalculatedPeriod(new DateOnly(2026, 11, 16), companyB), mine);
    }
}
