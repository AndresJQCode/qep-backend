using System.Net;
using System.Net.Http.Json;
using Modules.Automations.IntegrationTests;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>Spec 2026-10-26 (política de datos) de punta a punta: el bloque <c>dataConsent</c> pregunta por botones, registra el toque
/// en <c>customers.data_consents</c> y no vuelve a preguntar al mismo cliente.</summary>
public sealed partial class EngineEndToEndTests
{
    private const string ConsentPrompt = "Hola {nombre}, lee https://e.co/d y acepta.";
    private const string ConsentPromptSent = "Hola Laura, lee https://e.co/d y acepta.";

    private const string ConsentFlowJson = """
        {
          "schema": 1,
          "start": { "firstNodeId": "n1" },
          "variables": [],
          "nodes": [
            { "id": "n1", "kind": "dataConsent", "title": "Política de datos", "text": "Hola {nombre}, lee https://e.co/d y acepta.", "outputs": { "accepted": "n2", "rejected": "n3" } },
            { "id": "n2", "kind": "message", "title": "Gracias", "text": "Gracias.", "outputs": { "next": "n4" } },
            { "id": "n3", "kind": "message", "title": "Entendido", "text": "Entendido.", "outputs": { "next": "n5" } },
            { "id": "n4", "kind": "resolve", "title": "Listo" },
            { "id": "n5", "kind": "resolve", "title": "Listo" }
          ]
        }
        """;

    private static Task<long> ConsentRowsAsync(Fixture f) =>
        CountAsync(f.ConnectionString, "SELECT count(*) FROM customers.data_consents");

    [Fact]
    public async Task TheConsentBlockAsksRecordsTheTapAndSkipsTheQuestionNextTime()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database, flowJson: ConsentFlowJson);

        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal([$"[button] {ConsentPromptSent}"], f.Meta.Sends());
        var promptId = await ScalarAsync<Guid>(f.ConnectionString,
            "SELECT id FROM messaging.messages WHERE sent_by_kind = 2 ORDER BY occurred_at DESC, id DESC LIMIT 1");

        await TapAsync(f, "w2", "accepted", "Acepto");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal(1L, await ConsentRowsAsync(f));
        Assert.Equal($"Accepted|w2|{ConsentPromptSent}|{promptId}|1", await ScalarAsync<string>(f.ConnectionString,
            "SELECT event || '|' || reply_wamid || '|' || shown_text || '|' || prompt_message_id || '|' || chatbot_version_number FROM customers.data_consents"));
        // La auditoría de Customers viaja por el outbox de la plataforma (ver CustomerDataConsentsServiceTests).
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM platform.outbox_messages WHERE event_name = 'platform.audit.recorded.v1' "
            + "AND payload->>'action' = 'customers.customer.data_consent_accepted' AND payload->>'actorType' = 'System'"));
        Assert.Contains("Gracias.", f.Meta.Sends(), StringComparer.Ordinal);
        var sentBefore = f.Meta.Sends().Count;

        // Una conversación nueva del mismo cliente ya tiene el consentimiento: el bloque sigue por «accepted» sin botones.
        await InboundAsync(f, "w3", "Hola otra vez");
        await DrainAutomationsAsync(f.Host);

        var after = f.Meta.Sends()[sentBefore..];
        Assert.Equal(["Gracias."], after);
        Assert.Equal(1L, await ConsentRowsAsync(f));
    }

    private static async Task<(Guid Id, string Wamid)> LastBotMessageAsync(Fixture f)
    {
        var row = await ScalarAsync<string>(f.ConnectionString,
            "SELECT id || '|' || wamid FROM messaging.messages WHERE sent_by_kind = 2 ORDER BY occurred_at DESC, id DESC LIMIT 1");
        var parts = row.Split('|');
        return (Guid.Parse(parts[0]), parts[1]);
    }

    private static async Task RevokeInThisConversationAsync(Fixture f)
    {
        var customerId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT customer_id FROM messaging.conversations");
        var conversationId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations");
        using var client = CreateClient(f.Host, f.Tenant.OwnerUserId, f.Tenant.TenantId, ["customers.customer.read", "customers.data_consent.revoke"]);
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/tenants/{f.Tenant.TenantId}/customers/{customerId}/data-consent/revocations")
        {
            Content = JsonContent.Create(new { medium = "ThisConversation", conversationId, note = "Lo pidió por el chat." }),
        };
        request.Headers.TryAddWithoutValidation("X-Qep-Client", "web");
        using var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Final review F1 (D-5, D-6): los botones de Política de datos tienen ids fijos y WhatsApp deja tocar los de una pregunta
    /// vieja. Tras una revocación el bot vuelve a preguntar; el «Acepto» viejo cita la primera pregunta y no se toma como respuesta a la
    /// nueva: no hay fila y se pregunta otra vez. El toque sobre la pregunta vigente sí se registra, con esa pregunta como citada.</summary>
    [Fact]
    public async Task ATapOnAnOldPromptIsNotRecordedAsTheAnswerToTheCurrentOne()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database, flowJson: ConsentFlowJson);
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);
        var first = await LastBotMessageAsync(f);
        await TapAsync(f, "w2", "accepted", "Acepto", quotedWamid: first.Wamid);
        await DrainAutomationsAsync(f.Host);
        Assert.Equal($"{first.Id}|{first.Id}", await ScalarAsync<string>(f.ConnectionString,
            "SELECT prompt_message_id || '|' || replied_to_message_id FROM customers.data_consents WHERE reply_wamid = 'w2'"));

        await RevokeInThisConversationAsync(f);
        await InboundAsync(f, "w3", "Hola otra vez");
        await DrainAutomationsAsync(f.Host);
        var second = await LastBotMessageAsync(f);
        Assert.Equal($"[button] {ConsentPromptSent}", f.Meta.Sends()[^1]);

        await TapAsync(f, "w4", "accepted", "Acepto", quotedWamid: first.Wamid);
        await DrainAutomationsAsync(f.Host);

        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM customers.data_consents WHERE reply_wamid = 'w4'"));
        var third = await LastBotMessageAsync(f);
        Assert.NotEqual(second.Id, third.Id);
        Assert.Equal($"[button] {ConsentPromptSent}", f.Meta.Sends()[^1]);

        await TapAsync(f, "w5", "accepted", "Acepto", quotedWamid: third.Wamid);
        await DrainAutomationsAsync(f.Host);

        Assert.Equal($"Accepted|{third.Id}|{third.Id}", await ScalarAsync<string>(f.ConnectionString,
            "SELECT event || '|' || prompt_message_id || '|' || replied_to_message_id FROM customers.data_consents WHERE reply_wamid = 'w5'"));
        Assert.Equal(3L, await ConsentRowsAsync(f));
    }

    [Fact]
    public async Task AReplyAfterARepublishIsRecordedWithTheRunVersion()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database, flowJson: ConsentFlowJson);
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);
        using (var admin = CreateClient(f.Host, f.Tenant.OwnerUserId, f.Tenant.TenantId, ManagePermissions))
        {
            await RepublishChatbotAsync(admin, f.Tenant.TenantId, f.ChatbotId,
                ConsentFlowJson.Replace(ConsentPrompt, "Versión 2: acepta la política.", StringComparison.Ordinal));
        }

        await TapAsync(f, "w2", "accepted", "Acepto");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal($"1|{ConsentPromptSent}", await ScalarAsync<string>(f.ConnectionString,
            "SELECT chatbot_version_number || '|' || shown_text FROM customers.data_consents"));
    }

    private const string RepeatFlowJson = """
        {
          "schema": 1,
          "start": { "firstNodeId": "n1" },
          "variables": [],
          "nodes": [
            { "id": "n1", "kind": "dataConsent", "title": "Política de datos", "text": "Hola {nombre}, lee https://e.co/d y acepta.", "onReject": "repeat", "maxAsks": 2, "onRejectMemberId": null, "outputs": { "accepted": "n2" } },
            { "id": "n2", "kind": "message", "title": "Gracias", "text": "Gracias.", "outputs": { "next": "n3" } },
            { "id": "n3", "kind": "resolve", "title": "Listo" }
          ]
        }
        """;

    /// <summary>Spec 2026-10-26 repetir R-3 y R-4: con <c>maxAsks: 2</c> el primer «No acepto» vuelve a preguntar y el segundo pasa a la
    /// bandeja, y cada uno deja su fila <c>Rejected</c> con la pregunta que contestó. Es la prueba de que el contador sobrevive entre dos
    /// unidades del motor por el camino real (cargar la corrida, avanzar, guardar).</summary>
    [Fact]
    public async Task RejectingTwiceRecordsTwoRowsAndHandsOff()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database, flowJson: RepeatFlowJson);
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);
        var first = await LastBotMessageAsync(f);

        await TapAsync(f, "w2", "rejected", "No acepto");
        await DrainAutomationsAsync(f.Host);

        Assert.Equal([$"[button] {ConsentPromptSent}", $"[button] {ConsentPromptSent}"], f.Meta.Sends());
        var second = await LastBotMessageAsync(f);
        Assert.NotEqual(first.Id, second.Id);

        await TapAsync(f, "w3", "rejected", "No acepto", quotedWamid: second.Wamid);
        await DrainAutomationsAsync(f.Host);

        Assert.Equal(2L, await ConsentRowsAsync(f));
        Assert.Equal($"Rejected|w2|{first.Id}", await ScalarAsync<string>(f.ConnectionString,
            "SELECT event || '|' || reply_wamid || '|' || prompt_message_id FROM customers.data_consents WHERE reply_wamid = 'w2'"));
        Assert.Equal($"Rejected|w3|{second.Id}", await ScalarAsync<string>(f.ConnectionString,
            "SELECT event || '|' || reply_wamid || '|' || prompt_message_id FROM customers.data_consents WHERE reply_wamid = 'w3'"));
        Assert.Equal("Open|Paused|Handoff|", await ConversationAsync(f,
            "status || '|' || automation_status || '|' || automation_pause_reason || '|' || coalesce(assigned_member_id::text, '')"));
        Assert.Equal("DataConsentRejected|Política de datos", await ScalarAsync<string>(f.ConnectionString,
            "SELECT details->'handoff'->>'kind' || '|' || (details->'handoff'->>'nodeTitle') FROM messaging.messages WHERE details->>'type' = 'AutomationPaused'"));
        Assert.Equal(2, f.Meta.Sends().Count);
    }

    /// <summary>Review Focus 1: tras repetir, WhatsApp deja tocar el botón de la primera pregunta. Ese toque cita una pregunta que ya no
    /// es la vigente: no es la respuesta a la segunda, no deja fila y se vuelve a preguntar (intento inválido).</summary>
    [Fact]
    public async Task ATapOnTheFirstPromptAfterARepeatRecordsNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database, flowJson: RepeatFlowJson);
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);
        var first = await LastBotMessageAsync(f);
        await TapAsync(f, "w2", "rejected", "No acepto");
        await DrainAutomationsAsync(f.Host);
        Assert.Equal(1L, await ConsentRowsAsync(f));

        await TapAsync(f, "w3", "rejected", "No acepto", quotedWamid: first.Wamid);
        await DrainAutomationsAsync(f.Host);

        Assert.Equal(1L, await ConsentRowsAsync(f));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM customers.data_consents WHERE reply_wamid = 'w2'"));
        Assert.Equal(3, f.Meta.Sends().Count);
        Assert.Equal($"[button] {ConsentPromptSent}", f.Meta.Sends()[^1]);
        Assert.Equal("Open|Active", await ConversationAsync(f, "status || '|' || automation_status"));
    }

    [Fact]
    public async Task ExhaustedConsentFollowsRejectedWithoutARow()
    {
        await using var database = await StartDatabaseAsync();
        using var f = await ArrangeAsync(database, flowJson: ConsentFlowJson);
        await InboundAsync(f, "w1", "Hola");
        await DrainAutomationsAsync(f.Host);

        foreach (var wamid in new[] { "w2", "w3", "w4" })
        {
            await InboundAsync(f, wamid, "hola");
            await DrainAutomationsAsync(f.Host);
        }

        Assert.Equal("Entendido.", f.Meta.Sends()[^1]);
        Assert.Equal(0L, await ConsentRowsAsync(f));
        Assert.Equal(1L, await AuditAsync(f, "automations.run.data_consent_unanswered"));
    }
}
