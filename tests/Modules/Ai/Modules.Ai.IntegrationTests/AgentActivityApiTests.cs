using System.Net;
using System.Text.Json;
using Modules.Ai.Application;
using static Modules.Ai.IntegrationTests.AiApiHarness;

namespace Modules.Ai.IntegrationTests;

/// <summary>
/// Spec 2026-10-29 §5 y plan 2026-11-12 Tarea 9 (AI-B8), contra el esquema migrado: la actividad del agente en una conversación (proveedor y
/// modelo servido por respuesta, fuentes y herramientas), el historial de sesiones y los conteos de la lista (tokens del mes por proveedor
/// con caché, C-4, y lo del día).
/// </summary>
public sealed class AgentActivityApiTests
{
    private static readonly string[] Manage = [AiPermissions.AgentRead, AiPermissions.AgentManage];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seeded(QepApiFactory Factory, string ConnectionString, HttpClient Client, Guid AgentId, Guid VersionId, Guid ConnectionId);

    [Fact]
    public async Task TheConversationActivityShowsProviderServedModelsSourcesAndTools()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var seeded = await SeedAsync(factory, database.GetConnectionString());
        using var _ = seeded.Client;
        var conversation = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var session = await SessionAsync(seeded, conversation, "Live", now);
        var job = await JobAsync(seeded, session, "Done", "replied", now);
        var message = Guid.CreateVersion7();
        await TurnAsync(seeded, session, 2, "Agent", message, job, """["envios","call_1"]""", now);
        await ToolCallAsync(seeded, job, "call_1", AgentToolNames.SearchCatalog, now);
        await UsageAsync(seeded, session, conversation, job, "claude-sonnet-5-5", 1000, 50, 900, 100, now);
        await UsageAsync(seeded, session, conversation, job, "claude-opus-5-5", 1200, 70, null, 1000, now);

        var response = await seeded.Client.GetAsync($"{AiUrl()}/conversations/{conversation}/agent-activity", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var activity = await JsonAsync(response);
        var only = Assert.Single(activity.GetProperty("sessions").EnumerateArray());
        Assert.Equal(("Asesor virtual", "anthropic", "claude-sonnet-5-5"), (only.GetProperty("agentName").GetString(), only.GetProperty("provider").GetString(), only.GetProperty("model").GetString()));
        var reply = Assert.Single(only.GetProperty("responses").EnumerateArray());
        Assert.Equal(("anthropic", "replied"), (reply.GetProperty("provider").GetString(), reply.GetProperty("outcome").GetString()));
        Assert.Equal(["claude-sonnet-5-5", "claude-opus-5-5"], reply.GetProperty("servedModels").EnumerateArray().Select(model => model.GetString()));
        Assert.Equal([message], reply.GetProperty("messageIds").EnumerateArray().Select(id => id.GetGuid()));
        Assert.Equal(
            [("faq", "envios", "¿Hacen envíos?"), ("tool", "call_1", AgentToolNames.SearchCatalog)],
            reply.GetProperty("sources").EnumerateArray().Select(source =>
                (source.GetProperty("kind").GetString(), source.GetProperty("id").GetString(), source.GetProperty("label").GetString())));
        Assert.Equal(AgentToolNames.SearchCatalog, Assert.Single(reply.GetProperty("toolCalls").EnumerateArray()).GetProperty("tool").GetString());
        var usage = reply.GetProperty("usage");
        Assert.Equal(
            (2, 2200L, 120L, 900L, 1100L),
            (usage.GetProperty("calls").GetInt32(), usage.GetProperty("inputTokens").GetInt64(), usage.GetProperty("outputTokens").GetInt64(),
             usage.GetProperty("cacheCreationInputTokens").GetInt64(), usage.GetProperty("cacheReadInputTokens").GetInt64()));
    }

    // C-4 y spec §5: la lista suma el mes por proveedor con caché (null cuando nadie la reporta) y cuenta lo del día del tenant.
    [Fact]
    public async Task TheAgentListSumsTheMonthByProviderAndCountsToday()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var seeded = await SeedAsync(factory, database.GetConnectionString());
        using var _ = seeded.Client;
        var now = DateTimeOffset.UtcNow;
        var handedOff = await SessionAsync(seeded, Guid.CreateVersion7(), "HandedOff", now);
        var draftJob = await JobAsync(seeded, handedOff, "Done", "handed_off", now);
        await ToolCallAsync(seeded, draftJob, "call_q", AgentToolNames.CreateQuotationDraft, now);
        await UsageAsync(seeded, handedOff, null, draftJob, "claude-sonnet-5-5", 1000, 50, 900, 100, now);
        var live = await SessionAsync(seeded, Guid.CreateVersion7(), "Live", now);
        var liveJob = await JobAsync(seeded, live, "Done", "replied", now);
        await UsageAsync(seeded, live, null, liveJob, "claude-sonnet-5-5", 500, 20, null, 300, now);
        // De hace dos meses: no entra en el mes.
        var old = await SessionAsync(seeded, Guid.CreateVersion7(), "Abandoned", now.AddDays(-62));
        await UsageAsync(seeded, old, null, await JobAsync(seeded, old, "Done", "replied", now.AddDays(-62)), "claude-sonnet-5-5", 9999, 9999, null, null, now.AddDays(-62));

        var list = await JsonAsync(await seeded.Client.GetAsync($"{AiUrl()}/agents", Ct));

        var agent = Assert.Single(list.GetProperty("items").EnumerateArray());
        var month = Assert.Single(agent.GetProperty("usageThisMonth").EnumerateArray());
        Assert.Equal(
            ("anthropic", 2, 1500L, 70L, 900L, 400L),
            (month.GetProperty("provider").GetString(), month.GetProperty("calls").GetInt32(), month.GetProperty("inputTokens").GetInt64(),
             month.GetProperty("outputTokens").GetInt64(), month.GetProperty("cacheCreationInputTokens").GetInt64(),
             month.GetProperty("cacheReadInputTokens").GetInt64()));
        var today = agent.GetProperty("today");
        Assert.Equal(
            (2, 1, 1),
            (today.GetProperty("handled").GetInt32(), today.GetProperty("handedOff").GetInt32(), today.GetProperty("quotationDrafts").GetInt32()));
        var total = Assert.Single(list.GetProperty("usageThisMonth").EnumerateArray());
        Assert.Equal(1500L, total.GetProperty("inputTokens").GetInt64());
    }

    // Spec 2026-11-23 §9.5: cuenta las reservas hechas hoy en canales reales; ni la repetida (alreadyBooked: true) ni la del playground.
    [Fact]
    public async Task TodayCountsBookingsButNotRepeats()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var seeded = await SeedAsync(factory, database.GetConnectionString());
        using var _ = seeded.Client;
        var now = DateTimeOffset.UtcNow;
        var session = await SessionAsync(seeded, Guid.CreateVersion7(), "Live", now);
        var job = await JobAsync(seeded, session, "Done", "replied", now);
        await ToolCallAsync(seeded, job, "call_b1", AgentToolNames.BookAppointment, now, """{"appointmentId":"a","alreadyBooked":false}""");
        await ToolCallAsync(seeded, job, "call_b2", AgentToolNames.BookAppointment, now, """{"appointmentId":"a","alreadyBooked":true}""");
        var playgroundJob = Guid.CreateVersion7();
        await ExecuteAsync(
            seeded.ConnectionString,
            """
            INSERT INTO ai.model_jobs (id, tenant_id, test_id, up_to_seq, status, attempts, next_attempt_at, outcome, created_at, completed_at)
            VALUES (@id, @tenant, @test, 1, 'Done', 1, @at, 'replied', @at, @at)
            """,
            ("id", playgroundJob), ("tenant", Guid.Parse(TenantId)), ("test", Guid.CreateVersion7()), ("at", now));
        await ToolCallAsync(seeded, playgroundJob, "call_b3", AgentToolNames.BookAppointment, now, """{"appointmentId":"PRUEBA-1","alreadyBooked":false}""");

        var list = await JsonAsync(await seeded.Client.GetAsync($"{AiUrl()}/agents", Ct));

        var today = Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("today");
        Assert.Equal(1, today.GetProperty("appointments").GetInt32());
    }

    [Fact]
    public async Task TheSessionHistoryFiltersByAgentAndRangeNewestFirst()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var seeded = await SeedAsync(factory, database.GetConnectionString());
        using var _ = seeded.Client;
        var now = DateTimeOffset.UtcNow;
        var older = await SessionAsync(seeded, Guid.CreateVersion7(), "HandedOff", now.AddHours(-3));
        var newer = await SessionAsync(seeded, Guid.CreateVersion7(), "Live", now.AddHours(-1));
        var job = await JobAsync(seeded, newer, "Done", "replied", now);
        await TurnAsync(seeded, newer, 2, "Agent", Guid.CreateVersion7(), job, "[]", now);
        await TurnAsync(seeded, newer, 3, "Agent", Guid.CreateVersion7(), job, "[]", now);
        await UsageAsync(seeded, newer, null, job, "claude-sonnet-5-5", 100, 10, null, null, now);
        await SessionAsync(seeded, Guid.CreateVersion7(), "Abandoned", now.AddDays(-10));

        // Plan 2026-11-17 A5: from es un día del tenant; dos días atrás cubre las de hace horas en cualquier zona y deja afuera la de hace 10.
        var from = DateOnly.FromDateTime(now.AddDays(-2).UtcDateTime).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var history = await JsonAsync(await seeded.Client.GetAsync($"{AiUrl()}/sessions?agentId={seeded.AgentId}&from={from}", Ct));

        var items = history.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([newer, older], items.Select(item => item.GetProperty("id").GetGuid()));
        // Una respuesta partida en dos mensajes es una respuesta.
        Assert.Equal((1, "Live"), (items[0].GetProperty("replies").GetInt32(), items[0].GetProperty("status").GetString()));
        var usage = Assert.Single(items[0].GetProperty("usage").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("cacheReadInputTokens").ValueKind);
        Assert.Empty(items[1].GetProperty("usage").EnumerateArray());
    }

    [Fact]
    public async Task ARangeThatEndsBeforeItStartsIs422AndAnotherTenantSeesNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var seeded = await SeedAsync(factory, database.GetConnectionString());
        using var _ = seeded.Client;
        var conversation = Guid.CreateVersion7();
        await SessionAsync(seeded, conversation, "Live", DateTimeOffset.UtcNow);

        var invalid = await seeded.Client.GetAsync($"{AiUrl()}/sessions?from=2026-11-15&to=2026-11-01", Ct);
        using var reader = CreateClient(factory, permissions: [AiPermissions.AgentRead]);
        var other = await reader.GetAsync($"{AiUrl(OtherTenantId)}/conversations/{conversation}/agent-activity", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
    }

    // Plan 2026-11-17 A5 (F-6): from y to son días del tenant (Bogotá, UTC-5), los dos inclusivos, y el historial se pagina de a 50 con
    // nextCursor sin repetir ni saltar sesiones; ya no hay un tope silencioso.
    [Fact]
    public async Task TheSessionsHistoryPagesByTenantDays()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var seeded = await SeedAsync(factory, database.GetConnectionString());
        using var _ = seeded.Client;
        // El 10 de noviembre en Bogotá va de 2026-11-10T05:00Z a 2026-11-11T05:00Z (exclusivo).
        var dayStart = new DateTimeOffset(2026, 11, 10, 5, 0, 0, TimeSpan.Zero);
        var inside = new List<Guid>();
        for (var index = 0; index < 52; index++)
        {
            // De a dos por minuto: el id desempata las del mismo instante.
            inside.Add(await SessionAsync(seeded, Guid.CreateVersion7(), "HandedOff", dayStart.AddMinutes(index / 2)));
        }

        await SessionAsync(seeded, Guid.CreateVersion7(), "HandedOff", dayStart.AddTicks(-10)); // 1 µs antes: Postgres guarda microsegundos.
        await SessionAsync(seeded, Guid.CreateVersion7(), "HandedOff", dayStart.AddDays(1));
        inside.Add(await SessionAsync(seeded, Guid.CreateVersion7(), "HandedOff", dayStart.AddDays(1).AddTicks(-10)));

        var first = await JsonAsync(await seeded.Client.GetAsync($"{AiUrl()}/sessions?from=2026-11-10&to=2026-11-10", Ct));
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);
        var second = await JsonAsync(await seeded.Client.GetAsync(
            $"{AiUrl()}/sessions?from=2026-11-10&to=2026-11-10&cursor={Uri.EscapeDataString(cursor)}", Ct));

        var firstIds = first.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();
        var secondIds = second.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(50, firstIds.Count);
        Assert.Equal(3, secondIds.Count);
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
        Assert.Equal(inside.Order(), firstIds.Concat(secondIds).Order());
        var startedAt = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray())
            .Select(item => item.GetProperty("startedAt").GetDateTimeOffset()).ToList();
        Assert.Equal(startedAt.OrderDescending(), startedAt);

        var forged = await seeded.Client.GetAsync($"{AiUrl()}/sessions?cursor=no-es-un-cursor", Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, forged.StatusCode);
    }

    // Spec 2026-11-17 §7: el historial dice el origen de cada sesión y, en una de bloque, el chatbot (nombre de ahora, leído de Automations)
    // y la salida por la que volvió al flujo.
    [Fact]
    public async Task TheSessionHistoryShowsTheChatbotOfABlockSession()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var seeded = await SeedAsync(factory, database.GetConnectionString());
        using var _ = seeded.Client;
        var now = DateTimeOffset.UtcNow;
        var chatbot = Guid.CreateVersion7();
        await ExecuteAsync(
            seeded.ConnectionString,
            """
            INSERT INTO automations.chatbots (id, tenant_id, name, draft_flow, draft_updated_at, draft_updated_by, created_at, created_by, updated_at)
            VALUES (@chatbot, @tenant, 'Bienvenida', '{}'::jsonb, now(), @tenant, now(), @tenant, now());
            """,
            ("chatbot", chatbot), ("tenant", Guid.Parse(TenantId)));
        var connection = await SessionAsync(seeded, Guid.CreateVersion7(), "HandedOff", now.AddHours(-2));
        var block = Guid.CreateVersion7();
        await ExecuteAsync(
            seeded.ConnectionString,
            """
            INSERT INTO ai.sessions (id, tenant_id, conversation_id, connection_id, agent_id, agent_version_id, status, next_seq, started_at, updated_at,
                                     ended_at, end_reason, failure_code, origin, delegation_id, chatbot_id, run_id, idle_minutes)
            VALUES (@id, @tenant, gen_random_uuid(), @connection, @agent, @version, 'Returned', 3, @at, @at, @at, 'done', 'objective_completed',
                    'ChatbotBlock', gen_random_uuid(), @chatbot, gen_random_uuid(), 60)
            """,
            ("id", block), ("tenant", Guid.Parse(TenantId)), ("connection", seeded.ConnectionId), ("agent", seeded.AgentId),
            ("version", seeded.VersionId), ("at", now.AddHours(-1)), ("chatbot", chatbot));

        var history = await JsonAsync(await seeded.Client.GetAsync($"{AiUrl()}/sessions?agentId={seeded.AgentId}", Ct));

        var items = history.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([block, connection], items.Select(item => item.GetProperty("id").GetGuid()));
        Assert.Equal(("ChatbotBlock", "done", "Returned"), (items[0].GetProperty("origin").GetString(), items[0].GetProperty("output").GetString(), items[0].GetProperty("status").GetString()));
        Assert.Equal((chatbot, "Bienvenida"), (items[0].GetProperty("chatbot").GetProperty("id").GetGuid(), items[0].GetProperty("chatbot").GetProperty("name").GetString()));
        Assert.Equal("Connection", items[1].GetProperty("origin").GetString());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("chatbot").ValueKind);
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("output").ValueKind);
    }

    // Spec 2026-11-17 §7: la actividad de la conversación trae origin, chatbot y output con los mismos nombres y la misma regla que /sessions.
    [Fact]
    public async Task TheConversationActivityShowsTheOriginOfABlockSession()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var seeded = await SeedAsync(factory, database.GetConnectionString());
        using var _ = seeded.Client;
        var now = DateTimeOffset.UtcNow;
        var chatbot = Guid.CreateVersion7();
        var conversation = Guid.CreateVersion7();
        await ExecuteAsync(
            seeded.ConnectionString,
            """
            INSERT INTO automations.chatbots (id, tenant_id, name, draft_flow, draft_updated_at, draft_updated_by, created_at, created_by, updated_at)
            VALUES (@chatbot, @tenant, 'Bienvenida', '{}'::jsonb, now(), @tenant, now(), @tenant, now());
            """,
            ("chatbot", chatbot), ("tenant", Guid.Parse(TenantId)));
        await ExecuteAsync(
            seeded.ConnectionString,
            """
            INSERT INTO ai.sessions (id, tenant_id, conversation_id, connection_id, agent_id, agent_version_id, status, next_seq, started_at, updated_at,
                                     ended_at, end_reason, failure_code, origin, delegation_id, chatbot_id, run_id, idle_minutes)
            VALUES (gen_random_uuid(), @tenant, @conversation, @connection, @agent, @version, 'Returned', 3, @at, @at, @at, 'done', 'objective_completed',
                    'ChatbotBlock', gen_random_uuid(), @chatbot, gen_random_uuid(), 60)
            """,
            ("tenant", Guid.Parse(TenantId)), ("conversation", conversation), ("connection", seeded.ConnectionId), ("agent", seeded.AgentId),
            ("version", seeded.VersionId), ("at", now.AddHours(-1)), ("chatbot", chatbot));

        var activity = await JsonAsync(await seeded.Client.GetAsync($"{AiUrl()}/conversations/{conversation}/agent-activity", Ct));

        var session = Assert.Single(activity.GetProperty("sessions").EnumerateArray().ToList());
        Assert.Equal(("ChatbotBlock", "done"), (session.GetProperty("origin").GetString(), session.GetProperty("output").GetString()));
        Assert.Equal((chatbot, "Bienvenida"), (session.GetProperty("chatbot").GetProperty("id").GetGuid(), session.GetProperty("chatbot").GetProperty("name").GetString()));
    }

    private static async Task<Seeded> SeedAsync(QepApiFactory factory, string connectionString)
    {
        await SeedTenantAsync(factory);
        var whatsApp = await SeedWhatsAppConnectionAsync(factory, "Ventas", "100200300401");
        await SeedAnthropicConnectionAsync(factory);
        var client = CreateClient(factory, permissions: Manage);
        var created = await SendAsync(client, HttpMethod.Post, $"{AiUrl()}/agents", new { name = "Asesor virtual", source = new { kind = "blank" } });
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();
        var renamed = await SendAsync(client, HttpMethod.Put, $"{AiUrl()}/agents/{id}", new { name = "Asesor virtual", connectionIds = new[] { whatsApp } }, ifMatch: 1);
        var draft = await SendAsync(
            client,
            HttpMethod.Put,
            $"{AiUrl()}/agents/{id}/draft",
            new
            {
                instructions = "Atiende las dudas de los clientes de la tienda.",
                tone = "friendly",
                greeting = (string?)null,
                faq = new[] { new { id = "envios", question = "¿Hacen envíos?", answer = "Sí, a todo el país." } },
                useQuickReplies = false,
                answerOnlyFromSources = true,
                tools = new { catalog = true, orderStatus = false, quotationDraft = false },
                handoff = new { destination = "unassigned", memberId = (Guid?)null, message = (string?)null, maxRepliesWithoutHuman = 20, maxConsecutiveFailures = 2 },
                trigger = "NewConversation",
                model = new { provider = "anthropic", id = "claude-sonnet-5-5" },
            },
            ifMatch: (await JsonAsync(renamed)).GetProperty("version").GetInt64());
        var published = await SendAsync(
            client, HttpMethod.Post, $"{AiUrl()}/agents/{id}/publish", ifMatch: (await JsonAsync(draft)).GetProperty("version").GetInt64());
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        var versionId = await ScalarAsync<Guid>(connectionString, "SELECT id FROM ai.agent_versions WHERE agent_id = @id", ("id", id));
        return new Seeded(factory, connectionString, client, id, versionId, whatsApp);
    }

    private static async Task<Guid> SessionAsync(Seeded seeded, Guid conversation, string status, DateTimeOffset at)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            seeded.ConnectionString,
            """
            INSERT INTO ai.sessions (id, tenant_id, conversation_id, connection_id, agent_id, agent_version_id, status, next_seq, started_at, updated_at, ended_at)
            VALUES (@id, @tenant, @conversation, @connection, @agent, @version, @status, 3, @at, @at, CASE WHEN @status = 'Live' THEN NULL ELSE @at END)
            """,
            ("id", id), ("tenant", Guid.Parse(TenantId)), ("conversation", conversation), ("connection", seeded.ConnectionId),
            ("agent", seeded.AgentId), ("version", seeded.VersionId), ("status", status), ("at", at));
        return id;
    }

    private static async Task<Guid> JobAsync(Seeded seeded, Guid session, string status, string outcome, DateTimeOffset at)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            seeded.ConnectionString,
            """
            INSERT INTO ai.model_jobs (id, tenant_id, session_id, up_to_seq, status, attempts, next_attempt_at, outcome, created_at, completed_at)
            VALUES (@id, @tenant, @session, 1, @status, 1, @at, @outcome, @at, @at)
            """,
            ("id", id), ("tenant", Guid.Parse(TenantId)), ("session", session), ("status", status), ("outcome", outcome), ("at", at));
        return id;
    }

    private static Task<int> TurnAsync(Seeded seeded, Guid session, int seq, string role, Guid message, Guid job, string sources, DateTimeOffset at) =>
        ExecuteAsync(
            seeded.ConnectionString,
            """
            INSERT INTO ai.turns (tenant_id, session_id, seq, role, message_id, job_id, sources, created_at)
            VALUES (@tenant, @session, @seq, @role, @message, @job, CAST(@sources AS jsonb), @at)
            """,
            ("tenant", Guid.Parse(TenantId)), ("session", session), ("seq", seq), ("role", role), ("message", message), ("job", job),
            ("sources", sources), ("at", at));

    private static Task<int> ToolCallAsync(
        Seeded seeded, Guid job, string callId, string tool, DateTimeOffset at, string result = """{"ok":true}""") =>
        ExecuteAsync(
            seeded.ConnectionString,
            """
            INSERT INTO ai.tool_calls (tenant_id, job_id, call_id, tool, arguments, result, outcome, duration_ms, created_at)
            VALUES (@tenant, @job, @call, @tool, '{}'::jsonb, CAST(@result AS jsonb), 'ok', 25, @at)
            """,
            ("tenant", Guid.Parse(TenantId)), ("job", job), ("call", callId), ("tool", tool), ("result", result), ("at", at));

    private static Task<int> UsageAsync(
        Seeded seeded, Guid session, Guid? conversation, Guid job, string servedModel, int input, int output, int? cacheCreation, int? cacheRead,
        DateTimeOffset at) =>
        ExecuteAsync(
            seeded.ConnectionString,
            """
            INSERT INTO ai.usage_records (tenant_id, agent_id, agent_version_id, conversation_id, session_id, job_id, provider, model, served_model,
                                          input_tokens, output_tokens, cache_creation_input_tokens, cache_read_input_tokens, outcome, called_at)
            VALUES (@tenant, @agent, @version, @conversation, @session, @job, 'anthropic', 'claude-sonnet-5-5', @served, @input, @output,
                    @creation, @read, 'ok', @at)
            """,
            ("tenant", Guid.Parse(TenantId)), ("agent", seeded.AgentId), ("version", seeded.VersionId), ("conversation", (object?)conversation ?? DBNull.Value),
            ("session", session), ("job", job), ("served", servedModel), ("input", input), ("output", output),
            ("creation", (object?)cacheCreation ?? DBNull.Value), ("read", (object?)cacheRead ?? DBNull.Value), ("at", at));
}
