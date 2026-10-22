using System.Net;
using Npgsql;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>
/// Los caminos de recuperación del motor (fix round 1 de la tarea 6): la fila que se reprocesa después de confirmada (P2-R24), la
/// corrida que sobrevive a un tomar y devolver (D-M15, P2-R25), el chip del chatbot al devolver (P2-R26) y los desenlaces que fijaron
/// los rulings: ventana cerrada (D-M14), ciclo y bloque que no existe (P2-R13/R14), traspaso que una persona ganó, versión vieja (D-C1)
/// y el reintento de una corrida que ya esperaba (P2-R5 con base distinta de cero).
/// </summary>
public sealed partial class EngineEndToEndTests
{
    /// <summary>Mensaje y Resolver en la misma unidad: la corrida termina con el primer entrante.</summary>
    private const string ResolveAtOnceFlow = """
        {
          "schema": 1,
          "start": { "firstNodeId": "n1" },
          "variables": [],
          "nodes": [
            { "id": "n1", "kind": "message", "title": "Aviso", "text": "Gracias por escribir.", "outputs": { "next": "n2" } },
            { "id": "n2", "kind": "resolve", "title": "Listo" }
          ]
        }
        """;

    /// <summary>Un ciclo sin espera, que el validador publica (P2-R13).</summary>
    private const string LoopFlow = """
        {
          "schema": 1,
          "start": { "firstNodeId": "n1" },
          "variables": [],
          "nodes": [
            { "id": "n1", "kind": "message", "title": "Uno", "text": "Uno", "outputs": { "next": "n2" } },
            { "id": "n2", "kind": "message", "title": "Dos", "text": "Dos", "outputs": { "next": "n1" } }
          ]
        }
        """;

    /// <summary>La fila vuelve a estar pendiente como si su cierre hubiera fallado después de confirmar la unidad.</summary>
    private static Task<int> ReopenInboxRowAsync(Fixture f, string wamid) =>
        ExecuteAsync(f.ConnectionString,
            """
            UPDATE messaging.automation_inbox AS inbox SET processed_at = NULL, outcome = NULL, next_attempt_at = now() - interval '1 second'
              FROM messaging.messages AS message WHERE message.id = inbox.message_id AND message.wamid = @wamid
            """,
            ("wamid", wamid));

    private static Task<int> ExpireLeasesAsync(Fixture f) =>
        ExecuteAsync(f.ConnectionString, "UPDATE messaging.automation_inbox SET next_attempt_at = now() - interval '1 second' WHERE processed_at IS NULL");

    private static void ExecuteNow(Fixture f, string sql)
    {
        using var connection = new NpgsqlConnection(f.ConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task AReclaimedRowWhoseUnitCommittedIsADuplicate()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);
        var before = await RunAsync(f, "status || '|' || current_node_id || '|' || step_number || '|' || attempts || '|' || updated_at");

        await ReopenInboxRowAsync(f, "w1");
        await DrainAutomationsAsync(f.Host);

        // P2-R24: ni un segundo arranque ni un intento: la corrida queda como la dejó la unidad confirmada.
        Assert.Equal("duplicate", await OutcomesAsync(f));
        Assert.Equal(2, f.Meta.Sends().Count);
        Assert.Equal(before, await RunAsync(f, "status || '|' || current_node_id || '|' || step_number || '|' || attempts || '|' || updated_at"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM automations.conversation_runs"));
    }

    /// <summary>P2-R24: una corrida que terminó en la unidad que se reprocesa ya no está viva; el marcador de la última corrida igual la
    /// reconoce. Sin él, la de <c>Completed</c> (conversación de vuelta en None) arrancaría otra con el mismo <c>RunIdFor</c>. El validador
    /// no publica una salida obligatoria sin conectar, así que la corrida Completed sale de una versión guardada a la que se le corta la
    /// salida de la bienvenida.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AJustEndedRunReprocessedIsADuplicate(bool completed)
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database, flowJson: ResolveAtOnceFlow);
        if (completed)
        {
            await ExecuteAsync(f.ConnectionString, "UPDATE automations.chatbot_versions SET flow = jsonb_set(flow, '{nodes,0,outputs,next}', 'null')");
        }
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);
        Assert.Equal("ended", await OutcomesAsync(f));
        var conversation = await ConversationAsync(f, "status || '|' || automation_status");
        Assert.Equal(completed ? "Open|None" : "Resolved|None", conversation);

        await ReopenInboxRowAsync(f, "w1");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal("duplicate", await OutcomesAsync(f));
        Assert.Single(f.Meta.Sends());
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM automations.conversation_runs"));
        Assert.Equal(conversation, await ConversationAsync(f, "status || '|' || automation_status"));
    }

    /// <summary>D-M15, P2-R25: tomar pausa y no encola; devolver vuelve a Active. Sin la hora del regreso, el siguiente entrante seguiría
    /// la corrida vieja en el mismo paso.</summary>
    [Fact]
    public async Task ATakeAndResumeInterruptsTheLiveRunAndStartsOver()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);
        Assert.Equal(HttpStatusCode.OK, (await ConversationActionAsync(f, "take")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ConversationActionAsync(f, "automation/resume")).StatusCode);

        await InboundAsync(f, "w2", "Ya volví");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal("Interrupted,Waiting", await ScalarAsync<string>(f.ConnectionString,
            "SELECT string_agg(status, ',' ORDER BY started_at) FROM automations.conversation_runs"));
        Assert.Equal("started,started", await OutcomesAsync(f));
        var sends = f.Meta.Sends();
        Assert.Equal(4, sends.Count);
        Assert.Equal(sends[0], sends[2]);
    }

    /// <summary>P2-R26: devuelta al asistente con otro chatbot en el chip, la corrida nueva es la del chatbot que sirve la conexión y el
    /// chip lo muestra.</summary>
    [Fact]
    public async Task AResumeUnderAnotherChatbotUpdatesTheConversationsChatbot()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database, trigger: "Keyword", keywords: ["hola"]);
        await InboundAsync(f, "w1", "buenas tardes");
        await DrainAutomationsAsync(f.Host);
        await ExecuteAsync(f.ConnectionString,
            "UPDATE messaging.conversations SET automation_status = 'Active', automation_started_at = now(), automation_chatbot_id = @c, version = version + 1",
            ("c", Guid.CreateVersion7()));

        await InboundAsync(f, "w2", "cualquier cosa");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal($"Active|{f.ChatbotId}", await ConversationAsync(f, "automation_status || '|' || automation_chatbot_id"));
        Assert.Equal(f.ChatbotId.ToString(), await RunAsync(f, "chatbot_id::text"));
    }

    /// <summary>D-M14: un envío con la ventana de 24 h cerrada termina la corrida <c>WindowClosed</c> y la conversación vuelve a None.</summary>
    [Fact]
    public async Task AClosedWindowEndsTheRunAndReturnsTheConversationToNone()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);

        await InboundAsync(f, "w1", "Hola", DateTimeOffset.UtcNow.AddHours(-25).ToUnixTimeSeconds());
        await DrainAutomationsAsync(f.Host);

        Assert.Equal("WindowClosed|", await RunAsync(f, "status || '|' || coalesce(failure_code, '')"));
        Assert.Equal("None", await ConversationAsync(f, "automation_status"));
        Assert.Equal("ended", await OutcomesAsync(f));
        Assert.Empty(f.Meta.Sends());
    }

    [Fact]
    public async Task ALoopEndsTheRunFailedAndHandsOff()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database, flowJson: LoopFlow);

        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal(["Uno", "Dos"], f.Meta.Sends());
        Assert.Equal("Failed|automations.run.loop_detected", await RunAsync(f, "status || '|' || failure_code"));
        Assert.Equal("Paused|Handoff", await ConversationAsync(f, "automation_status || '|' || automation_pause_reason"));
        Assert.Equal(1L, await AuditAsync(f, "automations.run.failed"));
    }

    /// <summary>P2-R14: sólo una versión guardada corrupta apunta a un bloque que no existe.</summary>
    [Fact]
    public async Task AMissingTargetEndsTheRunFailedAndHandsOff()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await ExecuteAsync(f.ConnectionString, """UPDATE automations.chatbot_versions SET flow = jsonb_set(flow, '{start,firstNodeId}', '"nx"')""");

        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);

        Assert.Empty(f.Meta.Sends());
        Assert.Equal("Failed|automations.run.target_missing", await RunAsync(f, "status || '|' || failure_code"));
        Assert.Equal("Paused|Handoff", await ConversationAsync(f, "automation_status || '|' || automation_pause_reason"));
        Assert.Equal("failed", await OutcomesAsync(f));
    }

    /// <summary>Lista vinculante de la tarea 5: una persona pausó el asistente mientras salía el texto del traspaso; Mensajería rechaza el
    /// traspaso y la corrida termina <c>Interrupted</c> (no se deshace la unidad ni se reintenta).</summary>
    [Fact]
    public async Task AHandoffThatAPersonBeatIsInterrupted()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);
        f.Meta.BeforeAnswer = sent =>
        {
            if (sent == 3)
            {
                ExecuteNow(f, "UPDATE messaging.conversations SET automation_status = 'Paused', automation_paused_at = now(), automation_pause_reason = 'Manual', version = version + 1");
            }
        };

        await TapAsync(f, "w2", "maint", "Mantenimiento");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal("Interrupted|", await RunAsync(f, "status || '|' || coalesce(failure_code, '')"));
        Assert.Equal("Paused|Manual", await ConversationAsync(f, "automation_status || '|' || automation_pause_reason"));
        Assert.Equal("started,interrupted", await OutcomesAsync(f));
        Assert.Equal(0L, await AuditAsync(f, "automations.run.handed_off"));
    }

    /// <summary>D-C1: publicar otra versión no cambia la corrida en curso; sigue con la versión con que empezó.</summary>
    [Fact]
    public async Task ALiveRunKeepsItsVersionAfterARepublish()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);
        using (var admin = CreateClient(f.Host, f.Tenant.OwnerUserId, f.Tenant.TenantId, ManagePermissions))
        {
            await RepublishChatbotAsync(admin, f.Tenant.TenantId, f.ChatbotId, FlowJson.Replace("¿Cuál es tu número de documento?", "Versión 2: escribe tu documento.", StringComparison.Ordinal));
        }

        await TapAsync(f, "w2", "tech", "Servicio técnico");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal("¿Cuál es tu número de documento?", f.Meta.Sends()[2]);
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM automations.conversation_runs r JOIN automations.chatbot_versions v ON v.id = r.chatbot_version_id WHERE v.number = 1"));
    }

    /// <summary>P2-R5 con base distinta de cero: la palabra de reinicio reenvía bienvenida y botones desde el StepNumber guardado; si la
    /// unidad se deshace en el segundo envío, el reintento no repite la bienvenida.</summary>
    [Fact]
    public async Task ARetriedUnitOfAWaitingRunDoesNotDuplicate()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database);
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);
        var failing = true;
        f.Meta.BeforeAnswer = sent =>
        {
            if (sent == 4 && failing)
            {
                throw new InvalidOperationException("Graph se cayó a mitad de la unidad.");
            }
        };

        await InboundAsync(f, "w2", "reiniciar");
        await DrainAutomationsAsync(f.Host);
        Assert.Equal("started,-", await OutcomesAsync(f));
        failing = false;
        await ExpireLeasesAsync(f);
        await DrainAutomationsAsync(f.Host);

        Assert.Equal("started,advanced", await OutcomesAsync(f));
        var sends = f.Meta.Sends();
        Assert.Equal(2, sends.Count(text => text.StartsWith("Hola Laura", StringComparison.Ordinal)));
        Assert.Equal(4L, await BotMessagesAsync(f));
        Assert.Equal("Waiting|n2", await RunAsync(f, "status || '|' || current_node_id"));
    }
}
