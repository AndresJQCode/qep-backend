using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>
/// Spec 2026-10-28: <c>GET /messaging/reports/summary</c> contra Postgres. El escenario siembra por SQL con <c>occurred_at</c> a propósito
/// dos días antes de <c>created_at</c>: si el reporte leyera <c>occurred_at</c>, nada caería donde se afirma. «Ahora» es el 28 de octubre
/// de 2026, 15:00 en Bogotá; el periodo del escenario es del 5 al 11 de octubre (día).
/// </summary>
public sealed class MessagingReportApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset NowUtc = new(2026, 10, 28, 20, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset Bog(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.FromHours(-5));

    private sealed record Fixture(
        QepApiFactory Base, WebApplicationFactory<Program> Factory, string ConnectionString, RegisteredTenant Tenant, Guid Connection, Guid InstagramConnection, HttpClient Client)
        : IDisposable
    {
        public string Url(string query) => $"/api/v1/tenants/{Tenant.TenantId}/messaging/reports/summary?{query}";

        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
            Base.Dispose();
        }
    }

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, bool enableMessaging = true, string[]? permissions = null, int line = 1)
    {
        var connectionString = database.GetConnectionString();
        var baseFactory = new QepApiFactory(connectionString);
        var factory = baseFactory.WithTestClock(new TestClock { UtcNow = NowUtc });
        var tenant = await RegisterTenantAsync(factory);
        if (enableMessaging)
        {
            await EnableMessagingAsync(connectionString, tenant.TenantId);
        }

        var whatsApp = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", $"111{line}", $"222{line}");
        var instagram = await SeedInstagramConnectionAsync(factory, tenant.TenantId, "Instagram", $"1784140000000000{line}");
        var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, permissions ?? ReadPermissions);
        return new Fixture(baseFactory, factory, connectionString, tenant, whatsApp, instagram, client);
    }

    private static async Task<Guid> ConversationAsync(
        Fixture f, string key, DateTimeOffset createdAt, bool instagram = false, Guid? assigned = null)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(f.ConnectionString,
            """
            INSERT INTO messaging.conversations (id, tenant_id, connection_id, channel, user_id, wa_id, profile_name, status, unread_count,
                                                 last_activity_at, created_at, updated_at, version, assigned_member_id, assigned_at)
            VALUES (@id, @t, @c, @channel, @user, @wa, @key, 'Open', 0, @at, @at, @at, 1, @m, CASE WHEN @m::uuid IS NULL THEN NULL ELSE @at END)
            """,
            ("id", id), ("t", f.Tenant.TenantId), ("c", instagram ? f.InstagramConnection : f.Connection),
            ("channel", instagram ? "Instagram" : "WhatsApp"),
            ("user", instagram ? key : DBNull.Value), ("wa", instagram ? DBNull.Value : key), ("key", key),
            ("at", createdAt.ToUniversalTime()), ("m", (object?)assigned ?? DBNull.Value));
        return id;
    }

    private static async Task<Guid> MessageAsync(
        Fixture f, Guid conversationId, DateTimeOffset at, short direction, short kind = 1, short sentByKind = 1,
        Guid? member = null, short status = 1, string? details = null, bool instagram = false)
    {
        var id = Guid.CreateVersion7();
        var chatbot = sentByKind == 2 ? Guid.CreateVersion7() : (object)DBNull.Value;
        await ExecuteAsync(f.ConnectionString,
            """
            INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, details,
                                            sent_by_member_id, sent_by_kind, sent_by_chatbot_id, created_at)
            VALUES (@id, @conv, @t, @c, @occurred, @direction, @kind, @status, 'hola', @details::jsonb, @member, @sentByKind, @chatbot, @at)
            """,
            ("id", id), ("conv", conversationId), ("t", f.Tenant.TenantId), ("c", instagram ? f.InstagramConnection : f.Connection),
            ("occurred", at.AddDays(-2).ToUniversalTime()), ("direction", direction), ("kind", kind), ("status", status),
            ("details", (object?)details ?? DBNull.Value), ("member", (object?)member ?? DBNull.Value),
            ("sentByKind", sentByKind), ("chatbot", chatbot), ("at", at.ToUniversalTime()));
        return id;
    }

    private static async Task InboundAsync(Fixture f, Guid conversationId, DateTimeOffset at, bool instagram = false) =>
        await MessageAsync(f, conversationId, at, direction: 1, instagram: instagram);

    private static async Task ReopenedAsync(Fixture f, Guid conversationId, DateTimeOffset at) =>
        await MessageAsync(f, conversationId, at, direction: 3, kind: 13, status: 2, details: """{"type":"Reopened"}""");

    private static async Task<Guid> CampaignAsync(Fixture f, Guid createdBy)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(f.ConnectionString,
            """
            INSERT INTO messaging.campaigns (id, tenant_id, connection_id, name, template_name, template_language, template_category, parameters,
                audience_tag_ids, audience_customer_ids, status, created_by_member_id, created_at, updated_at, version)
            VALUES (@id, @t, @c, 'Promo', 'promo', 'es', 'MARKETING', '{}'::jsonb, ARRAY[@tag]::uuid[], ARRAY[]::uuid[], 'Completed', @member, now(), now(), 1)
            """,
            ("id", id), ("t", f.Tenant.TenantId), ("c", f.Connection), ("tag", Guid.CreateVersion7()), ("member", createdBy));
        return id;
    }

    private static async Task RecipientAsync(Fixture f, Guid campaignId, Guid conversationId, Guid messageId) =>
        await ExecuteAsync(f.ConnectionString,
            """
            INSERT INTO messaging.campaign_recipients (id, campaign_id, tenant_id, customer_id, customer_name, wa_id, status, message_id, conversation_id,
                                                       attempts, next_attempt_at, retry_count)
            VALUES (@id, @campaign, @t, @customer, 'Cliente', '573000000000', 'Sent', @message, @conv, 0, now(), 0)
            """,
            ("id", Guid.CreateVersion7()), ("campaign", campaignId), ("t", f.Tenant.TenantId), ("customer", Guid.CreateVersion7()),
            ("message", messageId), ("conv", conversationId));

    private static async Task<JsonElement> GetOkAsync(Fixture f, string query)
    {
        var response = await f.Client.GetAsync(f.Url(query), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task TheScenarioProducesTheSpecNumbers()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        var (laura, _) = await SeedMemberAsync(f.ConnectionString, f.Tenant.TenantId, "Laura Gómez", "advisor");
        var campaign = await CampaignAsync(f, laura);

        // A: dos sesiones. 1.ª: respuesta de Laura a los 2 min. 2.ª (Reopened): el bot a 1 min, Laura (plantilla de un miembro) a 30 min.
        var a = await ConversationAsync(f, "573000000001", Bog(10, 5, 9));
        await InboundAsync(f, a, Bog(10, 5, 9));
        await MessageAsync(f, a, Bog(10, 5, 9, 2), 2, member: laura);
        await ReopenedAsync(f, a, Bog(10, 7, 9));
        await InboundAsync(f, a, Bog(10, 7, 9));
        await MessageAsync(f, a, Bog(10, 7, 9, 1), 2, sentByKind: 2);
        await MessageAsync(f, a, Bog(10, 7, 9, 30), 2, kind: 11, member: laura);

        // B: asignada a Laura; sólo un envío de campaña y un saliente fallido: sin respuesta que cuente.
        var b = await ConversationAsync(f, "573000000002", Bog(10, 5, 10), assigned: laura);
        await InboundAsync(f, b, Bog(10, 5, 10));
        var campaignMessage = await MessageAsync(f, b, Bog(10, 5, 10, 1), 2, kind: 11, member: laura);
        await RecipientAsync(f, campaign, b, campaignMessage);
        await MessageAsync(f, b, Bog(10, 5, 10, 2), 2, member: laura, status: 4);

        // C: Instagram sin asignar; un eco (saliente sin autor) a los 5 min cuenta como primera respuesta pero no como de asesor.
        var c = await ConversationAsync(f, "17841400000000099", Bog(10, 6, 11), instagram: true);
        await InboundAsync(f, c, Bog(10, 6, 11), instagram: true);
        await MessageAsync(f, c, Bog(10, 6, 11, 5), 2, instagram: true);

        // D: el negocio escribió primero (campaña) y el contacto no ha contestado: cuenta como conversación, sin reloj.
        var d = await ConversationAsync(f, "573000000004", Bog(10, 6, 8));
        var outboundFirst = await MessageAsync(f, d, Bog(10, 6, 8), 2, kind: 11, member: laura);
        await RecipientAsync(f, campaign, d, outboundFirst);

        // E y F: a ambos lados de la medianoche de Bogotá del 11 al 12 (04:59Z y 05:00Z del 12).
        var e = await ConversationAsync(f, "573000000005", new DateTimeOffset(2026, 10, 12, 4, 59, 0, TimeSpan.Zero));
        await InboundAsync(f, e, new DateTimeOffset(2026, 10, 12, 4, 59, 0, TimeSpan.Zero));
        var outside = await ConversationAsync(f, "573000000006", new DateTimeOffset(2026, 10, 12, 5, 0, 0, TimeSpan.Zero));
        await InboundAsync(f, outside, new DateTimeOffset(2026, 10, 12, 5, 0, 0, TimeSpan.Zero));

        // G: contacto de septiembre que vuelve el 8 de octubre y la atiende Laura a los 10 min.
        var g = await ConversationAsync(f, "573000000007", Bog(9, 20, 9));
        await InboundAsync(f, g, Bog(9, 20, 9));
        await ReopenedAsync(f, g, Bog(10, 8, 9));
        await InboundAsync(f, g, Bog(10, 8, 9));
        await MessageAsync(f, g, Bog(10, 8, 9, 10), 2, member: laura);

        // H: periodo anterior (28 de septiembre al 4 de octubre), respondida a los 3 min.
        var h = await ConversationAsync(f, "573000000008", Bog(10, 1, 9));
        await InboundAsync(f, h, Bog(10, 1, 9));
        await MessageAsync(f, h, Bog(10, 1, 9, 3), 2, member: laura);

        var report = await GetOkAsync(f, "from=2026-10-05&to=2026-10-11&groupBy=day");

        Assert.Equal("day", report.GetProperty("period").GetProperty("groupBy").GetString());
        Assert.Equal("America/Bogota", report.GetProperty("period").GetProperty("timeZone").GetString());
        Assert.Equal("2026-09-28", report.GetProperty("previous").GetProperty("from").GetString());
        Assert.Equal("2026-10-04", report.GetProperty("previous").GetProperty("to").GetString());
        Assert.False(report.GetProperty("businessHoursApplied").GetBoolean());

        var current = report.GetProperty("totals").GetProperty("current");
        Assert.Equal(7, current.GetProperty("conversations").GetInt32());
        Assert.Equal(4, current.GetProperty("newContacts").GetInt32());
        Assert.Equal(3, current.GetProperty("newContactsByChannel").GetProperty("whatsApp").GetInt32());
        Assert.Equal(1, current.GetProperty("newContactsByChannel").GetProperty("instagram").GetInt32());
        Assert.Equal(1, current.GetProperty("returningContacts").GetInt32());
        var previous = report.GetProperty("totals").GetProperty("previous");
        Assert.Equal(1, previous.GetProperty("conversations").GetInt32());
        Assert.Equal(1, previous.GetProperty("newContacts").GetInt32());

        var series = report.GetProperty("series").EnumerateArray().ToArray();
        Assert.Equal(7, series.Length);
        Assert.Equal(["2026-10-05", "2026-10-06", "2026-10-07", "2026-10-08", "2026-10-09", "2026-10-10", "2026-10-11"],
            series.Select(p => p.GetProperty("start").GetString()));
        Assert.Equal([2, 2, 1, 1, 0, 0, 1], series.Select(p => p.GetProperty("conversations").GetInt32()));
        Assert.Equal([2, 1, 0, 0, 0, 0, 1], series.Select(p => p.GetProperty("newContacts").GetInt32()));

        var first = report.GetProperty("firstResponse");
        Assert.Equal(4, first.GetProperty("answeredSessions").GetInt32());
        Assert.Equal(2, first.GetProperty("unansweredSessions").GetInt32());
        Assert.Equal(120, first.GetProperty("medianSeconds").GetInt64());
        Assert.Equal(600, first.GetProperty("p90Seconds").GetInt64());
        Assert.Equal(180, first.GetProperty("previous").GetProperty("medianSeconds").GetInt64());
        Assert.Equal([2, 2, 0, 0, 0], first.GetProperty("buckets").EnumerateArray().Select(x => x.GetProperty("count").GetInt32()));

        var advisor = report.GetProperty("advisorResponse");
        Assert.Equal(3, advisor.GetProperty("answeredSessions").GetInt32());
        Assert.Equal(3, advisor.GetProperty("unansweredSessions").GetInt32());
        Assert.Equal(600, advisor.GetProperty("medianSeconds").GetInt64());
        Assert.Equal(1800, advisor.GetProperty("p90Seconds").GetInt64());
        Assert.Equal([1, 1, 1, 0, 0], advisor.GetProperty("buckets").EnumerateArray().Select(x => x.GetProperty("count").GetInt32()));

        var advisors = report.GetProperty("advisors").EnumerateArray().ToArray();
        Assert.Equal(2, advisors.Length);
        Assert.Equal(laura.ToString(), advisors[0].GetProperty("memberId").GetString());
        Assert.Equal("Laura Gómez", advisors[0].GetProperty("displayName").GetString());
        Assert.Equal(3, advisors[0].GetProperty("sessionsAttended").GetInt32());
        Assert.Equal(600, advisors[0].GetProperty("medianAdvisorResponseSeconds").GetInt64());
        Assert.Equal(2.0 / 3.0, advisors[0].GetProperty("answeredUnder15mRatio").GetDouble(), 6);
        Assert.Equal(1, advisors[0].GetProperty("unansweredAssigned").GetInt32());
        Assert.Equal(JsonValueKind.Null, advisors[1].GetProperty("memberId").ValueKind);
        Assert.Equal("Sin asignar", advisors[1].GetProperty("displayName").GetString());
        Assert.Equal(2, advisors[1].GetProperty("unansweredAssigned").GetInt32());

        var instagramOnly = await GetOkAsync(f, "from=2026-10-05&to=2026-10-11&groupBy=day&channel=Instagram");
        Assert.Equal(1, instagramOnly.GetProperty("totals").GetProperty("current").GetProperty("conversations").GetInt32());

        var weekly = await GetOkAsync(f, "from=2026-10-05&to=2026-10-11&groupBy=week");
        Assert.Equal(1, weekly.GetProperty("series").GetArrayLength());
        Assert.Equal(7, weekly.GetProperty("series")[0].GetProperty("conversations").GetInt32());
    }

    private static string[] Channels(JsonElement report) =>
        [.. report.GetProperty("channelsWithData").EnumerateArray().Select(e => e.GetString()!)];

    [Fact]
    public async Task ChannelsWithDataHasOnlyWhatsAppWhenThatIsAllThereIs()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await ConversationAsync(f, "573000000021", Bog(10, 5, 9));

        Assert.Equal(["WhatsApp"], Channels(await GetOkAsync(f, "from=2026-10-05&to=2026-10-11&groupBy=day")));
    }

    [Fact]
    public async Task ChannelsWithDataCountsInstagramFoundOnlyInThePreviousPeriod()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await ConversationAsync(f, "573000000022", Bog(10, 6, 9));
        await ConversationAsync(f, "ig-prev", Bog(9, 30, 9), instagram: true);

        Assert.Equal(["WhatsApp", "Instagram"], Channels(await GetOkAsync(f, "from=2026-10-05&to=2026-10-11&groupBy=day")));
    }

    [Fact]
    public async Task ChannelsWithDataIgnoresTheChannelFilter()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await ConversationAsync(f, "573000000023", Bog(10, 6, 9));
        await ConversationAsync(f, "ig-now", Bog(10, 7, 9), instagram: true);

        var report = await GetOkAsync(f, "from=2026-10-05&to=2026-10-11&groupBy=day&channel=WhatsApp");

        Assert.Equal(["WhatsApp", "Instagram"], Channels(report));
        Assert.Equal(1, report.GetProperty("totals").GetProperty("current").GetProperty("conversations").GetInt32());
    }

    [Fact]
    public async Task ChannelsWithDataCountsAReopenedSessionOfAnOlderConversation()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        var old = await ConversationAsync(f, "ig-old", Bog(8, 1, 9), instagram: true);
        await ReopenedAsync(f, old, Bog(10, 8, 9));

        Assert.Equal(["Instagram"], Channels(await GetOkAsync(f, "from=2026-10-05&to=2026-10-11&groupBy=day")));
    }

    [Fact]
    public async Task ChannelsWithDataIsEmptyWithoutData()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await ConversationAsync(f, "573000000024", Bog(8, 1, 9));

        Assert.Empty(Channels(await GetOkAsync(f, "from=2026-10-05&to=2026-10-11&groupBy=day")));
    }

    [Fact]
    public async Task ChannelsWithDataDoesNotLeakAcrossTenants()
    {
        await using var database = await StartDatabaseAsync();
        using var mine = await ArrangeAsync(database);
        using var other = await ArrangeAsync(database, line: 2);
        await ConversationAsync(other, "ig-other", Bog(10, 6, 9), instagram: true);
        await ConversationAsync(mine, "573000000025", Bog(10, 6, 9));

        Assert.Equal(["WhatsApp"], Channels(await GetOkAsync(mine, "from=2026-10-05&to=2026-10-11&groupBy=day")));
        Assert.Equal(["Instagram"], Channels(await GetOkAsync(other, "from=2026-10-05&to=2026-10-11&groupBy=day")));
    }

    [Fact]
    public async Task WithADefaultScheduleTheTimesCountOnlyBusinessHours()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        var (laura, _) = await SeedMemberAsync(f.ConnectionString, f.Tenant.TenantId, "Laura Gómez", "advisor");

        // Lunes 5 de octubre: el contacto escribe a las 07:00 (cerrado) y se le responde a las 08:10 (abierto desde las 08:00).
        var conversation = await ConversationAsync(f, "573000000010", Bog(10, 5, 7));
        await InboundAsync(f, conversation, Bog(10, 5, 7));
        await MessageAsync(f, conversation, Bog(10, 5, 8, 10), 2, member: laura);

        var without = await GetOkAsync(f, "from=2026-10-05&to=2026-10-11&groupBy=day");
        Assert.False(without.GetProperty("businessHoursApplied").GetBoolean());
        Assert.Equal(4200, without.GetProperty("firstResponse").GetProperty("medianSeconds").GetInt64());

        using (var scope = f.Factory.Services.CreateScope())
        {
            var weekly = Enumerable.Range(1, 7).Select(day => new WeekdayHours(day, day <= 5 ? [new TimeRange(480, 1080)] : [])).ToList();
            var schedule = BusinessHoursSchedule.Create(Guid.CreateVersion7(), f.Tenant.TenantId, "Oficina", weekly, [], null, new DateOnly(2026, 10, 1), NowUtc);
            schedule.MakeDefault(NowUtc);
            scope.ServiceProvider.GetRequiredService<IBusinessHoursRepository>().Add(schedule);
            await scope.ServiceProvider.GetRequiredService<IMessagingUnitOfWork>().SaveChangesAsync(Ct);
        }

        var with = await GetOkAsync(f, "from=2026-10-05&to=2026-10-11&groupBy=day");
        Assert.True(with.GetProperty("businessHoursApplied").GetBoolean());
        Assert.Equal(600, with.GetProperty("firstResponse").GetProperty("medianSeconds").GetInt64());
        Assert.Equal(600, with.GetProperty("advisorResponse").GetProperty("medianSeconds").GetInt64());
    }

    [Fact]
    public async Task WithoutThePermissionItIsForbiddenAndBadRangesAreUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        using var noPermission = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId);

        var forbidden = await noPermission.GetAsync(f.Url("from=2026-10-05&to=2026-10-11&groupBy=day"), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var tooLong = await f.Client.GetAsync(f.Url("from=2026-01-01&to=2026-04-03&groupBy=day"), Ct);
        Assert.True(tooLong.StatusCode == HttpStatusCode.UnprocessableEntity, await tooLong.Content.ReadAsStringAsync(Ct));
        Assert.Equal("messaging.report.range_too_large", (await ProblemAsync(tooLong)).Code);

        var justEnough = await f.Client.GetAsync(f.Url("from=2026-01-01&to=2026-04-02&groupBy=day"), Ct);
        Assert.Equal(HttpStatusCode.OK, justEnough.StatusCode);

        var badGrouping = await f.Client.GetAsync(f.Url("from=2026-10-05&to=2026-10-11&groupBy=year"), Ct);
        Assert.True(badGrouping.StatusCode == HttpStatusCode.UnprocessableEntity, "grouping: " + await badGrouping.Content.ReadAsStringAsync(Ct));
        Assert.Contains("groupBy", (await ProblemAsync(badGrouping)).ErrorKeys);

        var missingFrom = await f.Client.GetAsync(f.Url("to=2026-10-11&groupBy=day"), Ct);
        Assert.True(missingFrom.StatusCode == HttpStatusCode.UnprocessableEntity, "from: " + await missingFrom.Content.ReadAsStringAsync(Ct));
        Assert.Contains("from", (await ProblemAsync(missingFrom)).ErrorKeys);
    }

    [Fact]
    public async Task WithTheMessagingModuleOffItIsForbiddenWithTheModuleCode()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database, enableMessaging: false);

        var response = await f.Client.GetAsync(f.Url("from=2026-10-05&to=2026-10-11&groupBy=day"), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(response)).Code);
    }
}
