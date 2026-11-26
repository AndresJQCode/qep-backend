using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Hr.Application;
using Npgsql;
using static Modules.Hr.IntegrationTests.HrApiHarness;
using static Modules.Hr.IntegrationTests.HrApiSeeds;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// Spec 2026-11-23 §10: the activity and the audit of the exams. They record the type, the date and whether there is a PDF, and
/// nothing else: restrictions, recommendations and the provider are health data. The <c>Medical</c> category of the activity is only
/// for whoever has <c>hr.medical.read</c>.
/// </summary>
public sealed class MedicalExamActivityApiTests : IClassFixture<HrApiFixture>
{
    private const string SecretRestriction = "SECRET-RESTRICTION-4711";
    private const string SecretRecommendation = "SECRET-RECOMMENDATION-4712";
    private const string SecretProvider = "SECRET-PROVIDER-4713";
    private static readonly string[] Secrets = [SecretRestriction, SecretRecommendation, SecretProvider];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HrApiFixture _fixture;

    public MedicalExamActivityApiTests(HrApiFixture fixture) => _fixture = fixture;

    private static object Body(string type, string date, bool secrets) => new
    {
        type,
        examDate = date,
        provider = secrets ? SecretProvider : null,
        restrictions = secrets ? SecretRestriction : null,
        recommendations = secrets ? SecretRecommendation : null,
        restrictionsUntil = (string?)null,
    };

    private static string ContractUrl(HrTenant tenant, JsonElement detail) =>
        $"{EmployeeUrl(tenant, detail)}/contracts/{detail.GetProperty("contract").GetProperty("id").GetGuid()}";

    private static async Task<string> ActivityBodyAsync(HttpClient client, string employeeUrl)
    {
        var response = await client.GetAsync($"{employeeUrl}/activity?pageSize=100", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await BodyAsync(response);
    }

    private async Task<List<string>> AuditRowsAsync(string resourceId)
    {
        var connectionString = _fixture.Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT row_to_json(a)::text FROM audit.entries a WHERE resource_id = @id", connection);
        command.Parameters.AddWithValue("id", resourceId);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    [Fact]
    public async Task ActivityNeverContainsRestrictionsRecommendationsOrProvider()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        var url = $"{EmployeeUrl(tenant, detail)}/medical-exams";
        var recorded = await JsonOfAsync(await tenant.Hr.PostAsJsonAsync(url, Body("Periodic", "2026-11-10", secrets: true), Ct));
        var examId = recorded.GetProperty("exams")[0].GetProperty("id").GetGuid();
        var updated = await JsonOfAsync(await PutAsync(tenant.Hr, $"{url}/{examId}", Body("Periodic", "2026-11-11", secrets: true), "\"1\""));
        await tenant.Hr.PutAsJsonAsync($"{url}/{examId}/document", new { fileId = _fixture.Documents.Seed() }, Ct);
        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"{url}/{examId}");
        delete.Headers.TryAddWithoutValidation("If-Match", "\"3\"");
        Assert.Equal(HttpStatusCode.OK, (await tenant.Hr.SendAsync(delete, Ct)).StatusCode);
        Assert.Equal(2, updated.GetProperty("exams")[0].GetProperty("version").GetInt64());

        var activity = await ActivityBodyAsync(tenant.Hr, EmployeeUrl(tenant, detail));

        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, activity));
        using var document = JsonDocument.Parse(activity);
        var medical = document.RootElement.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("category").GetString() == "Medical")
            .OrderBy(item => item.GetProperty("occurredAt").GetDateTimeOffset())
            .ToList();
        Assert.Equal(
            ["MedicalExamDeleted", "MedicalExamRecorded", "MedicalExamUpdated", "MedicalExamUpdated"],
            medical.Select(item => item.GetProperty("action").GetString()).Order(StringComparer.Ordinal));
        var created = medical.Single(item => item.GetProperty("action").GetString() == "MedicalExamRecorded");
        Assert.Equal(
            ["type", "examDate", "hasDocument"],
            created.GetProperty("changes").EnumerateArray().Select(change => change.GetProperty("field").GetString()));
        Assert.All(
            AuditRows(await AuditRowsAsync(examId.ToString())),
            row => Assert.All(Secrets, secret => Assert.DoesNotContain(secret, row)));
    }

    private static List<string> AuditRows(List<string> rows)
    {
        Assert.NotEmpty(rows);
        return rows;
    }

    [Fact]
    public async Task ActivityMedicalCategoryHiddenWithoutMedicalRead()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        await tenant.Hr.PostAsJsonAsync($"{EmployeeUrl(tenant, detail)}/medical-exams", Body("Periodic", "2026-11-10", secrets: false), Ct);
        using var noMedical = tenant.Client(
            HrPermissions.EmployeeRead, HrPermissions.EmployeeManage, HrPermissions.ContractRead, HrPermissions.ContractManage);
        using var medicalOnly = tenant.Client(HrPermissions.EmployeeRead, HrPermissions.MedicalRead);

        var hidden = JsonDocument.Parse(await ActivityBodyAsync(noMedical, EmployeeUrl(tenant, detail))).RootElement;
        var visible = JsonDocument.Parse(await ActivityBodyAsync(medicalOnly, EmployeeUrl(tenant, detail))).RootElement;
        var everything = JsonDocument.Parse(await ActivityBodyAsync(tenant.Hr, EmployeeUrl(tenant, detail))).RootElement;

        Assert.DoesNotContain(hidden.GetProperty("items").EnumerateArray(), item => item.GetProperty("category").GetString() == "Medical");
        Assert.Equal(everything.GetProperty("total").GetInt32() - 1, hidden.GetProperty("total").GetInt32());
        Assert.True(visible.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("category").GetString() == "Medical"), visible.GetRawText());
        Assert.Contains(everything.GetProperty("items").EnumerateArray(), item => item.GetProperty("category").GetString() == "Medical");
    }

    [Fact]
    public async Task AuditRowsExistForEachWrite()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        var employeeId = detail.GetProperty("employee").GetProperty("id").GetString()!;
        var url = $"{EmployeeUrl(tenant, detail)}/medical-exams";
        var recorded = await JsonOfAsync(await tenant.Hr.PostAsJsonAsync(url, Body("Periodic", "2026-11-10", secrets: false), Ct));
        var examId = recorded.GetProperty("exams")[0].GetProperty("id").GetGuid().ToString();
        await PutAsync(tenant.Hr, $"{url}/{examId}", Body("Periodic", "2026-11-11", secrets: false), "\"1\"");
        await tenant.Hr.PutAsJsonAsync($"{url}/{examId}/document", new { fileId = _fixture.Documents.Seed() }, Ct);
        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"{url}/{examId}");
        delete.Headers.TryAddWithoutValidation("If-Match", "\"3\"");
        await tenant.Hr.SendAsync(delete, Ct);
        await tenant.Hr.PostAsJsonAsync($"{ContractUrl(tenant, detail)}/terminate", new { reason = "Resignation", date = "2026-11-16", notes = (string?)null }, Ct);
        await tenant.Hr.PostAsJsonAsync(
            $"{EmployeeUrl(tenant, detail)}/exit-exam/not-attended", new { notifiedOn = "2026-11-17", notes = "SECRET-NOTE" }, Ct);

        Assert.Equal(1, await CountAuditEntriesAsync(_fixture.Factory, "hr.medical_exam.created", examId));
        Assert.Equal(1, await CountAuditEntriesAsync(_fixture.Factory, "hr.medical_exam.updated", examId));
        Assert.Equal(1, await CountAuditEntriesAsync(_fixture.Factory, "hr.medical_exam.document_updated", examId));
        Assert.Equal(1, await CountAuditEntriesAsync(_fixture.Factory, "hr.medical_exam.deleted", examId));
        Assert.Equal(1, await CountAuditEntriesAsync(_fixture.Factory, "hr.medical_exam.exit_not_attended", employeeId));
        Assert.All(await AuditRowsAsync(employeeId), row => Assert.DoesNotContain("SECRET-NOTE", row));
    }

    [Fact]
    public async Task DeleteEmployeeRemovesExamsAndRequirements()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        var employeeId = detail.GetProperty("employee").GetProperty("id").GetGuid();
        await tenant.Hr.PostAsJsonAsync($"{EmployeeUrl(tenant, detail)}/medical-exams", Body("Periodic", "2026-11-10", secrets: false), Ct);
        await using var connection = new NpgsqlConnection(_fixture.Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!);
        await connection.OpenAsync(Ct);
        // A requirement of that employee, as if the contract had been terminated: the delete rule of the employee does not allow
        // deleting one that was terminated, so the row is inserted by hand to prove the cascade of the schema.
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO hr.exit_exam_requirements (id, tenant_id, employee_id, contract_id, terminated_on, due_date, status, created_at)
            SELECT gen_random_uuid(), e.tenant_id, c.employee_id, c.id, DATE '2026-11-16', DATE '2026-11-23', 'Pending', now()
            FROM hr.employment_contracts c JOIN hr.employees e ON e.id = c.employee_id WHERE c.employee_id = @employee
            """,
            connection))
        {
            insert.Parameters.AddWithValue("employee", employeeId);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(Ct));
        }

        var response = await tenant.Hr.DeleteAsync(EmployeeUrl(tenant, detail), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        foreach (var table in new[] { "hr.medical_exams", "hr.exit_exam_requirements" })
        {
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE employee_id = @employee", connection);
            count.Parameters.AddWithValue("employee", employeeId);
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync(Ct))!);
        }
    }
}
