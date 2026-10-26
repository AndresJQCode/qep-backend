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
