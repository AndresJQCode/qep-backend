using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Authorization.Application;
using Modules.Hr.Application.MedicalExams;
using Npgsql;
using static Modules.Hr.IntegrationTests.HrApiHarness;
using static Modules.Hr.IntegrationTests.HrApiSeeds;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// Spec 2026-11-23 §9 (medical exam reminders). The processor of the daily run is driven directly, with the clock of the fixture
/// (10:00 in Bogota on 2026-11-20, two hours after <c>SendHourLocal = 8</c>), without waiting for the timer. The events are read from
/// the platform outbox. The payloads never carry restrictions, recommendations, the provider or the type of an exam.
/// </summary>
public sealed class MedicalExamRemindersApiTests : IClassFixture<HrApiFixture>
{
    private const string DigestEvent = "hr.medical-exams.due-digest.v1";
    private const string ExitEvent = "hr.medical-exams.exit-required.v1";
    private const string SecretText = "SECRET-CLINICAL-TEXT-9911";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HrApiFixture _fixture;

    public MedicalExamRemindersApiTests(HrApiFixture fixture) => _fixture = fixture;

    private sealed record Outbox(string EventName, string CorrelationId, string Payload, DateTimeOffset OccurredAt);

    private async Task ProcessAsync(HrTenant tenant)
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IHrMedicalExamsReminderProcessor>().ProcessTenantAsync(tenant.Id, Ct);
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connectionString = _fixture.Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }

    private async Task<List<Outbox>> EventsAsync(HrTenant tenant, string eventName)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT event_name, correlation_id, payload::text, occurred_at FROM platform.outbox_messages
            WHERE event_name = @event AND payload->>'tenantId' = @tenant ORDER BY correlation_id
            """,
            connection);
        command.Parameters.AddWithValue("event", eventName);
        command.Parameters.AddWithValue("tenant", tenant.Id.ToString());
        var rows = new List<Outbox>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new Outbox(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return rows;
    }

    private async Task<long> CountAsync(string sql, HrTenant tenant)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tenant", tenant.Id);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static string ContractUrl(HrTenant tenant, JsonElement detail) =>
        $"{EmployeeUrl(tenant, detail)}/contracts/{detail.GetProperty("contract").GetProperty("id").GetGuid()}";

    private static Task<HttpResponseMessage> TerminateAsync(HrTenant tenant, JsonElement detail, string date) =>
        tenant.Hr.PostAsJsonAsync($"{ContractUrl(tenant, detail)}/terminate", new { reason = "Resignation", date, notes = (string?)null }, Ct);

    /// <summary>A tenant with two employees without exams (two pending), two admins, one removed admin and one advisor.</summary>
    private async Task<(HrTenant Tenant, SeededSetup Setup, Guid FirstAdmin, Guid SecondAdmin)> SeedAsync(bool employees = true, bool admins = true)
    {
        var tenant = await NewTenantAsync(_fixture);
        // The tenant is inserted directly: without the factory roles the advisor would be excluded for naming a role that does
        // not exist, not for being an advisor.
        await using (var seeding = _fixture.Factory.Services.CreateAsyncScope())
        {
            await seeding.ServiceProvider.GetRequiredService<ITenantRoleSeeder>().SeedAsync(tenant.Id, Ct);
        }

        var setup = await SeedSetupAsync(tenant);
        if (employees)
        {
            await HireAsync(tenant, HireBody(setup, documentNumber: "6100000001", firstName: "Ana", lastName: "Pérez"));
            await HireAsync(tenant, HireBody(setup, documentNumber: "6100000002", firstName: "Luis", lastName: "Gómez"));
        }

        var first = Guid.Empty;
        var second = Guid.Empty;
        if (admins)
        {
            first = await SeedMemberAsync(_fixture.Factory, $"a1-{tenant.Id:N}@example.com", "Admin Uno", "admin", tenantId: tenant.IdText);
            second = await SeedMemberAsync(_fixture.Factory, $"a2-{tenant.Id:N}@example.com", "Admin Dos", "admin", tenantId: tenant.IdText);
            await SeedMemberAsync(_fixture.Factory, $"a3-{tenant.Id:N}@example.com", "Admin Fuera", "admin", removed: true, tenantId: tenant.IdText);
        }

        await SeedMemberAsync(_fixture.Factory, $"ad-{tenant.Id:N}@example.com", "Asesor", "advisor", tenantId: tenant.IdText);
        return (tenant, setup, first, second);
    }

    [Fact]
    public async Task ProcessorPublishesOneDigestPerManagerWithTheCount()
    {
        var (tenant, _, first, second) = await SeedAsync();
        using var _ = tenant;

        await ProcessAsync(tenant);

        var events = await EventsAsync(tenant, DigestEvent);
        Assert.Equal(2, events.Count);
        Assert.Equal(
            new[] { first, second }.Select(member => $"{tenant.Id:D}:{member:D}:2026-11-20").Order(StringComparer.Ordinal),
            events.Select(row => row.CorrelationId));
        using var payload = JsonDocument.Parse(events[0].Payload);
        var root = payload.RootElement;
        Assert.Equal(tenant.Id, root.GetProperty("tenantId").GetGuid());
        Assert.Equal("2026-11-20", root.GetProperty("runDate").GetString());
        Assert.Equal(2, root.GetProperty("pendingCount").GetInt32());
        Assert.Equal(HrApiFixture.DefaultNow, root.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal(HrApiFixture.DefaultNow, events[0].OccurredAt);
        Assert.Equal(
            ["memberId", "occurredAt", "pendingCount", "runDate", "tenantId"],
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ProcessorDoesNotRunBeforeSendHourLocal()
    {
        var (tenant, _, _, _) = await SeedAsync();
        using var _ = tenant;
        // 11:00 UTC is 06:00 in Bogota, before SendHourLocal = 8.
        _fixture.Clock.UtcNow = new DateTimeOffset(2026, 11, 20, 11, 0, 0, TimeSpan.Zero);

        await ProcessAsync(tenant);

        Assert.Empty(await EventsAsync(tenant, DigestEvent));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM hr.tenant_runs WHERE tenant_id = @tenant", tenant));
        // And it does run from the hour on.
        _fixture.Clock.UtcNow = new DateTimeOffset(2026, 11, 20, 13, 0, 0, TimeSpan.Zero);
        await ProcessAsync(tenant);
        Assert.Equal(2, (await EventsAsync(tenant, DigestEvent)).Count);
    }

    [Fact]
    public async Task ProcessorRunsOncePerTenantPerDay()
    {
        var (tenant, _, _, _) = await SeedAsync();
        using var _ = tenant;

        await ProcessAsync(tenant);
        await ProcessAsync(tenant);

        Assert.Equal(2, (await EventsAsync(tenant, DigestEvent)).Count);
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM hr.tenant_runs WHERE tenant_id = @tenant AND finished_at IS NOT NULL", tenant));
        Assert.Equal(2, await CountAsync("SELECT count(*) FROM hr.daily_digests WHERE tenant_id = @tenant AND status = 'Sent'", tenant));

        // The next day is a new run, with its own claims.
        _fixture.Clock.UtcNow = HrApiFixture.DefaultNow.AddDays(1);
        await ProcessAsync(tenant);
        Assert.Equal(4, (await EventsAsync(tenant, DigestEvent)).Count);
    }

    [Fact]
    public async Task ProcessorSkipsMembersWithoutManagePermission()
    {
        var (tenant, _, _, _) = await SeedAsync(admins: false);
        using var _ = tenant;

        await ProcessAsync(tenant);

        // The only members are an advisor and the one that does not exist: nobody holds hr.medical.manage.
        Assert.Empty(await EventsAsync(tenant, DigestEvent));
    }

    [Fact]
    public async Task ProcessorZeroPendingPublishesNothing()
    {
        var (tenant, _, _, _) = await SeedAsync(employees: false);
        using var _ = tenant;

        await ProcessAsync(tenant);

        Assert.Empty(await EventsAsync(tenant, DigestEvent));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM hr.tenant_runs WHERE tenant_id = @tenant AND finished_at IS NOT NULL", tenant));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM hr.daily_digests WHERE tenant_id = @tenant", tenant));
    }

    [Fact]
    public async Task ProcessorSkipsATenantWithoutTheHrModule()
    {
        var tenant = await NewTenantAsync(_fixture, modules: [Modules.Tenancy.Domain.TenantModuleKeys.Quotations]);
        using var _ = tenant;
        await SeedMemberAsync(_fixture.Factory, $"a1-{tenant.Id:N}@example.com", "Admin Uno", "admin", tenantId: tenant.IdText);

        await ProcessAsync(tenant);

        Assert.Empty(await EventsAsync(tenant, DigestEvent));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM hr.tenant_runs WHERE tenant_id = @tenant", tenant));
    }

    [Fact]
    public async Task TerminatePublishesExitRequiredToEachManager()
    {
        var (tenant, setup, first, second) = await SeedAsync(employees: false);
        using var _ = tenant;
        var detail = await HireAsync(tenant, HireBody(setup, documentNumber: "6100000009", firstName: "Ana", lastName: "Pérez"));

        var response = await TerminateAsync(tenant, detail, "2026-11-30");

        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        var events = await EventsAsync(tenant, ExitEvent);
        var contractId = detail.GetProperty("contract").GetProperty("id").GetGuid();
        Assert.Equal(
            new[] { first, second }.Select(member => $"{tenant.Id:N}:{member:N}:{contractId:N}").Order(StringComparer.Ordinal),
            events.Select(row => row.CorrelationId));
        using var payload = JsonDocument.Parse(events[0].Payload);
        var root = payload.RootElement;
        Assert.Equal(detail.GetProperty("employee").GetProperty("id").GetGuid(), root.GetProperty("employeeId").GetGuid());
        Assert.Equal("Ana Pérez", root.GetProperty("employeeName").GetString());
        Assert.Equal("2026-12-07", root.GetProperty("dueDate").GetString());
        Assert.Equal(HrApiFixture.DefaultNow, root.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal(
            ["dueDate", "employeeId", "employeeName", "memberId", "occurredAt", "tenantId"],
            root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        // Written with the termination itself: the employee is retired and the requirement exists.
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM hr.exit_exam_requirements WHERE tenant_id = @tenant", tenant));
    }

    private static Task<HttpResponseMessage> RecordExitAsync(HrTenant tenant, JsonElement detail, string date) =>
        tenant.Hr.PostAsJsonAsync(
            $"{EmployeeUrl(tenant, detail)}/medical-exams",
            new { type = "Exit", examDate = date, provider = (string?)null, restrictions = (string?)null, recommendations = (string?)null, restrictionsUntil = (string?)null },
            Ct);

    // An exit exam recorded before the termination counts when it is dated on or after it: the requirement is born closed and nobody
    // is told to get an exam that already exists.
    [Fact]
    public async Task TerminateWithAnExitExamOnOrAfterTheDateClosesTheRequirementAndPublishesNothing()
    {
        var (tenant, setup, _, _) = await SeedAsync(employees: false);
        using var _ = tenant;
        var detail = await HireAsync(tenant, HireBody(setup, documentNumber: "6100000009"));
        Assert.Equal(HttpStatusCode.OK, (await RecordExitAsync(tenant, detail, "2026-11-15")).StatusCode);

        var response = await TerminateAsync(tenant, detail, "2026-11-10");

        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        Assert.Empty(await EventsAsync(tenant, ExitEvent));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM hr.exit_exam_requirements WHERE tenant_id = @tenant AND status = 'Done' AND exam_id IS NOT NULL", tenant));
        var exams = await JsonOfAsync(await tenant.Hr.GetAsync($"{EmployeeUrl(tenant, detail)}/medical-exams", Ct));
        Assert.Equal("Done", exams.GetProperty("exitRequirement").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, exams.GetProperty("status").ValueKind);
    }

    [Fact]
    public async Task TerminateWithAnExitExamBeforeTheDateKeepsItPendingAndPublishes()
    {
        var (tenant, setup, _, _) = await SeedAsync(employees: false);
        using var _ = tenant;
        var detail = await HireAsync(tenant, HireBody(setup, documentNumber: "6100000009"));
        Assert.Equal(HttpStatusCode.OK, (await RecordExitAsync(tenant, detail, "2026-11-05")).StatusCode);

        var response = await TerminateAsync(tenant, detail, "2026-11-10");

        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        Assert.Equal(2, (await EventsAsync(tenant, ExitEvent)).Count);
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM hr.exit_exam_requirements WHERE tenant_id = @tenant AND status = 'Pending' AND exam_id IS NULL", tenant));
    }
    [Fact]
    public async Task TerminateWithNoManagersStillSucceedsWithoutMessages()
    {
        var (tenant, setup, _, _) = await SeedAsync(employees: false, admins: false);
        using var _ = tenant;
        var detail = await HireAsync(tenant, HireBody(setup, documentNumber: "6100000009"));

        var response = await TerminateAsync(tenant, detail, "2026-11-30");

        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        Assert.Equal("Retired", (await JsonOfAsync(response)).GetProperty("employee").GetProperty("status").GetString());
        Assert.Empty(await EventsAsync(tenant, ExitEvent));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM hr.exit_exam_requirements WHERE tenant_id = @tenant", tenant));
    }

    [Fact]
    public async Task PayloadsNeverContainClinicalText()
    {
        var (tenant, setup, _, _) = await SeedAsync(employees: false);
        using var _ = tenant;
        var detail = await HireAsync(tenant, HireBody(setup, documentNumber: "6100000009"));
        await tenant.Hr.PostAsJsonAsync(
            $"{EmployeeUrl(tenant, detail)}/medical-exams",
            new { type = "PostIncapacity", examDate = "2026-11-10", provider = SecretText, restrictions = SecretText, recommendations = SecretText, restrictionsUntil = (string?)null },
            Ct);
        await TerminateAsync(tenant, detail, "2026-11-16");

        await ProcessAsync(tenant);

        var payloads = (await EventsAsync(tenant, DigestEvent)).Concat(await EventsAsync(tenant, ExitEvent)).Select(row => row.Payload).ToList();
        Assert.NotEmpty(payloads);
        Assert.All(payloads, payload =>
        {
            Assert.DoesNotContain(SecretText, payload);
            foreach (var type in Enum.GetNames<Modules.Hr.Domain.MedicalExamType>())
            {
                Assert.DoesNotContain(type, payload, StringComparison.OrdinalIgnoreCase);
            }
        });
    }
}
