using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Hr.Application;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;
using Testcontainers.PostgreSql;
using static Modules.Hr.IntegrationTests.HrApiHarness;

namespace Modules.Hr.IntegrationTests;

/// <summary>La migración <c>AddPayrollLegalParameters</c> (spec 2026-11-21, R-1): el conjunto sembrado para nómina, leído de la base.</summary>
public sealed class HrLegalParametersApiTests
{
    private static readonly DateTimeOffset Today = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(PostgreSqlContainer Database, QepApiFactory Factory)> OpenAtAsync()
    {
        var database = await StartDatabaseAsync();
        var clock = new MutableTenantClock(Today);
        var factory = new QepApiFactory(database.GetConnectionString(), services =>
        {
            services.RemoveAll<ITenantClock>();
            services.AddSingleton<ITenantClock>(clock);
        });
        await SeedTenantAsync(factory);
        return (database, factory);
    }

    private static async Task<IReadOnlyList<LegalParameterValue>> ListValuesAsync(QepApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ILegalParameterSource>().ListAsync(Ct);
    }

    private static decimal? At(IReadOnlyList<LegalParameterValue> values, LegalParameterKey key, DateOnly date) =>
        LegalParameters.Resolve(values, key, date);

    [Fact]
    public async Task TheMigrationSeedsTheTwentyTwentySixSet()
    {
        var (database, factory) = await OpenAtAsync();
        await using var _ = database;
        using var __ = factory;

        var values = await ListValuesAsync(factory);
        var date = new DateOnly(2026, 11, 1);

        Assert.Equal(249_095m, At(values, LegalParameterKey.TransportAllowance, date));
        Assert.Equal(0.0696m, At(values, LegalParameterKey.ArlClassVRate, date));
        Assert.Equal(0.90m, At(values, LegalParameterKey.SundaySurchargeRate, date));
        Assert.Equal(42m, At(values, LegalParameterKey.WeeklyHours, date));
        Assert.Equal(25m, At(values, LegalParameterKey.IbcCapSmmlv, date));
    }

    [Fact]
    public async Task ValuesKeepSixDecimals()
    {
        var (database, factory) = await OpenAtAsync();
        await using var _ = database;
        using var __ = factory;

        var values = await ListValuesAsync(factory);

        Assert.Equal(0.00522m, At(values, LegalParameterKey.ArlClassIRate, new DateOnly(2026, 11, 1)));
    }

    [Fact]
    public async Task TheSolidarityBracketsAreSeeded()
    {
        var (database, factory) = await OpenAtAsync();
        await using var _ = database;
        using var __ = factory;

        await using var scope = factory.Services.CreateAsyncScope();
        var brackets = await scope.ServiceProvider.GetRequiredService<ILegalParameterSource>().ListBracketsAsync(Ct);
        var of2026 = LegalParameters.ResolveBrackets(brackets, LegalBracketKey.SolidarityFund, new DateOnly(2026, 11, 1));

        Assert.Equal(6, of2026.Count);
        var last = of2026[^1];
        Assert.Equal((20m, 25m, 0.02m), (last.LowerBound, last.UpperBound, last.Rate));
    }

    [Fact]
    public async Task SalaryRulesStillAnswer()
    {
        var (database, factory) = await OpenAtAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateFullAccess(factory);

        var response = await client.GetAsync($"{HrUrl()}/salary-rules", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        Assert.Equal(1_750_905m, document.RootElement.GetProperty("smmlv").GetDecimal());
    }
}
