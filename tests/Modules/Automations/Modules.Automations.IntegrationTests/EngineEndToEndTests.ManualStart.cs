using System.Net;
using System.Text.Json.Nodes;
using Modules.Messaging.Application;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>
/// Spec 2026-10-31: una persona inicia el asistente desde el hilo (<c>POST …/automation/start</c>) y el resumen dice si el botón se
/// ofrece. De punta a punta contra el motor: el endpoint deja la fila manual del inbox, el evento y la auditoría en un solo guardado, y
/// el estado del encabezado sale de la misma decisión que aplica el motor.
/// </summary>
public sealed partial class EngineEndToEndTests
{
    private const string PrecioKeyword = "precio";

    /// <summary>Una conversación sin asistente: el chatbot dispara por palabra clave y «hola» no la dice.</summary>
    private static async Task<Fixture> ArrangeNoneAsync(TestDatabase database)
    {
        var f = await ArrangeAsync(database, trigger: "Keyword", keywords: [PrecioKeyword]);
        await InboundAsync(f, "w1", "hola");
        return f;
    }

    private static string ConversationsUrl(Fixture f) => $"/api/v1/tenants/{f.Tenant.TenantId}/messaging/conversations";

    private static async Task<HttpResponseMessage> StartAsync(Fixture f, long? version = null, bool ifMatch = true, HttpClient? client = null)
    {
        var owned = client is null;
        client ??= CreateClient(f.Host, f.Tenant.OwnerUserId, f.Tenant.TenantId, MessagingManage);
        try
        {
            var conversationId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");
            var current = version ?? await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ConversationsUrl(f)}/{conversationId}/automation/start");
            request.Headers.TryAddWithoutValidation("X-Qep-Client", "web");
            if (ifMatch)
            {
                request.Headers.TryAddWithoutValidation("If-Match", $"\"{current}\"");
            }

            return await client.SendAsync(request, Ct);
        }
        finally
        {
            if (owned)
            {
                client.Dispose();
            }
        }
    }

    private static async Task<JsonNode> SummaryAsync(Fixture f)
    {
        using var client = CreateClient(f.Host, f.Tenant.OwnerUserId, f.Tenant.TenantId, MessagingManage);
        var conversationId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");
        return JsonNode.Parse(await client.GetStringAsync($"{ConversationsUrl(f)}/{conversationId}", Ct))!;
    }

    /// <summary>Las filas de arranque manual pendientes: las del inbox cuyo id no es el de ningún mensaje.</summary>
    private static Task<long> ManualRowsAsync(Fixture f) =>
        CountAsync(f.ConnectionString,
            """
            SELECT count(*) FROM messaging.automation_inbox AS inbox
             WHERE inbox.processed_at IS NULL AND NOT EXISTS (SELECT 1 FROM messaging.messages AS message WHERE message.id = inbox.message_id)
            """);

    private static Task<long> StartedEventsAsync(Fixture f) =>
        CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE kind = 13 AND details->>'type' = 'AutomationStarted'");

    [Fact]
    [Trait("Slice", "ManualStart")]
    public async Task AStartAnswersTheSummaryWithTheEventAndTheAudit()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeNoneAsync(database);
        var owner = await OwnerMembershipIdAsync(f.ConnectionString, f.Tenant);

        var response = await StartAsync(f);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        Assert.Equal("Active", summary["automation"]!["status"]!.GetValue<string>());
        Assert.Equal(f.ChatbotId, summary["automation"]!["chatbotId"]!.GetValue<Guid>());
        Assert.Equal(1L, await StartedEventsAsync(f));
        Assert.Equal(owner.ToString(), await ScalarAsync<string>(f.ConnectionString,
            "SELECT details->>'actor' FROM messaging.messages WHERE kind = 13 AND details->>'type' = 'AutomationStarted'"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE action = 'messaging.conversation.automation_started' AND actor_id = @actor AND tenant_id = @t",
            ("actor", f.Tenant.OwnerUserId), ("t", f.Tenant.TenantId)));
        Assert.Equal(1L, await ManualRowsAsync(f));
    }

    [Fact]
    [Trait("Slice", "ManualStart")]
    public async Task WithoutIfMatchIs428AndAStaleVersionIs412()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeNoneAsync(database);
        var version = await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations");

        var missing = await StartAsync(f, ifMatch: false);
        var stale = await StartAsync(f, version: version - 1);

        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(0L, await StartedEventsAsync(f));
        Assert.Equal(0L, await ManualRowsAsync(f));
    }

    [Fact]
    [Trait("Slice", "ManualStart")]
    public async Task AnotherTenantAndAMissingPermissionAre403()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeNoneAsync(database);
        var other = await RegisterTenantAsync(f.Host);
        using var foreign = CreateClient(f.Host, other.OwnerUserId, other.TenantId, MessagingManage);
        using var readOnly = CreateClient(f.Host, f.Tenant.OwnerUserId, f.Tenant.TenantId, MessagingPermissions.ConversationRead);

        var underForeignTenant = await StartAsync(f, client: foreign);
        var withoutPermission = await StartAsync(f, client: readOnly);

        // Nunca 404: un 404 confirmaría que el id existe en otro tenant.
        Assert.Equal(HttpStatusCode.Forbidden, underForeignTenant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, withoutPermission.StatusCode);
        Assert.Equal(0L, await StartedEventsAsync(f));
    }

    [Fact]
    [Trait("Slice", "ManualStart")]
    public async Task AnInboundAfterTheReadAnswers412AndEnqueuesNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeNoneAsync(database);
        var read = await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations");

        // La ingesta sube la versión por SQL: el POST con la versión leída antes ya es viejo.
        await InboundAsync(f, "w2", "hola otra vez");
        var response = await StartAsync(f, version: read);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal(0L, await StartedEventsAsync(f));
        Assert.Equal(0L, await ManualRowsAsync(f));
    }

    /// <summary>Cada rechazo lleva el código de la decisión 7 y no deja evento, fila manual ni envío.</summary>
    [Theory]
    [Trait("Slice", "ManualStart")]
    [InlineData("advisor", "messaging.automation.has_advisor")]
    [InlineData("resolved", "messaging.conversation.not_open")]
    [InlineData("blocked", "messaging.contact.blocked")]
    [InlineData("instagram", "messaging.channel_unsupported")]
    [InlineData("nochatbot", "messaging.automation.no_chatbot")]
    [InlineData("window", "messaging.window_closed")]
    public async Task ARejectionNamesItsCodeAndSendsNothing(string scenario, string expectedCode)
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeNoneAsync(database);
        var owner = await OwnerMembershipIdAsync(f.ConnectionString, f.Tenant);
        switch (scenario)
        {
            case "advisor":
                await ExecuteAsync(f.ConnectionString,
                    "UPDATE messaging.conversations SET assigned_member_id = @m, assigned_at = now(), version = version + 1", ("m", owner));
                break;
            case "resolved":
                await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET status = 'Resolved', version = version + 1");
                break;
            case "blocked":
                await ExecuteAsync(f.ConnectionString,
                    "UPDATE messaging.conversations SET blocked_at = now(), blocked_by_member_id = @m, blocked_wa_id = '573001234567', version = version + 1",
                    ("m", owner));
                break;
            case "instagram":
                await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET channel = 'Instagram', version = version + 1");
                break;
            case "nochatbot":
                Assert.Equal(HttpStatusCode.OK, (await ChatbotActionAsync(f, "pause")).StatusCode);
                break;
            case "window":
                await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET last_inbound_at = now() - interval '25 hours', version = version + 1");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }

        var sends = f.Meta.Sends().Count;
        var response = await StartAsync(f);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(expectedCode, (await ProblemAsync(response)).Code);
        Assert.Equal(0L, await StartedEventsAsync(f));
        Assert.Equal(0L, await ManualRowsAsync(f));
        Assert.Equal(sends, f.Meta.Sends().Count);
    }

    [Fact]
    [Trait("Slice", "ManualStart")]
    public async Task TheBotAttendingIsRejectedWithoutAMessage()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);
        var sends = f.Meta.Sends().Count;

        var response = await StartAsync(f);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.automation.already_active", (await ProblemAsync(response)).Code);
        Assert.Equal(0L, await StartedEventsAsync(f));
        Assert.Equal(sends, f.Meta.Sends().Count);
    }

    [Fact]
    [Trait("Slice", "ManualStart")]
    public async Task TheAutomationsModuleOffIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeNoneAsync(database);
        await DisableModuleAsync(f.ConnectionString, f.Tenant.TenantId, "automations");

        var response = await StartAsync(f);
        var summary = await SummaryAsync(f);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.automation.not_available", (await ProblemAsync(response)).Code);
        Assert.Null(summary["automationStart"]);
        Assert.Equal(0L, await StartedEventsAsync(f));
    }

    /// <summary>Review Focus 3: con la fila manual en cola el motor todavía no actúa, pero ofrecer el botón otra vez dejaría iniciar
    /// dos veces.</summary>
    [Fact]
    [Trait("Slice", "ManualStart")]
    public async Task AStartWhilePendingIsRejectedAndTheResponseOffersNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeNoneAsync(database);

        var first = await StartAsync(f);
        var summary = JsonNode.Parse(await first.Content.ReadAsStringAsync(Ct))!;
        var second = await StartAsync(f);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Null(summary["automationStart"]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal("messaging.automation.already_active", (await ProblemAsync(second)).Code);
        Assert.Equal(1L, await ManualRowsAsync(f));
        Assert.Equal(1L, await StartedEventsAsync(f));
    }

    /// <summary>El detalle ofrece el botón con la misma regla que el motor: con asistente en None o pausado a mano con una corrida
    /// esperando se puede iniciar; con una corrida viva atendiendo, con asesor, resuelta o bloqueada no hay nada que ofrecer.</summary>
    [Fact]
    [Trait("Slice", "ManualStart")]
    public async Task TheSummaryMatchesTheEngineOnAttendance()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database, trigger: "Keyword", keywords: [PrecioKeyword]);
        var owner = await OwnerMembershipIdAsync(f.ConnectionString, f.Tenant);
        await InboundAsync(f, "w1", "hola");

        Assert.Equal(AutomationStartStates.Available, (await SummaryAsync(f))["automationStart"]!.GetValue<string>());

        // Una corrida viva atendiendo: la palabra clave la arranca.
        await InboundAsync(f, "w2", PrecioKeyword);
        await DrainAutomationsAsync(f.Host);
        Assert.Equal("Waiting", await RunAsync(f, "status"));
        Assert.Null((await SummaryAsync(f))["automationStart"]);

        // Pausado a mano con la corrida todavía esperando: se puede iniciar de nuevo.
        Assert.Equal(HttpStatusCode.OK, (await ConversationActionAsync(f, "automation/pause")).StatusCode);
        Assert.Equal(AutomationStartStates.Available, (await SummaryAsync(f))["automationStart"]!.GetValue<string>());

        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET last_inbound_at = now() - interval '25 hours', version = version + 1");
        Assert.Equal(AutomationStartStates.WindowClosed, (await SummaryAsync(f))["automationStart"]!.GetValue<string>());
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET last_inbound_at = now(), version = version + 1");

        Assert.Equal(HttpStatusCode.OK, (await ChatbotActionAsync(f, "pause")).StatusCode);
        Assert.Equal(AutomationStartStates.NoChatbot, (await SummaryAsync(f))["automationStart"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await ChatbotActionAsync(f, "resume")).StatusCode);

        await ExecuteAsync(f.ConnectionString,
            "UPDATE messaging.conversations SET blocked_at = now(), blocked_by_member_id = @m, blocked_wa_id = '573001234567', version = version + 1", ("m", owner));
        Assert.Null((await SummaryAsync(f))["automationStart"]);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET blocked_at = NULL, blocked_by_member_id = NULL, blocked_wa_id = NULL, version = version + 1");

        await ExecuteAsync(f.ConnectionString,
            "UPDATE messaging.conversations SET assigned_member_id = @m, assigned_at = now(), version = version + 1", ("m", owner));
        Assert.Null((await SummaryAsync(f))["automationStart"]);
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET assigned_member_id = NULL, assigned_at = NULL, version = version + 1");

        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET automation_status = 'None', automation_started_at = NULL, automation_paused_at = NULL, automation_pause_reason = NULL, status = 'Resolved', version = version + 1");
        Assert.Null((await SummaryAsync(f))["automationStart"]);
    }

    // ---- La fila manual en el worker (Task 6). ----

    private const string FirstFlowSends = "Hola Laura, bienvenido a la línea de servicio.";
    private const string ButtonsSend = "[button] ¿Qué necesitas para tu equipo?";

    /// <summary>Como <see cref="ArrangeNoneAsync"/>, pero con el «hola» ya procesado: el chatbot dispara por palabra clave, así que el
    /// worker lo salta. Sin drenarlo, ese entrante pendiente arrancaría el flujo en cuanto el POST deje la conversación Active.</summary>
    private static async Task<Fixture> ArrangeStartableAsync(TestDatabase database)
    {
        var f = await ArrangeNoneAsync(database);
        await DrainAutomationsAsync(f.Host);
        return f;
    }

    /// <summary>Los resultados de las filas manuales del inbox (las que no respaldan a ningún mensaje), en orden de llegada.</summary>
    private static Task<string> ManualOutcomesAsync(Fixture f) =>
        ScalarAsync<string>(f.ConnectionString,
            """
            SELECT coalesce(string_agg(coalesce(inbox.outcome, '-'), ',' ORDER BY inbox.created_at, inbox.message_id), '')
              FROM messaging.automation_inbox AS inbox
             WHERE NOT EXISTS (SELECT 1 FROM messaging.messages AS message WHERE message.id = inbox.message_id)
            """);

    private static Task<long> ManualRowsEverAsync(Fixture f) =>
        CountAsync(f.ConnectionString,
            """
            SELECT count(*) FROM messaging.automation_inbox AS inbox
             WHERE NOT EXISTS (SELECT 1 FROM messaging.messages AS message WHERE message.id = inbox.message_id)
            """);

    private static Task<long> RunCountAsync(Fixture f) => CountAsync(f.ConnectionString, "SELECT count(*) FROM automations.conversation_runs");

    [Fact]
    [Trait("Slice", "ManualStart")]
    public async Task AStartSendsTheFirstMessageFromTheWorker()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeStartableAsync(database);

        var response = await StartAsync(f);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // El POST no envía nada: el primer mensaje sale del worker, con los clientId deterministas de la corrida.
        Assert.Empty(f.Meta.Sends());
        await DrainAutomationsAsync(f.Host);

        Assert.Equal([FirstFlowSends, ButtonsSend], f.Meta.Sends());
        Assert.Equal("Waiting|n2", await RunAsync(f, "status || '|' || current_node_id"));
        Assert.Equal("Active", await ConversationAsync(f, "automation_status"));
        Assert.Equal("started", await ManualOutcomesAsync(f));
        Assert.Equal(1L, await AuditAsync(f, "automations.run.started"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM automations.run_units WHERE trigger = 'Start'"));
    }

    /// <summary>La respuesta del cliente al primer mensaje sigue la corrida que arrancó el worker: no la interrumpe, aunque la conversación
    /// la haya cambiado el POST (compara <c>AutomationResumedAt</c> con el arranque de la corrida).</summary>
    [Fact]
    [Trait("Slice", "ManualStart")]
    public async Task TheCustomerReplyContinuesTheStartedRun()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeStartableAsync(database);
        Assert.Equal(HttpStatusCode.OK, (await StartAsync(f)).StatusCode);
        await DrainAutomationsAsync(f.Host);
        var runId = await RunAsync(f, "id::text");

        await TapAsync(f, "w2", "tech", "Servicio técnico");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal(1L, await RunCountAsync(f));
        Assert.Equal($"{runId}|Waiting|n3", await RunAsync(f, "id::text || '|' || status || '|' || current_node_id"));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM automations.conversation_runs WHERE status = 'Interrupted'"));
        Assert.Contains("¿Cuál es tu número de documento?", f.Meta.Sends()[2], StringComparison.Ordinal);
    }

    /// <summary>Iniciar sobre una pausa deliberada (o la que dejó un asesor) la borra: la corrida vieja, si la había, queda Interrupted con
    /// su fila de cierre, y la nueva espera en el primer nodo.</summary>
    [Theory]
    [Trait("Slice", "ManualStart")]
    [InlineData("Manual", false)]
    [InlineData("Manual", true)]
    [InlineData("Handoff", false)]
    [InlineData("Handoff", true)]
    [InlineData("AdvisorTook", false)]
    [InlineData("AdvisorTook", true)]
    public async Task AStartOverAPauseClearsItAndClosesTheOldRun(string reason, bool withRun)
    {
        await using var database = await StartDatabaseAsync();
        using var f = withRun ? await ArrangeAsync(database, trigger: "Keyword", keywords: [PrecioKeyword]) : await ArrangeStartableAsync(database);
        if (withRun)
        {
            await InboundAsync(f, "w1", PrecioKeyword);
            await DrainAutomationsAsync(f.Host);
            Assert.Equal("Waiting", await RunAsync(f, "status"));
        }

        // El flujo viejo ya salió: lo que cuenta es lo que sale después del POST.
        var before = f.Meta.Sends().Count;
        await ExecuteAsync(f.ConnectionString,
            """
            UPDATE messaging.conversations SET automation_status = 'Paused', automation_started_at = coalesce(automation_started_at, now()),
                   automation_chatbot_id = @chatbot, automation_paused_at = now(), automation_pause_reason = @reason, version = version + 1
            """,
            ("chatbot", f.ChatbotId), ("reason", reason));

        var response = await StartAsync(f);
        await DrainAutomationsAsync(f.Host);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(withRun ? "Interrupted,Waiting" : "Waiting", await ScalarAsync<string>(f.ConnectionString,
            "SELECT string_agg(status, ',' ORDER BY started_at) FROM automations.conversation_runs"));
        Assert.Equal(withRun ? 1L : 0L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM automations.run_units WHERE trigger = 'Closed' AND closed_reason = 'human_took_over'"));
        Assert.Equal("Active|", await ConversationAsync(f, "automation_status || '|' || coalesce(automation_pause_reason, '')"));
        Assert.Equal("Waiting|n2", await RunAsync(f, "status || '|' || current_node_id"));
        Assert.Equal([FirstFlowSends, ButtonsSend], f.Meta.Sends()[before..]);
    }

    /// <summary>Un asesor la tomó y la devolvió al asistente: la corrida vieja quedó muerta (P2-R25). Iniciar la cierra y el siguiente
    /// entrante sigue la nueva, no la vieja.</summary>
    [Fact]
    [Trait("Slice", "ManualStart")]
    public async Task AStartAfterResumeClosesTheOldRunAndTheNextInboundFollowsTheNew()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database, trigger: "Keyword", keywords: [PrecioKeyword]);
        await InboundAsync(f, "w1", PrecioKeyword);
        await DrainAutomationsAsync(f.Host);
        Assert.Equal(HttpStatusCode.OK, (await ConversationActionAsync(f, "take")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ConversationActionAsync(f, "automation/resume")).StatusCode);

        var response = await StartAsync(f);
        await DrainAutomationsAsync(f.Host);
        await TapAsync(f, "w2", "tech", "Servicio técnico");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Interrupted,Waiting", await ScalarAsync<string>(f.ConnectionString,
            "SELECT string_agg(status, ',' ORDER BY started_at) FROM automations.conversation_runs"));
        Assert.Equal("Waiting|n3", await RunAsync(f, "status || '|' || current_node_id"));
        // Dos flujos de dos mensajes y la pregunta de documento que responde a la respuesta del cliente.
        Assert.Equal(5, f.Meta.Sends().Count);
        Assert.Contains("¿Cuál es tu número de documento?", f.Meta.Sends()[^1], StringComparison.Ordinal);
    }

    /// <summary>«Devolver al asistente» la deja Active sin corrida: iniciar arranca desde el primer nodo.</summary>
    [Fact]
    [Trait("Slice", "ManualStart")]
    public async Task AStartOnActiveWithoutARunStarts()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeStartableAsync(database);
        await ExecuteAsync(f.ConnectionString,
            "UPDATE messaging.conversations SET automation_status = 'Active', automation_started_at = now(), automation_chatbot_id = @c, version = version + 1",
            ("c", f.ChatbotId));

        var response = await StartAsync(f);
        await DrainAutomationsAsync(f.Host);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1L, await RunCountAsync(f));
        Assert.Equal([FirstFlowSends, ButtonsSend], f.Meta.Sends());
        Assert.Equal("Waiting|n2", await RunAsync(f, "status || '|' || current_node_id"));
    }

    /// <summary>D-6: una corrida que terminó sin resolver no impide iniciar otra, y D-12 sigue en pie para el disparador: ni antes ni
    /// después de iniciar un entrante nuevo la vuelve a arrancar.</summary>
    [Fact]
    [Trait("Slice", "ManualStart")]
    public async Task AStartAfterACompletedRunStartsAndTheTriggerStillDoesNot()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database, flowJson: ResolveAtOnceFlow);
        // El validador no publica una salida obligatoria sin conectar: la corrida Completed sale de una versión a la que se le corta.
        await ExecuteAsync(f.ConnectionString, "UPDATE automations.chatbot_versions SET flow = jsonb_set(flow, '{nodes,0,outputs,next}', 'null')");
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);
        Assert.Equal("Open|None", await ConversationAsync(f, "status || '|' || automation_status"));
        await InboundAsync(f, "w2", "¿Hay alguien?");
        await DrainAutomationsAsync(f.Host);
        Assert.Single(f.Meta.Sends());

        var response = await StartAsync(f);
        await DrainAutomationsAsync(f.Host);
        await InboundAsync(f, "w3", "Gracias");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2L, await RunCountAsync(f));
        Assert.Equal(2, f.Meta.Sends().Count);
        Assert.Equal("Open|None", await ConversationAsync(f, "status || '|' || automation_status"));
    }

    [Fact]
    [Trait("Slice", "ManualStart")]
    public async Task TwoStartsInARowSendOneFlow()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeStartableAsync(database);

        var first = await StartAsync(f);
        var second = await StartAsync(f);
        await DrainAutomationsAsync(f.Host);
        var third = await StartAsync(f);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, third.StatusCode);
        Assert.Equal(1L, await ManualRowsEverAsync(f));
        Assert.Equal(1L, await RunCountAsync(f));
        Assert.Equal([FirstFlowSends, ButtonsSend], f.Meta.Sends());
    }

    /// <summary>Dos POST con la misma versión: la versión optimista deja pasar a uno solo, y el candado de la conversación al otro lo
    /// frena con 412 o 422 sin dejar una segunda fila.</summary>
    [Fact]
    [Trait("Slice", "ManualStart")]
    public async Task TwoSimultaneousStartsSendOneFlow()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeStartableAsync(database);
        var version = await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations");

        var responses = await Task.WhenAll(StartAsync(f, version), StartAsync(f, version));
        await DrainAutomationsAsync(f.Host);

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.UnprocessableEntity);
        Assert.Equal(1L, await ManualRowsEverAsync(f));
        Assert.Equal(1L, await RunCountAsync(f));
        Assert.Equal([FirstFlowSends, ButtonsSend], f.Meta.Sends());
    }

    /// <summary>Review Focus 4: el entrante llegó antes del POST y sigue en la cola. Con la conversación ya Active, el worker lo toma
    /// primero y arranca el flujo; la fila manual encuentra la corrida viva y se cierra sin enviar, sin tomarse por una respuesta del cliente y
    /// sin pisar el entrante que la corrida tiene registrado.</summary>
    [Fact]
    [Trait("Slice", "ManualStart")]
    public async Task AnInboundQueuedBeforeTheStartRunsOneFlow()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database, trigger: "Keyword", keywords: [PrecioKeyword]);
        await InboundAsync(f, "w1", PrecioKeyword);

        var response = await StartAsync(f);
        await DrainAutomationsAsync(f.Host);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1L, await RunCountAsync(f));
        Assert.Equal([FirstFlowSends, ButtonsSend], f.Meta.Sends());
        Assert.Equal("Waiting|n2|0", await RunAsync(f, "status || '|' || current_node_id || '|' || attempts"));
        Assert.Equal("skipped", await ManualOutcomesAsync(f));
        Assert.Equal(await ScalarAsync<string>(f.ConnectionString, "SELECT id::text FROM messaging.messages WHERE wamid = 'w1'"),
            await RunAsync(f, "last_inbound_message_id::text"));
    }

    /// <summary>La fila manual reabierta (el cierre falló después de confirmar la unidad) ya no arranca otra corrida: la última lleva su id
    /// como marcador (P2-R24). <c>ReopenInboxRowAsync</c> busca por wamid y esta fila no tiene mensaje, así que se reabre por SQL.</summary>
    [Fact]
    [Trait("Slice", "ManualStart")]
    public async Task AReclaimedStartRowIsDuplicate()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeStartableAsync(database);
        Assert.Equal(HttpStatusCode.OK, (await StartAsync(f)).StatusCode);
        await DrainAutomationsAsync(f.Host);
        await ExecuteAsync(f.ConnectionString,
            """
            UPDATE messaging.automation_inbox AS inbox SET processed_at = NULL, outcome = NULL, next_attempt_at = now() - interval '1 second'
             WHERE NOT EXISTS (SELECT 1 FROM messaging.messages AS message WHERE message.id = inbox.message_id)
            """);

        await DrainAutomationsAsync(f.Host);

        Assert.Equal("duplicate", await ManualOutcomesAsync(f));
        Assert.Equal(1L, await RunCountAsync(f));
        Assert.Equal([FirstFlowSends, ButtonsSend], f.Meta.Sends());
    }
}
