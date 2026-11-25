using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Hr.Infrastructure.Persistence;
using Npgsql;
using static Modules.Hr.IntegrationTests.HrApiHarness;
using static Modules.Hr.IntegrationTests.HrApiSeeds;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// Spec 2026-11-23 (medical exams) §5.2 and §5.3: the exam frequency of a position, and the exit requirement that is born with a
/// termination. The clock of the fixture is 2026-11-20; the tenant is Colombian.
/// </summary>
public sealed class MedicalExamPersistenceApiTests : IClassFixture<HrApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HrApiFixture _fixture;

    public MedicalExamPersistenceApiTests(HrApiFixture fixture) => _fixture = fixture;

    private static Task<HttpResponseMessage> PutFrequencyAsync(HrTenant tenant, Guid positionId, int? months, string? ifMatch) =>
        PutAsync(tenant.Hr, $"{tenant.Url}/positions/{positionId}/exam-frequency", new { months }, ifMatch);

    private static async Task<JsonElement> GetPositionAsync(HrTenant tenant, Guid positionId)
    {
        var response = await tenant.Hr.GetAsync($"{tenant.Url}/settings", Ct);
        var settings = await JsonOfAsync(response);
        return settings.GetProperty("positions").EnumerateArray().Single(position => position.GetProperty("id").GetGuid() == positionId);
    }

    private static string ContractUrl(HrTenant tenant, JsonElement detail) =>
        $"{EmployeeUrl(tenant, detail)}/contracts/{detail.GetProperty("contract").GetProperty("id").GetGuid()}";

    private static Task<HttpResponseMessage> TerminateAsync(HrTenant tenant, JsonElement detail, string date) =>
        tenant.Hr.PostAsJsonAsync($"{ContractUrl(tenant, detail)}/terminate", new { reason = "Resignation", date, notes = (string?)null }, Ct);

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connectionString = _fixture.Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }

    private async Task<List<(Guid ContractId, DateOnly TerminatedOn, DateOnly DueDate, string Status)>> RequirementsAsync(HrTenant tenant)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT contract_id, terminated_on, due_date, status FROM hr.exit_exam_requirements WHERE tenant_id = @tenant ORDER BY terminated_on",
            connection);
        command.Parameters.AddWithValue("tenant", tenant.Id);
        var rows = new List<(Guid, DateOnly, DateOnly, string)>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetGuid(0), reader.GetFieldValue<DateOnly>(1), reader.GetFieldValue<DateOnly>(2), reader.GetString(3)));
        }

        return rows;
    }

    [Fact]
    public async Task PutExamFrequencyStoresMonthsAndBumpsVersion()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);

        var response = await PutFrequencyAsync(tenant, setup.PositionId, 12, "\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var position = await JsonOfAsync(response);
        Assert.Equal(12, position.GetProperty("examFrequencyMonths").GetInt32());
        Assert.Equal(2, position.GetProperty("version").GetInt64());
        Assert.Equal(12, (await GetPositionAsync(tenant, setup.PositionId)).GetProperty("examFrequencyMonths").GetInt32());

        var cleared = await JsonOfAsync(await PutFrequencyAsync(tenant, setup.PositionId, null, "\"2\""));
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("examFrequencyMonths").ValueKind);
        Assert.Equal(3, cleared.GetProperty("version").GetInt64());

        Assert.Equal(HttpStatusCode.PreconditionRequired, (await PutFrequencyAsync(tenant, setup.PositionId, 6, null)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await PutFrequencyAsync(tenant, setup.PositionId, 6, "\"1\"")).StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    [InlineData(-3)]
    public async Task PutExamFrequencyOutOfRangeReturns422WithCode(int months)
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);

        var response = await PutFrequencyAsync(tenant, setup.PositionId, months, "\"1\"");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("hr.settings.exam_frequency_invalid", CodeOf(await JsonOfAsync(response)));
        Assert.Equal(1, (await GetPositionAsync(tenant, setup.PositionId)).GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task PutExamFrequencyWithoutSettingsManageReturns403()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        using var reader = tenant.Client(Modules.Hr.Application.HrPermissions.EmployeeRead);

        var response = await PutAsync(reader, $"{tenant.Url}/positions/{setup.PositionId}/exam-frequency", new { months = 12 }, "\"1\"");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PutPositionWithOnlyNameKeepsExamFrequency()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        await PutFrequencyAsync(tenant, setup.PositionId, 24, "\"1\"");

        var renamed = await PutAsync(tenant.Hr, $"{tenant.Url}/positions/{setup.PositionId}", new { name = "Analista sénior" }, "\"2\"");

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var position = await JsonOfAsync(renamed);
        Assert.Equal("Analista sénior", position.GetProperty("name").GetString());
        Assert.Equal(24, position.GetProperty("examFrequencyMonths").GetInt32());
    }

    [Fact]
    public async Task TerminateCreatesPendingExitRequirementDueInFiveBusinessDays()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        // Monday 2026-11-30: the 5th business day after it is Monday 2026-12-07 (2026-12-08 is a Tuesday, not a holiday move).
        var response = await TerminateAsync(tenant, detail, "2026-11-30");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var requirement = Assert.Single(await RequirementsAsync(tenant));
        Assert.Equal(detail.GetProperty("contract").GetProperty("id").GetGuid(), requirement.ContractId);
        Assert.Equal(new DateOnly(2026, 11, 30), requirement.TerminatedOn);
        Assert.Equal(new DateOnly(2026, 12, 7), requirement.DueDate);
        Assert.Equal("Pending", requirement.Status);
    }

    [Fact]
    public async Task DeactivateDoesNotCreateExitRequirement()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var response = await tenant.Hr.PostAsJsonAsync($"{EmployeeUrl(tenant, detail)}/deactivate", new { date = "2026-11-30" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await RequirementsAsync(tenant));
    }

    [Fact]
    public async Task MigrationBackfillsOnlyContractsStillWithinTheDeadline()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var inside = await HireAsync(tenant, setup, "1000000001");
        var boundary = await HireAsync(tenant, setup, "1000000002");
        var expired = await HireAsync(tenant, setup, "1000000003");
        var old = await HireAsync(tenant, setup, "1000000004");
        // Reference day 2026-11-20 (Friday). Due dates (weekends only): inside 11-16 -> 11-23, boundary 11-13 -> 11-20 (today,
        // still inside), expired 11-12 -> 11-19, old 10-01 -> 10-08.
        await TerminateAsync(tenant, inside, "2026-11-16");
        await TerminateAsync(tenant, boundary, "2026-11-13");
        await TerminateAsync(tenant, expired, "2026-11-12");
        await TerminateAsync(tenant, old, "2026-10-01");
        await using (var connection = await OpenAsync())
        {
            // What the table looked like before the migration: no requirement rows for the already terminated contracts.
            await using var wipe = new NpgsqlCommand("DELETE FROM hr.exit_exam_requirements WHERE tenant_id = @tenant", connection);
            wipe.Parameters.AddWithValue("tenant", tenant.Id);
            await wipe.ExecuteNonQueryAsync(Ct);

            await using var backfill = new NpgsqlCommand(MedicalExamBackfill.Sql("DATE '2026-11-20'"), connection);
            await backfill.ExecuteNonQueryAsync(Ct);
        }

        var rows = await RequirementsAsync(tenant);

        Assert.Equal(
            [
                (boundary.GetProperty("contract").GetProperty("id").GetGuid(), new DateOnly(2026, 11, 13), new DateOnly(2026, 11, 20)),
                (inside.GetProperty("contract").GetProperty("id").GetGuid(), new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 23)),
            ],
            rows.Select(row => (row.ContractId, row.TerminatedOn, row.DueDate)));
        Assert.All(rows, row => Assert.Equal("Pending", row.Status));
    }
}
