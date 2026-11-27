using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Authorization.Application;
using Modules.Hr.Application.BirthdayNotices;
using Modules.Hr.Domain;
using Modules.Hr.Infrastructure.Persistence;
using Npgsql;
using static Modules.Hr.IntegrationTests.HrApiHarness;
using static Modules.Hr.IntegrationTests.HrApiSeeds;

namespace Modules.Hr.IntegrationTests;

/// <summary>
/// Spec 2026-11-26 §5.2 (the daily birthday run). The processor is driven directly, with the clock of the fixture (10:00 in Bogota on
/// 2026-11-20, two hours after <c>SendHourLocal = 8</c>). The events are read from the platform outbox. The payload carries names
/// only: never the birth date, the age or the year.
/// </summary>
public sealed class BirthdayNoticesRunTests : IClassFixture<HrApiFixture>
{
    private const string DigestEvent = "hr.birthdays.digest.v1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly HrApiFixture _fixture;

    public BirthdayNoticesRunTests(HrApiFixture fixture) => _fixture = fixture;

    private sealed record Outbox(string CorrelationId, string Payload);

    private async Task ProcessAsync(HrTenant tenant)
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IHrBirthdayNoticesProcessor>().ProcessTenantAsync(tenant.Id, Ct);
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connectionString = _fixture.Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }

    private async Task<List<Outbox>> EventsAsync(HrTenant tenant)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT correlation_id, payload::text FROM platform.outbox_messages
            WHERE event_name = @event AND payload->>'tenantId' = @tenant ORDER BY correlation_id
            """,
            connection);
        command.Parameters.AddWithValue("event", DigestEvent);
        command.Parameters.AddWithValue("tenant", tenant.Id.ToString());
        var rows = new List<Outbox>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new Outbox(reader.GetString(0), reader.GetString(1)));
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

    private static async Task<Guid> HireWithBirthdayAsync(HrTenant tenant, SeededSetup setup, string document, string first, string last, string? birthDate)
    {
        var body = HireBody(setup, documentNumber: document, firstName: first, lastName: last);
        body["birthDate"] = birthDate;
        var detail = await HrApiSeeds.HireAsync(tenant, body);
        return detail.GetProperty("employee").GetProperty("id").GetGuid();
    }

    /// <summary>The setting is inserted directly: an advisor cannot be saved through the API, and the run must still skip one.</summary>
    private async Task SaveSettingAsync(
        HrTenant tenant, Guid author, IReadOnlyList<Guid> managers, int leadDays = 1, bool whatsApp = false, Guid? connection = null, bool email = true)
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HrDbContext>();
        db.BirthdayNoticeSettings.Add(BirthdayNoticeSetting.Create(
            tenant.Id,
            new BirthdayNoticeValues(managers, leadDays, whatsApp, connection, email),
            author,
            HrApiFixture.DefaultNow));
        await db.SaveChangesAsync(Ct);
    }

    private sealed record Seeded(HrTenant Tenant, SeededSetup Setup, Guid Admin, Guid Advisor);

    /// <summary>A tenant with the factory roles, an admin (reads employees) and an advisor (does not), and no employees yet.</summary>
    private async Task<Seeded> SeedAsync()
    {
        var tenant = await NewTenantAsync(_fixture);
        await using (var seeding = _fixture.Factory.Services.CreateAsyncScope())
        {
            await seeding.ServiceProvider.GetRequiredService<ITenantRoleSeeder>().SeedAsync(tenant.Id, Ct);
        }

        var setup = await SeedSetupAsync(tenant);
        var admin = await SeedMemberAsync(_fixture.Factory, $"b1-{tenant.Id:N}@example.com", "Admin Uno", "admin", tenantId: tenant.IdText);
        var advisor = await SeedMemberAsync(_fixture.Factory, $"b2-{tenant.Id:N}@example.com", "Asesor", "advisor", tenantId: tenant.IdText);
        return new Seeded(tenant, setup, admin, advisor);
    }

    /// <summary>Ana has her birthday today (2026-11-20), Luis tomorrow, Carla on the 25th.</summary>
    private static async Task HireThreeAsync(Seeded seeded)
    {
        await HireWithBirthdayAsync(seeded.Tenant, seeded.Setup, "6200000001", "Ana", "Pérez", "1990-11-20");
        await HireWithBirthdayAsync(seeded.Tenant, seeded.Setup, "6200000002", "Luis", "Gómez", "1985-11-21");
        await HireWithBirthdayAsync(seeded.Tenant, seeded.Setup, "6200000003", "Carla", "Ruiz", "1992-11-25");
    }

    [Fact]
    public async Task PublishesOneEventPerManagerWithHrAccess()
    {
        var seeded = await SeedAsync();
        using var tenant = seeded.Tenant;
        await HireThreeAsync(seeded);
        await SaveSettingAsync(tenant, seeded.Admin, [seeded.Admin, seeded.Advisor]);

        await ProcessAsync(tenant);

        var events = await EventsAsync(tenant);
        var only = Assert.Single(events);
        Assert.Equal($"birthdays:{tenant.Id:D}:{seeded.Admin:D}:2026-11-20", only.CorrelationId);
        using var payload = JsonDocument.Parse(only.Payload);
        var root = payload.RootElement;
        Assert.Equal(tenant.Id, root.GetProperty("tenantId").GetGuid());
        Assert.Equal(seeded.Admin, root.GetProperty("memberId").GetGuid());
        Assert.Equal("2026-11-20", root.GetProperty("runDate").GetString());
        Assert.Equal("Ana Pérez", Assert.Single(root.GetProperty("today").EnumerateArray()).GetProperty("employeeName").GetString());
        Assert.Equal("Luis Gómez", Assert.Single(root.GetProperty("upcoming").EnumerateArray()).GetProperty("employeeName").GetString());
        Assert.Equal(1, root.GetProperty("todayTotal").GetInt32());
        Assert.Equal(1, root.GetProperty("upcomingTotal").GetInt32());
        Assert.Equal("2026-11-21", root.GetProperty("upcomingDate").GetString());
        var channels = root.GetProperty("channels");
        Assert.Equal(JsonValueKind.Null, channels.GetProperty("whatsAppConnectionId").ValueKind);
        Assert.True(channels.GetProperty("email").GetBoolean());
        Assert.Equal(seeded.Admin, channels.GetProperty("sentByMemberId").GetGuid());
        // Midnight of the next local day (Bogota, UTC-5) as UTC.
        Assert.Equal(new DateTimeOffset(2026, 11, 21, 5, 0, 0, TimeSpan.Zero), root.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM hr.birthday_notice_digests WHERE tenant_id = @tenant AND status = 'Sent'", tenant));
    }

    [Fact]
    public async Task WhatsAppConnectionTravelsOnlyWhenWhatsAppIsEnabled()
    {
        var seeded = await SeedAsync();
        using var tenant = seeded.Tenant;
        await HireWithBirthdayAsync(tenant, seeded.Setup, "6200000001", "Ana", "Pérez", "1990-11-20");
        var connection = Guid.NewGuid();
        await SaveSettingAsync(tenant, seeded.Admin, [seeded.Admin], whatsApp: true, connection: connection, email: false);

        await ProcessAsync(tenant);

        using var payload = JsonDocument.Parse(Assert.Single(await EventsAsync(tenant)).Payload);
        var channels = payload.RootElement.GetProperty("channels");
        Assert.Equal(connection, channels.GetProperty("whatsAppConnectionId").GetGuid());
        Assert.False(channels.GetProperty("email").GetBoolean());
    }

    // Moves the member to the advisor role, which has no hr.employee.read: the same revocation a role change does in the product.
    private async Task RevokeHrAccessAsync(HrTenant tenant, Guid memberId)
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<Modules.Tenancy.Infrastructure.Persistence.TenancyDbContext>();
        var tenantEntity = await tenancy.Tenants.FindAsync([new Modules.Tenancy.Domain.TenantId(tenant.Id)], Ct);
        var membership = await tenancy.Memberships.FirstAsync(candidate => candidate.Id == new Modules.Tenancy.Domain.MembershipId(memberId), Ct);
        membership.ChangeRoles(tenantEntity!, ["advisor"], HrApiFixture.DefaultNow);
        await tenancy.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task ManagerWhoLostAccessIsSkipped()
    {
        var seeded = await SeedAsync();
        using var tenant = seeded.Tenant;
        await HireThreeAsync(seeded);
        // The setting is saved while the admin reads employees; afterwards the role changes to one that does not.
        await SaveSettingAsync(tenant, seeded.Admin, [seeded.Admin]);
        await RevokeHrAccessAsync(tenant, seeded.Admin);

        await ProcessAsync(tenant);

        Assert.Empty(await EventsAsync(tenant));
    }

    [Fact]
    public async Task NothingToAnnouncePublishesNothing()
    {
        var seeded = await SeedAsync();
        using var tenant = seeded.Tenant;
        await HireWithBirthdayAsync(tenant, seeded.Setup, "6200000003", "Carla", "Ruiz", "1992-11-25");
        await SaveSettingAsync(tenant, seeded.Admin, [seeded.Admin]);

        await ProcessAsync(tenant);

        Assert.Empty(await EventsAsync(tenant));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM hr.birthday_notice_runs WHERE tenant_id = @tenant AND finished_at IS NOT NULL", tenant));
    }

    [Fact]
    public async Task RetiredEmployeeIsNotAnnounced()
    {
        var seeded = await SeedAsync();
        using var tenant = seeded.Tenant;
        var detail = await HrApiSeeds.HireAsync(tenant, WithBirthDate(HireBody(seeded.Setup, documentNumber: "6200000001"), "1990-11-20"));
        var contractUrl =
            $"{EmployeeUrl(tenant, detail)}/contracts/{detail.GetProperty("contract").GetProperty("id").GetGuid()}";
        var terminated = await tenant.Hr.PostAsJsonAsync(
            $"{contractUrl}/terminate", new { reason = "Resignation", date = "2026-11-19", notes = (string?)null }, Ct);
        Assert.Equal(HttpStatusCode.OK, terminated.StatusCode);
        await SaveSettingAsync(tenant, seeded.Admin, [seeded.Admin]);

        await ProcessAsync(tenant);

        Assert.Empty(await EventsAsync(tenant));
    }

    [Fact]
    public async Task EmployeeWithoutBirthDateIsIgnored()
    {
        var seeded = await SeedAsync();
        using var tenant = seeded.Tenant;
        await HireWithBirthdayAsync(tenant, seeded.Setup, "6200000001", "Ana", "Pérez", birthDate: null);
        await SaveSettingAsync(tenant, seeded.Admin, [seeded.Admin]);

        await ProcessAsync(tenant);

        Assert.Empty(await EventsAsync(tenant));
    }

    [Fact]
    public async Task RunsOncePerDay()
    {
        var seeded = await SeedAsync();
        using var tenant = seeded.Tenant;
        await HireThreeAsync(seeded);
        await SaveSettingAsync(tenant, seeded.Admin, [seeded.Admin]);

        await ProcessAsync(tenant);
        await ProcessAsync(tenant);

        Assert.Single(await EventsAsync(tenant));

        // The next day is a new run (Luis has his birthday then).
        _fixture.Clock.UtcNow = HrApiFixture.DefaultNow.AddDays(1);
        await ProcessAsync(tenant);
        Assert.Equal(2, (await EventsAsync(tenant)).Count);
    }

    [Fact]
    public async Task BeforeSendHourDoesNothing()
    {
        var seeded = await SeedAsync();
        using var tenant = seeded.Tenant;
        await HireThreeAsync(seeded);
        await SaveSettingAsync(tenant, seeded.Admin, [seeded.Admin]);
        // 11:00 UTC is 06:00 in Bogota, before SendHourLocal = 8.
        _fixture.Clock.UtcNow = new DateTimeOffset(2026, 11, 20, 11, 0, 0, TimeSpan.Zero);

        await ProcessAsync(tenant);

        Assert.Empty(await EventsAsync(tenant));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM hr.birthday_notice_runs WHERE tenant_id = @tenant", tenant));
        _fixture.Clock.UtcNow = new DateTimeOffset(2026, 11, 20, 13, 0, 0, TimeSpan.Zero);
        await ProcessAsync(tenant);
        Assert.Single(await EventsAsync(tenant));
    }

    [Fact]
    public async Task NoSettingExitsBeforeClaimingTheRun()
    {
        var seeded = await SeedAsync();
        using var tenant = seeded.Tenant;
        await HireThreeAsync(seeded);

        await ProcessAsync(tenant);

        Assert.Empty(await EventsAsync(tenant));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM hr.birthday_notice_runs WHERE tenant_id = @tenant", tenant));
    }

    [Fact]
    public async Task PayloadHasNoBirthDate()
    {
        var seeded = await SeedAsync();
        using var tenant = seeded.Tenant;
        await HireThreeAsync(seeded);
        await SaveSettingAsync(tenant, seeded.Admin, [seeded.Admin]);

        await ProcessAsync(tenant);

        var payload = Assert.Single(await EventsAsync(tenant)).Payload;
        Assert.DoesNotContain("birthDate", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1990", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("1985", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MedicalRunIsUntouched()
    {
        var seeded = await SeedAsync();
        using var tenant = seeded.Tenant;
        await HireThreeAsync(seeded);
        await SaveSettingAsync(tenant, seeded.Admin, [seeded.Admin]);

        await ProcessAsync(tenant);

        Assert.Equal(0, await CountAsync("SELECT count(*) FROM hr.tenant_runs WHERE tenant_id = @tenant", tenant));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM hr.daily_digests WHERE tenant_id = @tenant", tenant));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM hr.birthday_notice_runs WHERE tenant_id = @tenant", tenant));
    }

    private static Dictionary<string, object?> WithBirthDate(Dictionary<string, object?> body, string birthDate)
    {
        body["birthDate"] = birthDate;
        return body;
    }
}
