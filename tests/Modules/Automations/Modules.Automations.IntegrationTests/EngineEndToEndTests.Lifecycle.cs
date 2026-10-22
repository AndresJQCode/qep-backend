using System.Net;
using System.Net.Http.Json;
using Modules.Automations.Application;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>
/// La vida de una corrida de punta a punta (tarea 7): el orden de los entrantes de una conversación (§5.2, uno por unidad), la ventana
/// cerrada a mitad de corrida (D-M9, D-M14), la palabra de reinicio y los intentos agotados (D-C7, D-M7), el abandono a las 24 h y la purga
/// del inbox (D-M13, P2-R7), la reapertura (D-M16), el chatbot que dejó de atender (D-M15) y el rechazo de Graph (D-M14).
/// </summary>
public sealed partial class EngineEndToEndTests
{
    private static Task<string> RunsAsync(Fixture f) =>
        ScalarAsync<string>(f.ConnectionString, "SELECT string_agg(status, ',' ORDER BY started_at) FROM automations.conversation_runs");

    private static async Task<HttpResponseMessage> ChatbotActionAsync(Fixture f, string action)
    {
        using var admin = CreateClient(f.Host, f.Tenant.OwnerUserId, f.Tenant.TenantId, ManagePermissions);
        var url = $"{AutomationsUrl(f.Tenant.TenantId)}/chatbots/{f.ChatbotId}";
        var editor = (await admin.GetFromJsonAsync<ChatbotEditorDto>(url, Ct))!;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{url}/{action}");
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{editor.Chatbot.Version}\"");
        return await admin.SendAsync(request, Ct);
    }

    /// <summary>§5.2: con una fila por pasada se ve que el segundo entrante espera al primero; al revés, la cédula caería en los botones
    /// como intento inválido y el toque dejaría la corrida esperando la pregunta.</summary>
    [Fact]
    public async Task TwoInboundsAreProcessedInOrderOnePerPass()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database, inboxBatchSize: 1);
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);

        await TapAsync(f, "w2", "tech", "Servicio técnico");
        await InboundAsync(f, "w3", "10203");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal("Waiting|n3|0|", await RunAsync(f, "status || '|' || current_node_id || '|' || attempts || '|' || coalesce(variables->>'cedula', '')"));
        Assert.Equal("started,advanced,-", await OutcomesAsync(f));

        await DrainAutomationsAsync(f.Host);

        Assert.Equal("Resolved|10203", await RunAsync(f, "status || '|' || (variables->>'cedula')"));
        Assert.Equal("started,advanced,ended", await OutcomesAsync(f));
    }

    /// <summary>D-M9 y D-M14: la respuesta llega pero la ventana ya se cerró (un HTTP lento, en la práctica): el bot no envía, la corrida
    /// termina <c>WindowClosed</c> y la conversación vuelve a None sin pasar a un asesor.</summary>
    [Fact]
    public async Task AClosedWindowEndsWindowClosedAndReturnsToNone()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);
        await TapAsync(f, "w2", "tech", "Servicio técnico");
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET last_inbound_at = now() - interval '25 hours'");

        await DrainAutomationsAsync(f.Host);

        Assert.Equal("WindowClosed|", await RunAsync(f, "status || '|' || coalesce(failure_code, '')"));
        Assert.Equal("Open|None|", await ConversationAsync(f, "status || '|' || automation_status || '|' || coalesce(automation_pause_reason, '')"));
        Assert.Equal("started,ended", await OutcomesAsync(f));
        Assert.Equal(2, f.Meta.Sends().Count);
        Assert.Equal(0L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.messages WHERE kind = 13 AND details->>'type' = 'AutomationPaused'"));
        Assert.Equal(0L, await AuditAsync(f, "automations.run.failed"));
    }

    /// <summary>D-C7: la palabra de reinicio, escrita en una pregunta, vuelve a la bienvenida y olvida lo capturado.</summary>
    [Fact]
    public async Task TheRestartKeywordRestartsTheRun()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);
        await TapAsync(f, "w2", "tech", "Servicio técnico");
        await DrainAutomationsAsync(f.Host);

        await InboundAsync(f, "w3", "Reiniciar");
        await DrainAutomationsAsync(f.Host);

        var sends = f.Meta.Sends();
        Assert.Equal(5, sends.Count);
        Assert.Equal(sends[..2], sends[3..]);
        Assert.Equal("Waiting|n2|0|false|false|Laura", await RunAsync(f,
            "status || '|' || current_node_id || '|' || attempts || '|' || (variables ? 'cedula')::text || '|' || (variables ? 'motivo')::text || '|' || (variables->>'nombre')"));
        Assert.Equal("started,advanced,advanced", await OutcomesAsync(f));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM automations.conversation_runs"));
    }

    /// <summary>D-M7: tres respuestas inválidas con «no válido» sin conectar reinician la corrida desde el primer bloque.</summary>
    [Fact]
    public async Task ThreeInvalidAnswersRestart()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);
        await TapAsync(f, "w2", "tech", "Servicio técnico");
        await DrainAutomationsAsync(f.Host);

        foreach (var wamid in new[] { "w3", "w4", "w5" })
        {
            await InboundAsync(f, wamid, "abc");
            await DrainAutomationsAsync(f.Host);
        }

        var sends = f.Meta.Sends();
        // Bienvenida y botones, la pregunta y sus dos repeticiones, y otra vez bienvenida y botones.
        Assert.Equal(7, sends.Count);
        Assert.Equal(new[] { sends[2], sends[2] }, sends[3..5]);
        Assert.Equal(sends[..2], sends[5..]);
        Assert.Equal("Waiting|n2|0|false", await RunAsync(f, "status || '|' || current_node_id || '|' || attempts || '|' || (variables ? 'motivo')::text"));
    }

    /// <summary>D-M13: una corrida en espera más de 24 h sin entrante termina <c>Abandoned</c> y suelta la conversación (D-M14); el siguiente
    /// entrante arranca otra. Una que espera desde hace menos no se toca.</summary>
    [Fact]
    public async Task AWaitingRunOlderThanADayIsAbandoned()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database, trigger: "Keyword", keywords: ["hola"]);
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);

        await SweepAutomationsAsync(f.Host, DateTimeOffset.UtcNow);
        Assert.Equal("Waiting", await RunAsync(f, "status"));

        await ExecuteAsync(f.ConnectionString, "UPDATE automations.conversation_runs SET updated_at = now() - interval '25 hours'");
        await SweepAutomationsAsync(f.Host, DateTimeOffset.UtcNow);

        Assert.Equal("Abandoned||true", await RunAsync(f, "status || '|' || coalesce(failure_code, '') || '|' || (ended_at IS NOT NULL)::text"));
        Assert.Equal("Open|None", await ConversationAsync(f, "status || '|' || automation_status"));
        Assert.Equal(1L, await AuditAsync(f, "automations.run.abandoned"));

        await InboundAsync(f, "w2", "hola");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal("Abandoned,Waiting", await RunsAsync(f));
        Assert.Equal("started,started", await OutcomesAsync(f));
        Assert.Equal(4, f.Meta.Sends().Count);
    }

    /// <summary>P2-R7: el barrido purga las filas del inbox procesadas hace más de <c>InboxRetentionDays</c>; las recientes y las pendientes
    /// quedan.</summary>
    [Fact]
    public async Task TheSweepPurgesProcessedInboxRowsPastTheRetention()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);
        await InboundAsync(f, "w2", "no sé");
        await DrainAutomationsAsync(f.Host);
        await ExecuteAsync(f.ConnectionString,
            """
            UPDATE messaging.automation_inbox AS inbox SET processed_at = now() - interval '8 days'
              FROM messaging.messages AS message WHERE message.id = inbox.message_id AND message.wamid = 'w1'
            """);
        await InboundAsync(f, "w3", "tampoco");

        await SweepAutomationsAsync(f.Host, DateTimeOffset.UtcNow);

        Assert.Equal("advanced,-", await OutcomesAsync(f));
    }

    /// <summary>D-M16: el bot resolvió la conversación; el cliente vuelve a escribir, el entrante la reabre (<c>opened</c>) y el disparador
    /// <c>NewConversation</c> arranca otra corrida.</summary>
    [Fact]
    public async Task AResolvedConversationReopenedByTheCustomerStartsAgain()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);
        await TapAsync(f, "w2", "tech", "Servicio técnico");
        await DrainAutomationsAsync(f.Host);
        await InboundAsync(f, "w3", "10203");
        await DrainAutomationsAsync(f.Host);
        Assert.Equal("Resolved|None", await ConversationAsync(f, "status || '|' || automation_status"));

        await InboundAsync(f, "w4", "Hola otra vez");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal("true,false,false,true", await ScalarAsync<string>(f.ConnectionString,
            "SELECT string_agg(opened::text, ',' ORDER BY created_at, message_id) FROM messaging.automation_inbox"));
        Assert.Equal("Resolved,Waiting", await RunsAsync(f));
        Assert.Equal("started,advanced,ended,started", await OutcomesAsync(f));
        Assert.Equal($"Open|Active|{f.ChatbotId}", await ConversationAsync(f, "status || '|' || automation_status || '|' || automation_chatbot_id"));
        Assert.Equal(6, f.Meta.Sends().Count);
    }

    /// <summary>§5.2 paso 3 y D-M14: devuelta al asistente (Active sin corrida) con el chatbot de la conexión en pausa, la conversación
    /// vuelve a None y la fila queda <c>skipped</c>.</summary>
    [Fact]
    public async Task AnUnpublishedChatbotOnAnActiveConversationEndsIt()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database, trigger: "Keyword", keywords: ["hola"]);
        await InboundAsync(f, "w1", "buenas tardes");
        await DrainAutomationsAsync(f.Host);
        await ExecuteAsync(f.ConnectionString,
            "UPDATE messaging.conversations SET automation_status = 'Active', automation_started_at = now(), automation_chatbot_id = @c, version = version + 1",
            ("c", f.ChatbotId));
        Assert.Equal(HttpStatusCode.OK, (await ChatbotActionAsync(f, "pause")).StatusCode);

        await InboundAsync(f, "w2", "hola");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal("None", await ConversationAsync(f, "automation_status"));
        Assert.Equal("skipped,skipped", await OutcomesAsync(f));
        Assert.Empty(f.Meta.Sends());
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM automations.conversation_runs"));
    }

    /// <summary>D-M14 y P2-R21 a mitad de corrida: Graph rechaza la pregunta (131026, el número no recibe), la corrida termina
    /// <c>Failed</c> con el código de Mensajería y la conversación pasa a un asesor.</summary>
    [Fact]
    public async Task AGraphRejectionFailsTheRunAndHandsOff()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);
        f.Meta.RejectFrom = 3;
        f.Meta.RejectCode = 131026;

        await TapAsync(f, "w2", "tech", "Servicio técnico");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal("Failed|messaging.message.rejected|tech", await RunAsync(f, "status || '|' || failure_code || '|' || (variables->'motivo'->>'id')"));
        Assert.Equal("Open|Paused|Handoff", await ConversationAsync(f, "status || '|' || automation_status || '|' || automation_pause_reason"));
        Assert.Equal("started,failed", await OutcomesAsync(f));
        Assert.Equal(1L, await AuditAsync(f, "automations.run.failed"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.messages WHERE kind = 13 AND details->>'type' = 'AutomationPaused'"));
    }
}
