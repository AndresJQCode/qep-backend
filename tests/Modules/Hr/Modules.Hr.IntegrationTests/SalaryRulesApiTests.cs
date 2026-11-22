using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Hr.Application;
using Modules.Tenancy.Application;
using Testcontainers.PostgreSql;
using static Modules.Hr.IntegrationTests.HrApiHarness;

namespace Modules.Hr.IntegrationTests;

/// <summary><c>GET /hr/salary-rules</c> (spec 2026-11-20): los valores legales vigentes a una fecha, sin pantalla de carga.</summary>
public sealed class SalaryRulesApiTests
{
    // 15:00Z es 10:00 en Bogotá: el mismo día en el tenant y en UTC.
    private static readonly DateTimeOffset Today = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string RulesUrl(string query = "") => $"{HrUrl()}/salary-rules{query}";

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

    [Fact]
    public async Task AnswersTheValuesOfTheRequestedDate()
    {
        var (database, factory) = await OpenAtAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateFullAccess(factory);

        var response = await client.GetAsync(RulesUrl("?date=2025-06-30"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        var root = document.RootElement;
        Assert.Equal("2025-06-30", root.GetProperty("date").GetString());
        Assert.Equal(1_423_500m, root.GetProperty("smmlv").GetDecimal());
        Assert.Equal(18_505_500m, root.GetProperty("integralFloor").GetDecimal());
        Assert.Equal(
            ["Contractor", "PartTime", "SenaApprenticeTeaching", "SenaApprenticeProductive", "SenaApprenticeDual", "StudentLaw789", "StudentDecree055"],
            root.GetProperty("belowMinimumAllowedContractTypes").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task WithoutDateUsesTheTenantToday()
    {
        var (database, factory) = await OpenAtAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateFullAccess(factory);

        var response = await client.GetAsync(RulesUrl(), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        Assert.Equal("2026-11-20", document.RootElement.GetProperty("date").GetString());
        Assert.Equal(1_750_905m, document.RootElement.GetProperty("smmlv").GetDecimal());
    }

    [Fact]
    public async Task BeforeTwentyTwentyFourIs422Missing()
    {
        var (database, factory) = await OpenAtAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateFullAccess(factory);

        var response = await client.GetAsync(RulesUrl("?date=2023-12-31"), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        Assert.Equal("hr.legal_parameter.missing", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task WithoutContractReadIs403()
    {
        var (database, factory) = await OpenAtAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateClient(factory, SubjectId, TenantId, HrPermissions.EmployeeRead);

        var response = await client.GetAsync(RulesUrl(), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AMalformedDateIs422WithTheDateField()
    {
        var (database, factory) = await OpenAtAsync();
        await using var _ = database;
        using var __ = factory;
        using var client = CreateFullAccess(factory);

        var response = await client.GetAsync(RulesUrl("?date=20-11-2026"), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("date", out var dateErrors));
        Assert.NotEqual(JsonValueKind.Null, dateErrors.ValueKind);
    }

    // La fecha se lee en el handler, ya autorizado: quien opera en otro tenant recibe 403 y no un 422 sobre un dato que todavía
    // no puede ver.
    [Fact]
    public async Task AWrongTenantWithAMalformedDateIs403()
    {
        var (database, factory) = await OpenAtAsync();
        await using var _ = database;
        using var __ = factory;
        await SeedTenantAsync(factory, OtherTenantId);
        using var outsider = CreateClient(factory, OtherSubjectId, OtherTenantId, HrPermissions.ContractRead);

        var response = await outsider.GetAsync(RulesUrl("?date=garbage"), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        Assert.Equal("authorization.denied", document.RootElement.GetProperty("code").GetString());
    }
}
