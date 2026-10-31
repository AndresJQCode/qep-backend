using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Automations.Api;
using Modules.Automations.Application;
using Modules.Messaging.Application;
using Npgsql;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>
/// Spec 2026-10-21 §5.2, D-M3, D-M4, D-M5, D-M15 y D-M17, de punta a punta: un webhook de Meta entra por la ingesta de Mensajería, cae en
/// <c>automation_inbox</c> y el worker del motor lo procesa contra el chatbot publicado por la API. Graph es un doble
/// (<see cref="FakeMetaGraphHandler"/>) que responde <c>wamid.N</c> a cada envío.
/// </summary>
public sealed partial class EngineEndToEndTests
{
    private const string PhoneNumberId = "111";
    private const string WaId = "573001234567";

    /// <summary>«Bienvenida → Botones (Servicio técnico / Mantenimiento) → técnico: pregunta abierta digits → resolver; mantenimiento:
    /// pasar a un asesor».</summary>
    private const string FlowJson = """
        {
          "schema": 1,
          "start": { "firstNodeId": "n1" },
          "variables": [ { "name": "cedula", "type": "text" }, { "name": "motivo", "type": "text" } ],
          "nodes": [
            { "id": "n1", "kind": "message", "title": "Bienvenida", "text": "Hola {nombre_contacto}, bienvenido a la línea de servicio.", "outputs": { "next": "n2" } },
            { "id": "n2", "kind": "buttons", "title": "¿Qué necesitas?", "text": "¿Qué necesitas para tu equipo?", "buttons": [ { "id": "tech", "label": "Servicio técnico" }, { "id": "maint", "label": "Mantenimiento" } ], "saveTo": "motivo", "fallback": "repeat", "outputs": { "tech": "n3", "maint": "n5", "other": null } },
            { "id": "n3", "kind": "openQuestion", "title": "Número de documento", "text": "¿Cuál es tu número de documento?", "saveTo": "cedula", "validation": { "kind": "digits" }, "outputs": { "answered": "n4", "invalid": null } },
            { "id": "n4", "kind": "resolve", "title": "Listo", "text": "Gracias, registramos el documento {cedula}." },
            { "id": "n5", "kind": "handoff", "title": "Asesor de mantenimiento", "text": "Un asesor te escribe en un momento." }
          ]
        }
        """;

    private static readonly string[] MessagingManage = [MessagingPermissions.ConversationRead, MessagingPermissions.ConversationManage];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(
        QepApiFactory Factory, WebApplicationFactory<Program> Host, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId,
        Guid ChatbotId, HttpClient Anonymous, RecordingNotifier Notifier, BotMeta Meta) : IDisposable
    {
        public void Dispose()
        {
            Anonymous.Dispose();
            Factory.Dispose();
        }
    }

    private static async Task<Fixture> ArrangeAsync(
        TestDatabase database, string trigger = "NewConversation", string[]? keywords = null, bool enqueue = true, string flowJson = FlowJson,
        int? inboxBatchSize = null)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString, enqueueAutomation: enqueue);
        var notifier = new RecordingNotifier();
        var host = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IMessagingNotifier>();
                services.AddSingleton<IMessagingNotifier>(notifier);
            });
            if (inboxBatchSize is { } size)
            {
                builder.UseSetting("Automations:Workers:InboxBatchSize", size.ToString(CultureInfo.InvariantCulture));
            }
        });
        var tenant = await RegisterTenantAsync(host);
        await EnableAutomationsAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(host, tenant.TenantId, "Servicio", PhoneNumberId, "222");
        using var admin = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var chatbotId = await PublishChatbotAsync(admin, tenant.TenantId, flowJson, connectionId, trigger, keywords ?? []);
        var meta = new BotMeta(factory.MetaHandler);
        return new Fixture(factory, host, connectionString, tenant, connectionId, chatbotId, host.CreateClient(), notifier, meta);
    }

    /// <summary>Segundos Unix recientes y crecientes con el número del wamid («w3» → +3): la ventana de 24 h está abierta y el orden del
    /// hilo es el de llegada.</summary>
    private static long Timestamp(string wamid) =>
        DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600 + long.Parse(wamid.AsSpan(1), CultureInfo.InvariantCulture);

    private static async Task InboundAsync(Fixture f, string wamid, string text, long? timestamp = null)
    {
        var response = await PostWebhookAsync(f.Anonymous, Payloads.InboundText(PhoneNumberId, WaId, wamid, timestamp ?? Timestamp(wamid), text));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await DrainDeliveriesAsync(f.Host);
    }

    /// <summary><paramref name="quotedWamid"/>: el mensaje del botón tocado, que Meta manda en <c>context.id</c>.</summary>
    private static async Task TapAsync(Fixture f, string wamid, string replyId, string title, string? quotedWamid = null)
    {
        var response = await PostWebhookAsync(
            f.Anonymous, Payloads.InboundButtonReply(PhoneNumberId, WaId, wamid, Timestamp(wamid), replyId, title, quotedWamid));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await DrainDeliveriesAsync(f.Host);
    }

    private static Task<string> ConversationAsync(Fixture f, string columns) =>
        ScalarAsync<string>(f.ConnectionString, $"SELECT {columns} FROM messaging.conversations");

    private static Task<string> RunAsync(Fixture f, string columns) =>
        ScalarAsync<string>(f.ConnectionString, $"SELECT {columns} FROM automations.conversation_runs ORDER BY started_at DESC LIMIT 1");

    private static Task<string> OutcomesAsync(Fixture f) =>
        ScalarAsync<string>(f.ConnectionString,
            "SELECT coalesce(string_agg(coalesce(outcome, '-'), ',' ORDER BY created_at, message_id), '') FROM messaging.automation_inbox");

    private static Task<long> BotMessagesAsync(Fixture f) =>
        CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE sent_by_kind = 2 AND status <> 4");

    private static Task<long> AuditAsync(Fixture f, string action) =>
        CountAsync(f.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE action = @action AND actor_type = 'System' AND tenant_id = @t",
            ("action", action), ("t", f.Tenant.TenantId));

    private static async Task<HttpResponseMessage> ConversationActionAsync(Fixture f, string action)
    {
        using var client = CreateClient(f.Host, f.Tenant.OwnerUserId, f.Tenant.TenantId, MessagingManage);
        var conversationId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");
        var version = await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/tenants/{f.Tenant.TenantId}/messaging/conversations/{conversationId}/{action}");
        request.Headers.TryAddWithoutValidation("X-Qep-Client", "web");
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return await client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task AFirstInboundStartsTheRunAndSendsUntilTheButtons()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);

        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);

        var sends = f.Meta.Sends();
        Assert.Equal(2, sends.Count);
        // D-M8: {nombre_contacto} es el nombre de perfil de WhatsApp.
        Assert.Equal(["Hola Laura, bienvenido a la línea de servicio.", "[button] ¿Qué necesitas para tu equipo?"], sends);
        Assert.Equal(2L, await BotMessagesAsync(f));
        Assert.Equal($"Active|{f.ChatbotId}|¿Qué necesitas?|", await ConversationAsync(f,
            "automation_status || '|' || automation_chatbot_id || '|' || automation_step || '|' || coalesce(assigned_member_id::text, '')"));
        Assert.Equal("Waiting|n2", await RunAsync(f, "status || '|' || current_node_id"));
        Assert.Equal("started", await OutcomesAsync(f));
        Assert.Equal(1L, await AuditAsync(f, "automations.run.started"));
    }

    [Fact]
    public async Task TheTriggerInboundIsNotAnAnswer()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);

        // «Servicio técnico» escrito es una opción válida de los botones: si el entrante que dispara contara como respuesta, avanzaría.
        await InboundAsync(f, "w1", "Servicio técnico");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal("Waiting|n2|0", await RunAsync(f, "status || '|' || current_node_id || '|' || attempts"));
        Assert.Equal(2, f.Meta.Sends().Count);
    }

    [Fact]
    public async Task AButtonReplyAdvancesAndADigitsAnswerResolves()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);

        await TapAsync(f, "w2", "tech", "Servicio técnico");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal("Waiting|n3|Número de documento", await RunAsync(f, "status || '|' || current_node_id || '|' || (SELECT automation_step FROM messaging.conversations)"));
        Assert.Contains("¿Cuál es tu número de documento?", f.Meta.Sends()[2], StringComparison.Ordinal);

        await InboundAsync(f, "w3", "10203");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal("Resolved|None", await ConversationAsync(f, "status || '|' || automation_status"));
        Assert.Equal("Resolved|10203|tech", await RunAsync(f, "status || '|' || (variables->>'cedula') || '|' || (variables->'motivo'->>'id')"));
        Assert.Contains("registramos el documento 10203", f.Meta.Sends()[3], StringComparison.Ordinal);
        Assert.Equal("started,advanced,ended", await OutcomesAsync(f));
        Assert.Equal(1L, await AuditAsync(f, "automations.run.resolved"));
        Assert.Equal(1L, await AuditAsync(f, "messaging.conversation.resolved"));
    }

    [Fact]
    public async Task MaintenanceHandsOffAndNotifiesTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);
        await TapAsync(f, "w2", "maint", "Mantenimiento");
        var conversationId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");
        var lastInbound = await ScalarAsync<Guid>(f.ConnectionString,
            "SELECT id FROM messaging.messages WHERE direction = 1 ORDER BY occurred_at DESC, id DESC LIMIT 1");
        f.Notifier.Clear();

        await DrainAutomationsAsync(f.Host);

        Assert.Equal("Open|Paused|Handoff|", await ConversationAsync(f,
            "status || '|' || automation_status || '|' || automation_pause_reason || '|' || coalesce(assigned_member_id::text, '')"));
        Assert.Equal("HandedOff", await RunAsync(f, "status"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.messages WHERE kind = 13 AND details->>'type' = 'AutomationPaused'"));
        Assert.Contains("Un asesor te escribe en un momento.", f.Meta.Sends()[2], StringComparison.Ordinal);
        // D-M5: suena a todo el tenant como un entrante sin asignar.
        Assert.Contains($"inbound {conversationId} {lastInbound} -", f.Notifier.Snapshot());
        Assert.Equal(1L, await AuditAsync(f, "automations.run.handed_off"));
    }

    /// <summary>Guarda en los ajustes del chatbot publicado la regla de intentos agotados (spec 2026-10-25 D-2), conservando el resto.</summary>
    private static async Task SaveExhaustedRuleAsync(Fixture f, string action, Guid? memberId)
    {
        using var admin = CreateClient(f.Host, f.Tenant.OwnerUserId, f.Tenant.TenantId, ManagePermissions);
        var url = $"{AutomationsUrl(f.Tenant.TenantId)}/chatbots/{f.ChatbotId}";
        var editor = (await admin.GetFromJsonAsync<ChatbotEditorDto>(url, Ct))!;
        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = JsonContent.Create(new UpdateChatbotRequest(
                editor.Chatbot.Name, editor.Chatbot.Description, editor.Chatbot.Trigger, editor.Keywords, editor.RestartKeyword, editor.MaxAttempts,
                [f.ConnectionId], null, action, memberId)),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{editor.Chatbot.Version}\"");
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(request, Ct)).StatusCode);
    }

    /// <summary>Lleva la corrida a la pregunta de dígitos (n3) y manda tres «abc», que la agotan (D-M7).</summary>
    private static async Task ExhaustTheQuestionAsync(Fixture f)
    {
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);
        await TapAsync(f, "w2", "tech", "Servicio técnico");
        await DrainAutomationsAsync(f.Host);
        foreach (var wamid in new[] { "w3", "w4", "w5" })
        {
            await InboundAsync(f, wamid, "abc");
            await DrainAutomationsAsync(f.Host);
        }
    }

    /// <summary>Spec 2026-10-25 D-1, D-2 y D-8: agotar los intentos con la regla «Asignar a un miembro» pasa la conversación al asesor sin
    /// escribirle al cliente (el traspaso por agotamiento no manda texto) y el hilo dice quién la tiene.</summary>
    [Fact]
    public async Task AnExhaustedQuestionAssignsTheConversationToTheChosenMember()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        var advisor = await SeedMemberAsync(f.ConnectionString, f.Tenant.TenantId, "Asesora Ana");
        await SaveExhaustedRuleAsync(f, "Handoff", advisor.MembershipId);

        await ExhaustTheQuestionAsync(f);

        Assert.Equal("HandedOff", await RunAsync(f, "status"));
        Assert.Equal($"Open|Paused|{advisor.MembershipId}", await ConversationAsync(f, "status || '|' || automation_status || '|' || assigned_member_id::text"));
        // Bienvenida, botones y la pregunta tres veces: el traspaso por agotamiento no escribe.
        var sends = f.Meta.Sends();
        Assert.Equal(5, sends.Count);
        Assert.Equal(new[] { sends[2], sends[2], sends[2] }, sends[2..]);

        var conversationId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");
        using var reader = CreateClient(f.Host, f.Tenant.OwnerUserId, f.Tenant.TenantId, MessagingManage);
        var page = await reader.GetStringAsync($"/api/v1/tenants/{f.Tenant.TenantId}/messaging/conversations/{conversationId}/messages", Ct);
        var paused = JsonNode.Parse(page)!["items"]!.AsArray()
            .Select(item => item!["event"])
            .Single(@event => @event?["type"]?.GetValue<string>() == "AutomationPaused")!["handoff"]!;
        Assert.Equal("AttemptsExhausted", paused["kind"]!.GetValue<string>());
        Assert.Equal(advisor.MembershipId, paused["assignedTo"]!["memberId"]!.GetValue<Guid>());
        Assert.Equal("Asesora Ana", paused["assignedTo"]!["displayName"]!.GetValue<string>());
    }

    /// <summary>Spec 2026-10-25 D-4: sin tocar los ajustes la regla es «Volver al inicio»: la corrida arranca de nuevo y el asistente
    /// sigue activo en la conversación.</summary>
    [Fact]
    public async Task AnExhaustedQuestionRestartsByDefault()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);

        await ExhaustTheQuestionAsync(f);

        Assert.Equal("Waiting|n2", await RunAsync(f, "status || '|' || current_node_id"));
        Assert.Equal("Open|Active|", await ConversationAsync(f, "status || '|' || automation_status || '|' || coalesce(assigned_member_id::text, '')"));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE details->>'type' = 'AutomationPaused'"));
    }

    /// <summary>§5.2 paso 5 y D-M14: un envío que Meta rechaza termina la corrida Failed con el código de Mensajería (P2-R21) y la
    /// conversación pasa a un asesor.</summary>
    [Fact]
    public async Task ARejectedSendFailsTheRunAndHandsOff()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        f.Meta.RejectFrom = 2;

        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal("Failed|messaging.message.rejected", await RunAsync(f, "status || '|' || failure_code"));
        Assert.Equal("Paused|Handoff", await ConversationAsync(f, "automation_status || '|' || automation_pause_reason"));
        Assert.Equal("run_failed", await OutcomesAsync(f));
        Assert.Equal(1L, await AuditAsync(f, "automations.run.failed"));
        Assert.Equal(0L, await AuditAsync(f, "automations.run.handed_off"));
    }

    [Fact]
    public async Task AnAssignedConversationIsSkipped()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await InboundAsync(f, "w1", "Hola");

        // Tomarla después de encolar y antes de procesar (si fuera antes del webhook, la ingesta ni siquiera encolaría: D-M1).
        var take = await ConversationActionAsync(f, "take");
        Assert.Equal(HttpStatusCode.OK, take.StatusCode);
        await DrainAutomationsAsync(f.Host);

        Assert.Equal("skipped", await OutcomesAsync(f));
        Assert.Empty(f.Meta.Sends());
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM automations.conversation_runs"));
    }

    [Fact]
    public async Task AnAdvisorTakingBetweenClaimAndSendInterruptsTheRun()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        var owner = await OwnerMembershipIdAsync(f.ConnectionString, f.Tenant);
        // Plan Review Focus #1: el asesor la toma mientras Meta recibe el primer mensaje; el segundo ya no es del bot.
        f.Meta.BeforeAnswer = sent =>
        {
            if (sent == 1)
            {
                using var connection = new NpgsqlConnection(f.ConnectionString);
                connection.Open();
                // Lo que escribe Conversation.Take sobre un asistente activo (CK_conversations_automation_assignment).
                using var command = new NpgsqlCommand(
                    """
                    UPDATE messaging.conversations SET assigned_member_id = @m, assigned_at = now(), automation_status = 'Paused',
                        automation_paused_at = now(), automation_pause_reason = 'AdvisorTook', version = version + 1
                    """,
                    connection);
                command.Parameters.AddWithValue("m", owner);
                command.ExecuteNonQuery();
            }
        };

        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);

        Assert.Single(f.Meta.Sends());
        Assert.Equal("Interrupted|", await RunAsync(f, "status || '|' || coalesce(failure_code, '')"));
        Assert.Equal("interrupted", await OutcomesAsync(f));
        // NotAutomated no es un fallo: no pasa a un asesor por Handoff (ya hay uno) ni deja auditoría de fallo.
        Assert.Equal("AdvisorTook", await ConversationAsync(f, "automation_pause_reason"));
        Assert.Equal(0L, await AuditAsync(f, "automations.run.failed"));
        Assert.Equal(0L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.messages WHERE kind = 13 AND details->>'type' = 'AutomationPaused'"));
    }

    [Fact]
    public async Task ABlockBetweenClaimAndSendInterruptsTheRunSilently()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        var owner = await OwnerMembershipIdAsync(f.ConnectionString, f.Tenant);
        // Spec 2026-10-26 §3.4: bloquean al contacto mientras Meta recibe el primer mensaje; el segundo ya no sale.
        f.Meta.BeforeAnswer = sent =>
        {
            if (sent == 1)
            {
                using var connection = new NpgsqlConnection(f.ConnectionString);
                connection.Open();
                // Lo que escribe Conversation.Block (CK_conversations_block: las tres columnas juntas).
                using var command = new NpgsqlCommand(
                    """
                    UPDATE messaging.conversations SET blocked_at = now(), blocked_by_member_id = @m, blocked_wa_id = '573001234567', version = version + 1
                    """,
                    connection);
                command.Parameters.AddWithValue("m", owner);
                command.ExecuteNonQuery();
            }
        };

        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);

        Assert.Single(f.Meta.Sends());
        Assert.Equal("Interrupted|", await RunAsync(f, "status || '|' || coalesce(failure_code, '')"));
        // No es un fallo ni un traspaso: la automatización sigue Active y no queda auditoría de fallo ni aviso de pausa.
        Assert.Equal("Active", await ConversationAsync(f, "automation_status"));
        Assert.Equal(0L, await AuditAsync(f, "automations.run.failed"));
        Assert.Equal(0L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.messages WHERE kind = 13 AND details->>'type' = 'AutomationPaused'"));
    }

    [Fact]
    public async Task AHumanResumeStartsANewRunWithoutTheTrigger()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database, trigger: "Keyword", keywords: ["hola"]);
        await InboundAsync(f, "w1", "buenas tardes");
        await DrainAutomationsAsync(f.Host);
        Assert.Equal("skipped", await OutcomesAsync(f));

        // «Devolver al asistente» (spec 1): Active sin corrida.
        await ExecuteAsync(f.ConnectionString,
            "UPDATE messaging.conversations SET automation_status = 'Active', automation_started_at = now(), automation_chatbot_id = @c, version = version + 1",
            ("c", f.ChatbotId));
        await InboundAsync(f, "w2", "cualquier cosa");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal("skipped,started", await OutcomesAsync(f));
        Assert.Equal(2, f.Meta.Sends().Count);
        Assert.Equal("Waiting|n2", await RunAsync(f, "status || '|' || current_node_id"));
    }

    [Fact]
    public async Task ARetriedRowDoesNotDuplicateTheMessages()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        var failing = true;
        f.Meta.BeforeAnswer = sent =>
        {
            if (sent == 2 && failing)
            {
                throw new InvalidOperationException("Graph se cayó a mitad de la unidad.");
            }
        };

        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);

        // La unidad se deshizo: sin corrida y la fila queda para su reintento por lease.
        Assert.Equal("-|1", await ScalarAsync<string>(f.ConnectionString,
            "SELECT coalesce(outcome, '-') || '|' || attempts FROM messaging.automation_inbox"));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM automations.conversation_runs"));

        failing = false;
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.automation_inbox SET next_attempt_at = now() - interval '1 second'");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal("started", await OutcomesAsync(f));
        Assert.Equal(2L, await BotMessagesAsync(f));
        // La bienvenida salió una sola vez: el reintento la encontró por su clientId (AlreadySent).
        Assert.Single(f.Meta.Sends(), body => body.Contains("bienvenido", StringComparison.Ordinal));
        Assert.Equal("Waiting|n2", await RunAsync(f, "status || '|' || current_node_id"));
    }

    [Fact]
    public async Task WithTheFlagOffNothingHappens()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database, enqueue: false);

        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.automation_inbox"));
        Assert.Empty(f.Meta.Sends());
        Assert.Equal("None", await ConversationAsync(f, "automation_status"));
    }

    [Fact]
    public async Task AReopenedConversationInterruptsTheStaleRunAndStartsANewOne()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);
        // Un asesor la resuelve con la corrida esperando (D-A5: queda en None); el cliente vuelve a escribir y la reabre.
        var resolve = await ConversationActionAsync(f, "resolve");
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);

        await InboundAsync(f, "w2", "Hola otra vez");
        await DrainAutomationsAsync(f.Host);

        // D-M15: la vieja termina Interrupted y, en la misma unidad, el disparador arranca otra (D-M12: una sola viva).
        Assert.Equal("Interrupted,Waiting", await ScalarAsync<string>(f.ConnectionString,
            "SELECT string_agg(status, ',' ORDER BY started_at) FROM automations.conversation_runs"));
        Assert.Equal("started,started", await OutcomesAsync(f));
        Assert.Equal(4, f.Meta.Sends().Count);
    }

    /// <summary>
    /// Reinicio con la última versión publicada (2026-10-23): D-C1 deja la corrida viva en su versión, pero la palabra de reinicio es un
    /// comienzo explícito. Con una versión más nueva publicada, la corrida vieja termina <c>Completed</c> y, en la misma unidad, arranca
    /// otra con la publicada; la conversación sigue <c>Active</c> con el mismo chatbot.
    /// </summary>
    [Fact]
    public async Task RestartAfterANewPublicationStartsARunWithTheLatestVersion()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);
        using (var admin = CreateClient(f.Host, f.Tenant.OwnerUserId, f.Tenant.TenantId, ManagePermissions))
        {
            await RepublishChatbotAsync(admin, f.Tenant.TenantId, f.ChatbotId, FlowJson.Replace(
                "Hola {nombre_contacto}, bienvenido a la línea de servicio.", "Hola {nombre_contacto}, esta es la versión 2.", StringComparison.Ordinal));
        }

        await InboundAsync(f, "w2", "REINICIAR");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal("Completed|1,Waiting|2", await ScalarAsync<string>(f.ConnectionString,
            "SELECT string_agg(r.status || '|' || v.number, ',' ORDER BY r.started_at) FROM automations.conversation_runs r JOIN automations.chatbot_versions v ON v.id = r.chatbot_version_id"));
        var sends = f.Meta.Sends();
        Assert.Equal(4, sends.Count);
        Assert.Equal(["Hola Laura, esta es la versión 2.", "[button] ¿Qué necesitas para tu equipo?"], sends[2..]);
        Assert.Equal("Open|Active", await ConversationAsync(f, "status || '|' || automation_status"));
        Assert.Equal("started,started", await OutcomesAsync(f));
        Assert.Equal(1L, await AuditAsync(f, "automations.run.completed"));
        Assert.Equal(2L, await AuditAsync(f, "automations.run.started"));
    }

    /// <summary>Sin versión nueva, la palabra de reinicio vuelve a empezar la misma corrida (D-I5): no hay corrida nueva.</summary>
    [Fact]
    public async Task RestartWithTheSameVersionKeepsTheRun()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);

        await InboundAsync(f, "w2", "REINICIAR");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM automations.conversation_runs"));
        Assert.Equal("Waiting|n2", await RunAsync(f, "status || '|' || current_node_id"));
        Assert.Equal(2, f.Meta.Sends().Count(text => text == "Hola Laura, bienvenido a la línea de servicio."));
        Assert.Equal("started,advanced", await OutcomesAsync(f));
        Assert.Equal(0L, await AuditAsync(f, "automations.run.completed"));
    }

    /// <summary>Responde <c>wamid.N</c> a cada envío del bot (el acuse de leído aparte) y deja a la prueba meterse antes de responder.</summary>
    private sealed class BotMeta
    {
        private readonly FakeMetaGraphHandler handler;
        private int sent;

        public BotMeta(FakeMetaGraphHandler handler)
        {
            this.handler = handler;
            handler.RespondWith($"/{PhoneNumberId}/messages", request =>
            {
                var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                if (IsReadReceipt(body))
                {
                    return FakeMetaGraphHandler.Json(HttpStatusCode.OK, """{"success":true}""");
                }

                var number = Interlocked.Increment(ref sent);
                BeforeAnswer?.Invoke(number);
                if (number >= RejectFrom)
                {
                    return FakeMetaGraphHandler.Json(HttpStatusCode.BadRequest, FakeMetaGraphHandler.GraphError(RejectCode));
                }

                return FakeMetaGraphHandler.Json(HttpStatusCode.OK, $$"""
                    {"messaging_product":"whatsapp","contacts":[{"input":"{{WaId}}","wa_id":"{{WaId}}"}],"messages":[{"id":"wamid.{{number}}"}]}
                    """);
            });
        }

        public Action<int>? BeforeAnswer { get; set; }

        /// <summary>Desde este envío, Graph responde un error que no es de credencial ni de ventana.</summary>
        public int? RejectFrom { get; set; }

        /// <summary>El código de Graph del rechazo: 131000 (error desconocido) si la prueba no dice otro.</summary>
        public int RejectCode { get; set; } = 131000;

        /// <summary>El texto de cada envío que llegó a Graph, en orden (también el que lanzó): <c>text.body</c> o el cuerpo del
        /// interactivo con su tipo adelante (<c>[button] …</c>). El JSON de Graph escapa las tildes, así que se lee y no se busca en crudo.</summary>
        public List<string> Sends() =>
        [
            .. handler.Requests
                .Where(request => request.Uri!.AbsolutePath.EndsWith($"/{PhoneNumberId}/messages", StringComparison.Ordinal) && !IsReadReceipt(request.Body))
                .Select(request => TextOf(request.Body)),
        ];

        private static string TextOf(string? body)
        {
            var root = JsonNode.Parse(body ?? "{}")!;
            return root["type"]?.GetValue<string>() == "interactive"
                ? $"[{root["interactive"]?["type"]?.GetValue<string>()}] {root["interactive"]?["body"]?["text"]?.GetValue<string>()}"
                : root["text"]?["body"]?.GetValue<string>() ?? string.Empty;
        }
    }

    private sealed class RecordingNotifier : IMessagingNotifier
    {
        private readonly List<string> events = [];

        public void Clear()
        {
            lock (events)
            {
                events.Clear();
            }
        }

        public string[] Snapshot()
        {
            lock (events)
            {
                return [.. events];
            }
        }

        public Task ConversationChangedAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken)
        {
            lock (events)
            {
                events.Add($"changed {conversationId}");
            }

            return Task.CompletedTask;
        }

        public Task InboundMessageAsync(Guid tenantId, Guid conversationId, Guid messageId, Guid? assignedMemberId, CancellationToken cancellationToken)
        {
            lock (events)
            {
                events.Add($"inbound {conversationId} {messageId} {assignedMemberId?.ToString() ?? "-"}");
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>Los cuerpos de webhook con la forma literal de Meta, copia mínima de <c>MetaPayloads</c> de Mensajería.</summary>
    private static class Payloads
    {
        public static string InboundText(string phoneNumberId, string waId, string wamid, long timestamp, string text) =>
            Messages(phoneNumberId, waId, new JsonObject
            {
                ["from"] = waId, ["id"] = wamid, ["timestamp"] = Seconds(timestamp), ["type"] = "text",
                ["text"] = new JsonObject { ["body"] = text },
            });

        public static string InboundButtonReply(
            string phoneNumberId, string waId, string wamid, long timestamp, string replyId, string title, string? quotedWamid = null)
        {
            var message = new JsonObject
            {
                ["from"] = waId, ["id"] = wamid, ["timestamp"] = Seconds(timestamp), ["type"] = "interactive",
                ["interactive"] = new JsonObject
                {
                    ["type"] = "button_reply", ["button_reply"] = new JsonObject { ["id"] = replyId, ["title"] = title },
                },
            };
            if (quotedWamid is not null)
            {
                message["context"] = new JsonObject { ["from"] = "15550000000", ["id"] = quotedWamid };
            }

            return Messages(phoneNumberId, waId, message);
        }

        private static string Messages(string phoneNumberId, string waId, JsonObject message)
        {
            var userId = "CO." + waId;
            message["from_user_id"] = userId;
            var value = new JsonObject
            {
                ["messaging_product"] = "whatsapp",
                ["metadata"] = new JsonObject { ["display_phone_number"] = "15550000000", ["phone_number_id"] = phoneNumberId },
                ["contacts"] = new JsonArray(new JsonObject
                {
                    ["profile"] = new JsonObject { ["name"] = "Laura" }, ["user_id"] = userId, ["wa_id"] = waId,
                }),
                ["messages"] = new JsonArray(message),
            };
            return JsonSerializer.Serialize(new
            {
                @object = "whatsapp_business_account",
                entry = new[] { new { id = "222", changes = new[] { new { field = "messages", value = (JsonNode)value } } } },
            });
        }

        private static string Seconds(long timestamp) => timestamp.ToString(CultureInfo.InvariantCulture);
    }
}
