using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Modules.Automations.Infrastructure.Persistence;
using Modules.Messaging.Application;
using Npgsql;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>
/// Spec 2026-11-01: «Continuar donde iba». Una pausa manual vuelve al asistente sin mandar nada y el siguiente entrante sigue la misma
/// corrida en su paso pendiente. De punta a punta contra el motor: el resumen y el POST salen de la misma regla, y lo que no se puede
/// continuar lo dice el resumen (<c>FlowStart</c>) y lo rechaza el POST (<c>cannot_continue</c>).
/// </summary>
public sealed partial class EngineEndToEndTests
{
    private const string CannotContinueCode = "messaging.automation.cannot_continue";
    private const string PendingNode = "n2";

    /// <summary>La corrida espera en los botones (n2), con la bienvenida ya enviada.</summary>
    private static async Task<Fixture> ArrangeWaitingAsync(TestDatabase database)
    {
        var f = await ArrangeAsync(database);
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);
        Assert.Equal($"Waiting|{PendingNode}", await RunAsync(f, "status || '|' || current_node_id"));
        return f;
    }

    private static async Task<Fixture> ArrangePausedAsync(TestDatabase database)
    {
        var f = await ArrangeWaitingAsync(database);
        Assert.Equal(HttpStatusCode.OK, (await ConversationActionAsync(f, "automation/pause")).StatusCode);
        return f;
    }

    private static Task<HttpResponseMessage> ContinueAsync(Fixture f, long? version = null, bool ifMatch = true, HttpClient? client = null) =>
        PostAutomationAsync(f, "continue", version, ifMatch, client);

    private static Task<long> ContinuedEventsAsync(Fixture f) =>
        CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE kind = 13 AND details->>'type' = 'AutomationContinued'");

    private static Task<long> RunUnitsAsync(Fixture f) => CountAsync(f.ConnectionString, "SELECT count(*) FROM automations.run_units");

    /// <summary>Segundos Unix después de ahora: un entrante posterior a la pausa, que <see cref="Timestamp"/> no da (va 10 min atrás).</summary>
    private static long AfterNow(int seconds) => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + seconds;

    private static async Task TapAtAsync(Fixture f, string wamid, long timestamp, string replyId, string title)
    {
        var response = await PostWebhookAsync(f.Anonymous, Payloads.InboundButtonReply(PhoneNumberId, WaId, wamid, timestamp, replyId, title, null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await DrainDeliveriesAsync(f.Host);
    }

    private static async Task AssertOffersAsync(Fixture f, string mode, bool wrote = false)
    {
        var summary = await SummaryAsync(f);
        Assert.Equal(AutomationStartStates.Available, summary["automationStart"]!.GetValue<string>());
        Assert.Equal(mode, summary["automationContinue"]!["mode"]!.GetValue<string>());
        Assert.Equal(wrote, summary["automationContinue"]!["customerWroteDuringPause"]!.GetValue<bool>());
    }

    private static async Task AssertCannotContinueAsync(Fixture f)
    {
        var version = await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations");
        var before = await ConversationAsync(f, "automation_status || '|' || coalesce(automation_pause_reason, '')");
        var sends = f.Meta.Sends().Count;

        var response = await ContinueAsync(f);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(CannotContinueCode, (await ProblemAsync(response)).Code);
        Assert.Equal(version, await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations"));
        Assert.Equal(before, await ConversationAsync(f, "automation_status || '|' || coalesce(automation_pause_reason, '')"));
        Assert.Equal(0L, await ContinuedEventsAsync(f));
        Assert.Equal(sends, f.Meta.Sends().Count);
    }

    [Fact]
    [Trait("Slice", "Continue")]
    public async Task ContinuingSendsNothingAndTheNextInboundFollowsThePendingStep()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeWaitingAsync(database);
        await InboundAsync(f, "w2", "no entendí");
        await DrainAutomationsAsync(f.Host);
        var attempts = await RunAsync(f, "attempts::text");
        Assert.NotEqual("0", attempts);
        var runId = await RunAsync(f, "id::text");
        var variables = await RunAsync(f, "variables::text");
        var sends = f.Meta.Sends().Count;
        var units = await RunUnitsAsync(f);
        Assert.Equal(HttpStatusCode.OK, (await ConversationActionAsync(f, "automation/pause")).StatusCode);
        await AssertOffersAsync(f, AutomationContinueModes.PendingStep);

        var response = await ContinueAsync(f);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        Assert.Equal("Active", summary["automation"]!["status"]!.GetValue<string>());
        Assert.Null(summary["automationStart"]);
        Assert.Null(summary["automationContinue"]);
        Assert.Equal(sends, f.Meta.Sends().Count);
        Assert.Equal("Active||true", await ConversationAsync(f,
            "automation_status || '|' || coalesce(automation_pause_reason, '') || '|' || (automation_resumed_at IS NULL)::text"));
        Assert.Equal($"Waiting|{PendingNode}|{attempts}|{variables}", await RunAsync(f, "status || '|' || current_node_id || '|' || attempts || '|' || variables::text"));
        Assert.Equal(1L, await RunCountAsync(f));
        Assert.Equal(units, await RunUnitsAsync(f));
        Assert.Equal(1L, await AuditAsync(f, "automations.run.started"));

        await TapAsync(f, "w3", "tech", "Servicio técnico");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal($"{runId}|Waiting|n3", await RunAsync(f, "id::text || '|' || status || '|' || current_node_id"));
        Assert.Equal(1L, await RunCountAsync(f));
        var after = f.Meta.Sends()[sends..];
        Assert.Contains("¿Cuál es tu número de documento?", Assert.Single(after), StringComparison.Ordinal);
    }

    /// <summary>D-13: el evento lleva quién y la auditoría es de esa persona; ninguna corrida arranca.</summary>
    [Fact]
    [Trait("Slice", "Continue")]
    public async Task ContinuingLeavesTheEventAndTheAudit()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangePausedAsync(database);
        var owner = await OwnerMembershipIdAsync(f.ConnectionString, f.Tenant);

        Assert.Equal(HttpStatusCode.OK, (await ContinueAsync(f)).StatusCode);

        Assert.Equal(1L, await ContinuedEventsAsync(f));
        Assert.Equal(owner.ToString(), await ScalarAsync<string>(f.ConnectionString,
            "SELECT details->>'actor' FROM messaging.messages WHERE kind = 13 AND details->>'type' = 'AutomationContinued'"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE action = 'messaging.conversation.automation_continued' AND actor_id = @actor AND tenant_id = @t",
            ("actor", f.Tenant.OwnerUserId), ("t", f.Tenant.TenantId)));
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'messaging.conversation.automation_resumed'"));
    }

    /// <summary>P2-R25 sigue en pie: el mismo caso por <c>resume</c> interrumpe la corrida y empieza desde el primer bloque.</summary>
    [Fact]
    [Trait("Slice", "Continue")]
    public async Task ResumeStillRestartsTheFlow()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangePausedAsync(database);
        var sends = f.Meta.Sends().Count;

        Assert.Equal(HttpStatusCode.OK, (await ConversationActionAsync(f, "automation/resume")).StatusCode);
        Assert.Equal(sends, f.Meta.Sends().Count);
        await TapAsync(f, "w2", "tech", "Servicio técnico");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal("Interrupted,Waiting", await ScalarAsync<string>(f.ConnectionString,
            "SELECT string_agg(status, ',' ORDER BY started_at) FROM automations.conversation_runs"));
        Assert.Equal([FirstFlowSends, ButtonsSend], f.Meta.Sends()[sends..]);
    }

    /// <summary>D-4 y D-5, una fila por regla: el resumen dice <c>FlowStart</c> y el POST rechaza, sin cambios.</summary>
    [Theory]
    [Trait("Slice", "Continue")]
    [InlineData("running")]
    [InlineData("no-run")]
    [InlineData("handoff")]
    [InlineData("advisor-took")]
    [InlineData("advisor-replied")]
    [InlineData("advisor-transferred")]
    [InlineData("resume-then-pause")]
    [InlineData("other-chatbot")]
    [InlineData("taken-after-pause")]
    [InlineData("auto-taken-after-pause")]
    [InlineData("transferred-after-pause")]
    public async Task TheSummaryAndThePostAgreeWhenItCannotContinue(string scenario)
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangePausedAsync(database);
        switch (scenario)
        {
            case "running":
                await ExecuteAsync(f.ConnectionString, "UPDATE automations.conversation_runs SET status = 'Running'");
                break;
            case "no-run":
                await ExecuteAsync(f.ConnectionString, "UPDATE automations.conversation_runs SET status = 'Completed', ended_at = now()");
                break;
            case "handoff":
                await SetPauseReasonAsync(f, "Handoff");
                break;
            case "advisor-took":
                await SetPauseReasonAsync(f, "AdvisorTook");
                break;
            case "advisor-replied":
                await SetPauseReasonAsync(f, "AdvisorReplied");
                break;
            case "advisor-transferred":
                await SetPauseReasonAsync(f, "AdvisorTransferred");
                break;
            case "resume-then-pause":
                Assert.Equal(HttpStatusCode.OK, (await ConversationActionAsync(f, "automation/resume")).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await ConversationActionAsync(f, "automation/pause")).StatusCode);
                break;
            case "other-chatbot":
                await ExecuteAsync(f.ConnectionString, "UPDATE automations.conversation_runs SET chatbot_id = @c", ("c", Guid.CreateVersion7()));
                break;
            case "taken-after-pause":
                await TakeAndReleaseAsync(f);
                break;
            case "auto-taken-after-pause":
                // Responder sin asignar autoasigna y deja AutoTaken; el motivo de la pausa no cambia: la trampa de §1.
                await ReplyAsync(f);
                Assert.Equal(HttpStatusCode.OK, (await ConversationActionAsync(f, "release")).StatusCode);
                Assert.Equal("Paused|Manual", await ConversationAsync(f, "automation_status || '|' || automation_pause_reason"));
                Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE kind = 13 AND details->>'type' = 'AutoTaken'"));
                break;
            case "transferred-after-pause":
                await TakeAndReleaseAsync(f);
                // Transferir exige un segundo miembro y lo único que cuenta aquí es el tipo del evento.
                await ExecuteAsync(f.ConnectionString, "UPDATE messaging.messages SET details = jsonb_set(details, '{type}', '\"Transferred\"') WHERE kind = 13 AND details->>'type' = 'Taken'");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }

        await AssertOffersAsync(f, AutomationContinueModes.FlowStart);
        await AssertCannotContinueAsync(f);
    }

    private static async Task SetPauseReasonAsync(Fixture f, string reason) =>
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET automation_pause_reason = @r, version = version + 1", ("r", reason));

    private static async Task TakeAndReleaseAsync(Fixture f)
    {
        Assert.Equal(HttpStatusCode.OK, (await ConversationActionAsync(f, "take")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ConversationActionAsync(f, "release")).StatusCode);
        Assert.Equal("Paused|Manual", await ConversationAsync(f, "automation_status || '|' || automation_pause_reason"));
    }

    private static async Task ReplyAsync(Fixture f)
    {
        using var client = CreateClient(f.Host, f.Tenant.OwnerUserId, f.Tenant.TenantId, MessagingManage);
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Qep-Client", "web");
        var conversationId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");
        var response = await client.PostAsJsonAsync($"{ConversationsUrl(f)}/{conversationId}/messages", new { clientId = Guid.CreateVersion7(), text = "Te ayudo yo." }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>D-5: sólo cuentan los eventos desde la pausa actual. Tomó, la devolvió y arrancó otra corrida: esa pausa nueva se continúa.</summary>
    [Fact]
    [Trait("Slice", "Continue")]
    public async Task AnAdvisorBeforeALaterPauseDoesNotCount()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeWaitingAsync(database);
        Assert.Equal(HttpStatusCode.OK, (await ConversationActionAsync(f, "take")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ConversationActionAsync(f, "automation/resume")).StatusCode);
        await TapAsync(f, "w2", "tech", "Servicio técnico");
        await DrainAutomationsAsync(f.Host);
        Assert.Equal("Interrupted,Waiting", await ScalarAsync<string>(f.ConnectionString,
            "SELECT string_agg(status, ',' ORDER BY started_at) FROM automations.conversation_runs"));
        Assert.Equal(HttpStatusCode.OK, (await ConversationActionAsync(f, "automation/pause")).StatusCode);

        await AssertOffersAsync(f, AutomationContinueModes.PendingStep);

        Assert.Equal(HttpStatusCode.OK, (await ContinueAsync(f)).StatusCode);
    }

    /// <summary>Una corrida de arranque manual lleva <c>StartedAt</c> en el piso de <c>AutomationResumedAt</c>: no es posterior.</summary>
    [Fact]
    [Trait("Slice", "Continue")]
    public async Task ARunOfAManualStartContinues()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeStartableAsync(database);
        Assert.Equal(HttpStatusCode.OK, (await StartAsync(f)).StatusCode);
        await DrainAutomationsAsync(f.Host);
        var runId = await RunAsync(f, "id::text");
        Assert.Equal(HttpStatusCode.OK, (await ConversationActionAsync(f, "automation/pause")).StatusCode);
        await AssertOffersAsync(f, AutomationContinueModes.PendingStep);

        Assert.Equal(HttpStatusCode.OK, (await ContinueAsync(f)).StatusCode);
        await TapAsync(f, "w2", "tech", "Servicio técnico");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal($"{runId}|Waiting|n3", await RunAsync(f, "id::text || '|' || status || '|' || current_node_id"));
    }

    /// <summary>§1: pausada, el barrido la cierra <c>Interrupted</c> a las 24 h y la conversación sigue pausada; el resumen ya no promete.</summary>
    [Fact]
    [Trait("Slice", "Continue")]
    public async Task APausedRunSweptAtTwentyFourHoursMovesToFlowStart()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangePausedAsync(database);
        await ExecuteAsync(f.ConnectionString, "UPDATE automations.conversation_runs SET updated_at = now() - interval '25 hours'");

        await SweepAutomationsAsync(f.Host, DateTimeOffset.UtcNow);

        Assert.Equal("Interrupted", await RunAsync(f, "status"));
        Assert.Equal("Paused|Manual", await ConversationAsync(f, "automation_status || '|' || automation_pause_reason"));
        await AssertOffersAsync(f, AutomationContinueModes.FlowStart);
    }

    /// <summary>D-9: continuada a la hora 23, sigue viva a la hora 24 y a las 24 h del clic termina <c>Abandoned</c>.</summary>
    [Fact]
    [Trait("Slice", "Continue")]
    public async Task TheAbandonClockCountsFromTheClick()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangePausedAsync(database);
        await ExecuteAsync(f.ConnectionString, "UPDATE automations.conversation_runs SET updated_at = now() - interval '23 hours'");

        Assert.Equal(HttpStatusCode.OK, (await ContinueAsync(f)).StatusCode);
        var clicked = DateTimeOffset.UtcNow;

        await SweepAutomationsAsync(f.Host, clicked.AddHours(1));
        Assert.Equal("Waiting", await RunAsync(f, "status"));
        Assert.Equal("Active", await ConversationAsync(f, "automation_status"));

        await SweepAutomationsAsync(f.Host, clicked.AddHours(24).AddMinutes(1));
        Assert.Equal("Abandoned", await RunAsync(f, "status"));
        Assert.Equal("None", await ConversationAsync(f, "automation_status"));
    }

    /// <summary>D-8: el barrido no sube la versión, así que <c>If-Match</c> no lo ve; lo ve la relectura bajo el candado, y no cae a resume.</summary>
    [Fact]
    [Trait("Slice", "Continue")]
    public async Task ARunClosedWithTheModalOpenCannotContinue()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangePausedAsync(database);
        var read = await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations");
        await ExecuteAsync(f.ConnectionString, "UPDATE automations.conversation_runs SET updated_at = now() - interval '25 hours'");
        await SweepAutomationsAsync(f.Host, DateTimeOffset.UtcNow);

        var response = await ContinueAsync(f, version: read);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(CannotContinueCode, (await ProblemAsync(response)).Code);
        Assert.Equal("Paused|Manual|true", await ConversationAsync(f,
            "automation_status || '|' || automation_pause_reason || '|' || (automation_resumed_at IS NULL)::text"));
        Assert.Equal(read, await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations"));
        Assert.Equal(0L, await ContinuedEventsAsync(f));
    }

    [Fact]
    [Trait("Slice", "Continue")]
    public async Task TheLockTakenIs412AndChangesNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangePausedAsync(database);
        var conversationId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");

        await using (var holder = new NpgsqlConnection(f.ConnectionString))
        {
            await holder.OpenAsync(Ct);
            await using var transaction = await holder.BeginTransactionAsync(Ct);
            await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@ns, hashtext(@c))", holder, transaction))
            {
                command.Parameters.AddWithValue("ns", AutomationsUnitOfWork.ConversationLockNamespace);
                command.Parameters.AddWithValue("c", conversationId.ToString());
                await command.ExecuteNonQueryAsync(Ct);
            }

            var response = await ContinueAsync(f);

            Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
            Assert.Equal("Paused", await ConversationAsync(f, "automation_status"));
            Assert.Equal(0L, await ContinuedEventsAsync(f));
            await transaction.CommitAsync(Ct);
        }
    }

    /// <summary>Una vez hecho, la versión vieja es 412 y la nueva ve al asistente atendiendo: una sola transición.</summary>
    [Fact]
    [Trait("Slice", "Continue")]
    public async Task TwoCallsInARowMakeOneTransition()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangePausedAsync(database);
        var read = await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations");

        var first = await ContinueAsync(f, version: read);
        var stale = await ContinueAsync(f, version: read);
        var fresh = await ContinueAsync(f);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, fresh.StatusCode);
        Assert.Equal("messaging.automation.already_active", (await ProblemAsync(fresh)).Code);
        Assert.Equal(1L, await ContinuedEventsAsync(f));
    }

    [Fact]
    [Trait("Slice", "Continue")]
    public async Task TwoSimultaneousCallsMakeOneTransition()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangePausedAsync(database);
        var read = await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations");

        var responses = await Task.WhenAll(ContinueAsync(f, version: read), ContinueAsync(f, version: read));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.UnprocessableEntity);
        Assert.Equal(1L, await ContinuedEventsAsync(f));
    }

    /// <summary>La ingesta sube la versión por SQL aunque el entrante no se encole: quien leyó antes ya tiene una versión vieja.</summary>
    [Fact]
    [Trait("Slice", "Continue")]
    public async Task AnInboundInTheMiddleIs412()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangePausedAsync(database);
        var read = await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations");

        await InboundAsync(f, "w2", "¿Sigues ahí?", AfterNow(2));
        var response = await ContinueAsync(f, version: read);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal("Paused", await ConversationAsync(f, "automation_status"));
        Assert.Equal(0L, await ContinuedEventsAsync(f));
    }

    /// <summary>D-10: lo escrito en la pausa no se reprocesa y se avisa; el siguiente entrante sí avanza.</summary>
    [Fact]
    [Trait("Slice", "Continue")]
    public async Task AnInboundDuringThePauseAdvancesNothingUntilTheNextOne()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangePausedAsync(database);
        await InboundAsync(f, "w2", "Servicio técnico", AfterNow(2));
        await DrainAutomationsAsync(f.Host);
        await AssertOffersAsync(f, AutomationContinueModes.PendingStep, wrote: true);
        var sends = f.Meta.Sends().Count;

        Assert.Equal(HttpStatusCode.OK, (await ContinueAsync(f)).StatusCode);
        await DrainAutomationsAsync(f.Host);

        Assert.Equal(sends, f.Meta.Sends().Count);
        Assert.Equal($"Waiting|{PendingNode}", await RunAsync(f, "status || '|' || current_node_id"));

        await TapAtAsync(f, "w3", AfterNow(4), "tech", "Servicio técnico");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal("Waiting|n3", await RunAsync(f, "status || '|' || current_node_id"));
        Assert.Contains("¿Cuál es tu número de documento?", Assert.Single(f.Meta.Sends()[sends..]), StringComparison.Ordinal);
    }

    /// <summary>D-12: publicar durante la pausa no le cambia el flujo a la corrida (D-C1); la palabra de reinicio la pasa a la nueva.</summary>
    [Fact]
    [Trait("Slice", "Continue")]
    public async Task ANewVersionLeavesTheRunOnItsOwnAndTheRestartWordMovesIt()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangePausedAsync(database);
        using (var admin = CreateClient(f.Host, f.Tenant.OwnerUserId, f.Tenant.TenantId, ManagePermissions))
        {
            await RepublishChatbotAsync(admin, f.Tenant.TenantId, f.ChatbotId, FlowJson.Replace(
                "Hola {nombre_contacto}, bienvenido a la línea de servicio.", "Hola {nombre_contacto}, esta es la versión 2.", StringComparison.Ordinal));
        }

        await AssertOffersAsync(f, AutomationContinueModes.PendingStep);
        Assert.Equal(HttpStatusCode.OK, (await ContinueAsync(f)).StatusCode);
        Assert.Equal("Waiting|1", await ScalarAsync<string>(f.ConnectionString,
            "SELECT r.status || '|' || v.number FROM automations.conversation_runs r JOIN automations.chatbot_versions v ON v.id = r.chatbot_version_id"));

        await InboundAsync(f, "w2", "REINICIAR");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal("Completed|1,Waiting|2", await ScalarAsync<string>(f.ConnectionString,
            "SELECT string_agg(r.status || '|' || v.number, ',' ORDER BY r.started_at) FROM automations.conversation_runs r JOIN automations.chatbot_versions v ON v.id = r.chatbot_version_id"));
    }

    /// <summary>D-12: chatbot en pausa, sin modal y queda «Devolver al asistente».</summary>
    [Fact]
    [Trait("Slice", "Continue")]
    public async Task APausedChatbotOffersNoModalAndIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangePausedAsync(database);
        Assert.Equal(HttpStatusCode.OK, (await ChatbotActionAsync(f, "pause")).StatusCode);

        var summary = await SummaryAsync(f);
        var response = await ContinueAsync(f);

        Assert.Equal(AutomationStartStates.NoChatbot, summary["automationStart"]!.GetValue<string>());
        Assert.Null(summary["automationContinue"]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("messaging.automation.no_chatbot", (await ProblemAsync(response)).Code);
    }

    /// <summary>D-12: el POST lee la versión de la corrida bajo el candado; el resumen no, para no cargar el flujo en cada detalle.</summary>
    [Fact]
    [Trait("Slice", "Continue")]
    public async Task AnUnreadableVersionCannotContinueAndTouchesNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangePausedAsync(database);
        await ExecuteAsync(f.ConnectionString, "UPDATE automations.chatbot_versions SET flow = '{\"schema\": 999}'");
        var clock = await RunAsync(f, "updated_at::text");

        await AssertOffersAsync(f, AutomationContinueModes.PendingStep);
        await AssertCannotContinueAsync(f);

        Assert.Equal(clock, await RunAsync(f, "updated_at::text"));
    }

    [Fact]
    [Trait("Slice", "Continue")]
    public async Task AnotherTenantAndAMissingPermissionAre403WithoutIfMatchIs428AndAStaleVersionIs412()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangePausedAsync(database);
        var other = await RegisterTenantAsync(f.Host);
        using var foreign = CreateClient(f.Host, other.OwnerUserId, other.TenantId, MessagingManage);
        using var readOnly = CreateClient(f.Host, f.Tenant.OwnerUserId, f.Tenant.TenantId, MessagingPermissions.ConversationRead);
        var version = await ScalarAsync<long>(f.ConnectionString, "SELECT version FROM messaging.conversations");

        var underForeignTenant = await ContinueAsync(f, client: foreign);
        var withoutPermission = await ContinueAsync(f, client: readOnly);
        var missing = await ContinueAsync(f, ifMatch: false);
        var stale = await ContinueAsync(f, version: version - 1);

        // Nunca 404: un 404 confirmaría que el id existe en otro tenant.
        Assert.Equal(HttpStatusCode.Forbidden, underForeignTenant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, withoutPermission.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(0L, await ContinuedEventsAsync(f));
    }
}
