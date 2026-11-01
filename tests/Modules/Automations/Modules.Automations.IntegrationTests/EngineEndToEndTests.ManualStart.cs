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
}
