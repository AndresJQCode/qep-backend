using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Hr.IntegrationTests.HrApiHarness;
using static Modules.Hr.IntegrationTests.HrApiSeeds;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// Spec 2026-11-23 (medical exams) §5.3, §5.4 and §7.1: recording, editing and deleting exams, and the exit requirement that closes
/// and reopens with them. The clock of the fixture is 2026-11-20; the tenant is Colombian (2026-11-16 is a holiday Monday).
/// </summary>
public sealed class MedicalExamsApiTests : IClassFixture<HrApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HrApiFixture _fixture;

    public MedicalExamsApiTests(HrApiFixture fixture) => _fixture = fixture;

    private static string MedicalUrl(HrTenant tenant, JsonElement detail) => $"{EmployeeUrl(tenant, detail)}/medical-exams";

    private static string ContractUrl(HrTenant tenant, JsonElement detail) =>
        $"{EmployeeUrl(tenant, detail)}/contracts/{detail.GetProperty("contract").GetProperty("id").GetGuid()}";

    private static object ExamBody(
        string type = "Periodic",
        string examDate = "2026-11-10",
        string? restrictions = null,
        string? restrictionsUntil = null,
        string? provider = null,
        string? recommendations = null) =>
        new { type, examDate, provider, restrictions, recommendations, restrictionsUntil };

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    private static Task<HttpResponseMessage> PostExamAsync(HrTenant tenant, JsonElement detail, object body) =>
        tenant.Hr.PostAsJsonAsync(MedicalUrl(tenant, detail), body, Ct);

    private static async Task<JsonElement> RecordAsync(HrTenant tenant, JsonElement detail, object body) =>
        await OkAsync(await PostExamAsync(tenant, detail, body));

    private static Task<HttpResponseMessage> DeleteExamAsync(HrTenant tenant, JsonElement detail, Guid examId, string? ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{MedicalUrl(tenant, detail)}/{examId}");
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return tenant.Hr.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> TerminateAsync(HrTenant tenant, JsonElement detail, string date) =>
        tenant.Hr.PostAsJsonAsync($"{ContractUrl(tenant, detail)}/terminate", new { reason = "Resignation", date, notes = (string?)null }, Ct);

    private static async Task<JsonElement> GetExamsAsync(HrTenant tenant, JsonElement detail)
    {
        var response = await tenant.Hr.GetAsync(MedicalUrl(tenant, detail), Ct);
        return await OkAsync(response);
    }

    private static Guid ExamId(JsonElement dto, int index = 0) => dto.GetProperty("exams")[index].GetProperty("id").GetGuid();

    private static string ExamVersion(JsonElement dto, int index = 0) => $"\"{dto.GetProperty("exams")[index].GetProperty("version").GetInt64()}\"";

    private static Task<HttpResponseMessage> SetFrequencyAsync(HrTenant tenant, SeededSetup setup, int months) =>
        PutAsync(tenant.Hr, $"{tenant.Url}/positions/{setup.PositionId}/exam-frequency", new { months }, "\"1\"");

    [Fact]
    public async Task PostRecordsExamAndReturnsWholeDto()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        await SetFrequencyAsync(tenant, setup, 12);

        var dto = await RecordAsync(
            tenant, detail, ExamBody(restrictions: "No lifting", restrictionsUntil: "2026-12-10", provider: "IPS Norte", recommendations: "Pauses"));

        Assert.Equal("UpToDate", dto.GetProperty("status").GetString());
        Assert.Equal("2027-11-10", dto.GetProperty("nextPeriodicDue").GetString());
        Assert.Equal(12, dto.GetProperty("examFrequencyMonths").GetInt32());
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("exitRequirement").ValueKind);
        var exam = Assert.Single(dto.GetProperty("exams").EnumerateArray());
        Assert.Equal("Periodic", exam.GetProperty("type").GetString());
        Assert.Equal("2026-11-10", exam.GetProperty("examDate").GetString());
        Assert.Equal("IPS Norte", exam.GetProperty("provider").GetString());
        Assert.Equal("No lifting", exam.GetProperty("restrictions").GetString());
        Assert.Equal("Pauses", exam.GetProperty("recommendations").GetString());
        Assert.Equal("2026-12-10", exam.GetProperty("restrictionsUntil").GetString());
        Assert.False(exam.GetProperty("hasDocument").GetBoolean());
        Assert.Equal(1, exam.GetProperty("version").GetInt64());

        var again = await GetExamsAsync(tenant, detail);
        Assert.Equal(dto.GetRawText(), again.GetRawText());
    }

    [Fact]
    public async Task GetListsTheMostRecentExamFirstAndTheStatusFollowsTheLatest()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        await SetFrequencyAsync(tenant, setup, 6);

        await RecordAsync(tenant, detail, ExamBody("PreEmployment", "2026-01-10"));
        var dto = await RecordAsync(tenant, detail, ExamBody("Periodic", "2026-05-20"));

        Assert.Equal(["2026-05-20", "2026-01-10"], dto.GetProperty("exams").EnumerateArray().Select(exam => exam.GetProperty("examDate").GetString()));
        // Latest 2026-05-20 + 6 months = 2026-11-20 = today: due soon, not overdue.
        Assert.Equal("2026-11-20", dto.GetProperty("nextPeriodicDue").GetString());
        Assert.Equal("DueSoon", dto.GetProperty("status").GetString());
    }

    [Fact]
    public async Task PostDuplicateReturns422Duplicate()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        await RecordAsync(tenant, detail, ExamBody());

        var response = await PostExamAsync(tenant, detail, ExamBody(restrictions: "Different text"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("hr.medical_exam.duplicate", CodeOf(await JsonOfAsync(response)));
        Assert.Single((await GetExamsAsync(tenant, detail)).GetProperty("exams").EnumerateArray());
    }

    [Fact]
    public async Task PutWithoutIfMatchReturns428()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        var dto = await RecordAsync(tenant, detail, ExamBody());

        var response = await PutAsync(tenant.Hr, $"{MedicalUrl(tenant, detail)}/{ExamId(dto)}", ExamBody(), null);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await DeleteExamAsync(tenant, detail, ExamId(dto), null)).StatusCode);
    }

    [Fact]
    public async Task PutStaleVersionReturns412()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        var dto = await RecordAsync(tenant, detail, ExamBody());
        var url = $"{MedicalUrl(tenant, detail)}/{ExamId(dto)}";
        var updated = await OkAsync(await PutAsync(tenant.Hr, url, ExamBody(provider: "IPS Sur"), ExamVersion(dto)));
        Assert.Equal(2, updated.GetProperty("exams")[0].GetProperty("version").GetInt64());

        var stale = await PutAsync(tenant.Hr, url, ExamBody(provider: "IPS Oeste"), ExamVersion(dto));

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await DeleteExamAsync(tenant, detail, ExamId(dto), ExamVersion(dto))).StatusCode);
        Assert.Equal("IPS Sur", (await GetExamsAsync(tenant, detail)).GetProperty("exams")[0].GetProperty("provider").GetString());
    }

    [Fact]
    public async Task PutUnknownExamReturns404()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var response = await PutAsync(tenant.Hr, $"{MedicalUrl(tenant, detail)}/{Guid.NewGuid()}", ExamBody(), "\"1\"");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("hr.medical_exam.not_found", CodeOf(await JsonOfAsync(response)));
    }

    [Fact]
    public async Task PutIntoAnExistingTypeAndDateReturns422Duplicate()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        await RecordAsync(tenant, detail, ExamBody("Periodic", "2026-11-10"));
        var dto = await RecordAsync(tenant, detail, ExamBody("FollowUp", "2026-11-12"));

        var response = await PutAsync(
            tenant.Hr, $"{MedicalUrl(tenant, detail)}/{ExamId(dto)}", ExamBody("Periodic", "2026-11-10"), ExamVersion(dto));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("hr.medical_exam.duplicate", CodeOf(await JsonOfAsync(response)));
    }

    [Fact]
    public async Task PutChangingExitToPeriodicReopensTheRequirement()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        await TerminateAsync(tenant, detail, "2026-11-16");
        var closed = await RecordAsync(tenant, detail, ExamBody("Exit", "2026-11-18"));
        Assert.Equal("Done", closed.GetProperty("exitRequirement").GetProperty("status").GetString());
        Assert.Equal(ExamId(closed), closed.GetProperty("exitRequirement").GetProperty("examId").GetGuid());
        Assert.Equal(JsonValueKind.Null, closed.GetProperty("status").ValueKind);

        var reopened = await OkAsync(await PutAsync(
            tenant.Hr, $"{MedicalUrl(tenant, detail)}/{ExamId(closed)}", ExamBody("Periodic", "2026-11-18"), ExamVersion(closed)));

        var requirement = reopened.GetProperty("exitRequirement");
        Assert.Equal("Pending", requirement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, requirement.GetProperty("examId").ValueKind);
        Assert.Equal("ExitPending", reopened.GetProperty("status").GetString());

        // An Exit exam dated before the termination does not close it either.
        var early = await OkAsync(await PutAsync(
            tenant.Hr, $"{MedicalUrl(tenant, detail)}/{ExamId(reopened)}", ExamBody("Exit", "2026-11-15"), ExamVersion(reopened)));
        Assert.Equal("Pending", early.GetProperty("exitRequirement").GetProperty("status").GetString());
    }

    [Fact]
    public async Task DeleteTheExitExamReopensTheRequirement()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        await TerminateAsync(tenant, detail, "2026-11-16");
        var closed = await RecordAsync(tenant, detail, ExamBody("Exit", "2026-11-18"));

        var reopened = await OkAsync(await DeleteExamAsync(tenant, detail, ExamId(closed), ExamVersion(closed)));

        Assert.Empty(reopened.GetProperty("exams").EnumerateArray());
        Assert.Equal("Pending", reopened.GetProperty("exitRequirement").GetProperty("status").GetString());
        Assert.Equal("ExitPending", reopened.GetProperty("status").GetString());
    }

    [Fact]
    public async Task NotAttendedSetsNoticeAndKeepsItAcrossRecalculation()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        await TerminateAsync(tenant, detail, "2026-11-16");

        var marked = await OkAsync(await tenant.Hr.PostAsJsonAsync(
            $"{EmployeeUrl(tenant, detail)}/exit-exam/not-attended", new { notifiedOn = "2026-11-17", notes = "  Called twice  " }, Ct));

        var requirement = marked.GetProperty("exitRequirement");
        Assert.Equal("NotAttended", requirement.GetProperty("status").GetString());
        Assert.Equal("2026-11-17", requirement.GetProperty("notifiedOn").GetString());
        Assert.Equal("Called twice", requirement.GetProperty("notes").GetString());
        // Closed: the employee leaves the control universe.
        Assert.Equal(JsonValueKind.Null, marked.GetProperty("status").ValueKind);

        var done = await RecordAsync(tenant, detail, ExamBody("Exit", "2026-11-19"));
        Assert.Equal("Done", done.GetProperty("exitRequirement").GetProperty("status").GetString());
        Assert.Equal("2026-11-17", done.GetProperty("exitRequirement").GetProperty("notifiedOn").GetString());

        var reopened = await OkAsync(await DeleteExamAsync(tenant, detail, ExamId(done), ExamVersion(done)));
        Assert.Equal("NotAttended", reopened.GetProperty("exitRequirement").GetProperty("status").GetString());
        Assert.Equal("2026-11-17", reopened.GetProperty("exitRequirement").GetProperty("notifiedOn").GetString());
    }

    [Fact]
    public async Task NotAttendedWithoutPendingReturns422NoPendingExit()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var none = await tenant.Hr.PostAsJsonAsync(
            $"{EmployeeUrl(tenant, detail)}/exit-exam/not-attended", new { notifiedOn = "2026-11-17", notes = (string?)null }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, none.StatusCode);
        Assert.Equal("hr.medical_exam.no_pending_exit", CodeOf(await JsonOfAsync(none)));

        await TerminateAsync(tenant, detail, "2026-11-16");
        await OkAsync(await tenant.Hr.PostAsJsonAsync(
            $"{EmployeeUrl(tenant, detail)}/exit-exam/not-attended", new { notifiedOn = "2026-11-17", notes = (string?)null }, Ct));
        var twice = await tenant.Hr.PostAsJsonAsync(
            $"{EmployeeUrl(tenant, detail)}/exit-exam/not-attended", new { notifiedOn = "2026-11-18", notes = (string?)null }, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, twice.StatusCode);
        Assert.Equal("hr.medical_exam.no_pending_exit", CodeOf(await JsonOfAsync(twice)));
    }

    [Fact]
    public async Task NotAttendedWithAFutureNoticeReturns422()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        await TerminateAsync(tenant, detail, "2026-11-16");

        var response = await tenant.Hr.PostAsJsonAsync(
            $"{EmployeeUrl(tenant, detail)}/exit-exam/not-attended", new { notifiedOn = "2026-11-21", notes = (string?)null }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True(HasError(await JsonOfAsync(response), "notifiedOn"));
    }

    [Fact]
    public async Task GetDeactivatedReturnsNullStatusWithHistory()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        await SetFrequencyAsync(tenant, setup, 12);
        await RecordAsync(tenant, detail, ExamBody());
        await tenant.Hr.PostAsJsonAsync($"{EmployeeUrl(tenant, detail)}/deactivate", new { date = "2026-11-19" }, Ct);

        var dto = await GetExamsAsync(tenant, detail);

        Assert.Equal(JsonValueKind.Null, dto.GetProperty("status").ValueKind);
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("nextPeriodicDue").ValueKind);
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("examFrequencyMonths").ValueKind);
        Assert.Single(dto.GetProperty("exams").EnumerateArray());
    }

    [Fact]
    public async Task GetWithoutAnyExamAndWithoutFrequencyFollowsTheStatusRules()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var empty = await GetExamsAsync(tenant, detail);
        Assert.Equal("NoExams", empty.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("examFrequencyMonths").ValueKind);

        var recorded = await RecordAsync(tenant, detail, ExamBody());
        Assert.Equal("NotRequired", recorded.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, recorded.GetProperty("nextPeriodicDue").ValueKind);
    }

    [Fact]
    public async Task PostFutureDateReturns422FutureDate()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var response = await PostExamAsync(tenant, detail, ExamBody(examDate: "2026-11-21"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await JsonOfAsync(response);
        Assert.Equal("hr.medical_exam.future_date", CodeOf(problem));
        Assert.True(HasError(problem, "examDate"));
    }

    [Fact]
    public async Task PostRestrictionsUntilWithoutRestrictionsReturns422()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var response = await PostExamAsync(tenant, detail, ExamBody(restrictionsUntil: "2026-12-01"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await JsonOfAsync(response);
        Assert.Equal("hr.medical_exam.restrictions_until_invalid", CodeOf(problem));
        Assert.True(HasError(problem, "restrictionsUntil"));
    }

    [Fact]
    public async Task PostInvalidBodyReturnsFieldErrors()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);

        var response = await tenant.Hr.PostAsJsonAsync(
            MedicalUrl(tenant, detail),
            new { type = "Nonsense", examDate = (string?)null, restrictions = new string('x', 2001) },
            Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await JsonOfAsync(response);
        Assert.True(HasError(problem, "type"));
        Assert.True(HasError(problem, "examDate"));
        Assert.True(HasError(problem, "restrictions"));
    }

    [Fact]
    public async Task UnknownEmployeeReturns404()
    {
        using var tenant = await NewTenantAsync(_fixture);

        var response = await tenant.Hr.GetAsync($"{tenant.Url}/employees/{Guid.NewGuid()}/medical-exams", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("hr.employee.not_found", CodeOf(await JsonOfAsync(response)));
    }

    [Fact]
    public async Task TheExitRequirementShowsBusinessDaysLeftAndOverdue()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var onTime = await HireAsync(tenant, setup, "1000000001");
        var late = await HireAsync(tenant, setup, "1000000002");
        // Today is Friday 2026-11-20. Terminated on the holiday Monday 11-16 the deadline is Monday 11-23: one business day left.
        // Terminated on 11-09 it was 11-17 (11-16 is a holiday): already overdue.
        await TerminateAsync(tenant, onTime, "2026-11-16");
        await TerminateAsync(tenant, late, "2026-11-09");

        var first = (await GetExamsAsync(tenant, onTime)).GetProperty("exitRequirement");
        var second = (await GetExamsAsync(tenant, late)).GetProperty("exitRequirement");

        Assert.Equal("2026-11-23", first.GetProperty("dueDate").GetString());
        Assert.Equal(1, first.GetProperty("businessDaysLeft").GetInt32());
        Assert.False(first.GetProperty("overdue").GetBoolean());
        Assert.Equal("2026-11-17", second.GetProperty("dueDate").GetString());
        Assert.Equal(0, second.GetProperty("businessDaysLeft").GetInt32());
        Assert.True(second.GetProperty("overdue").GetBoolean());
        Assert.Equal("2026-11-09", second.GetProperty("terminatedOn").GetString());
        Assert.Equal(detailContract(late), second.GetProperty("contractId").GetGuid());

        static Guid detailContract(JsonElement detail) => detail.GetProperty("contract").GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task TwoTerminationsTakeTheirOwnExitExamAndNeverShareOne()
    {
        using var tenant = await NewTenantAsync(_fixture);
        var setup = await SeedSetupAsync(tenant);
        var detail = await HireAsync(tenant, setup);
        await TerminateAsync(tenant, detail, "2026-02-27");
        var rehired = await OkAsync(await tenant.Hr.PostAsJsonAsync(
            $"{EmployeeUrl(tenant, detail)}/reincorporate", NewContractBody(setup, "2026-03-02"), Ct));
        await TerminateAsync(tenant, rehired, "2026-11-16");

        // One exit exam after both terminations closes only the oldest requirement; the most recent one stays pending.
        var first = await RecordAsync(tenant, detail, ExamBody("Exit", "2026-11-18"));
        Assert.Equal("Pending", first.GetProperty("exitRequirement").GetProperty("status").GetString());
        Assert.Equal("2026-11-16", first.GetProperty("exitRequirement").GetProperty("terminatedOn").GetString());

        // A second, older one: each requirement takes the oldest exam that qualifies and neither shares.
        var second = await RecordAsync(tenant, detail, ExamBody("Exit", "2026-03-05"));
        var requirement = second.GetProperty("exitRequirement");
        Assert.Equal("Done", requirement.GetProperty("status").GetString());
        Assert.Equal("2026-11-16", requirement.GetProperty("terminatedOn").GetString());
        var examsByDate = second.GetProperty("exams").EnumerateArray().ToDictionary(
            exam => exam.GetProperty("examDate").GetString()!, exam => exam.GetProperty("id").GetGuid());
        Assert.Equal(examsByDate["2026-11-18"], requirement.GetProperty("examId").GetGuid());
    }
}
