using Modules.Automations.Application;
using Modules.Automations.Application.Engine;
using Modules.Automations.Domain;
using Modules.Automations.Domain.Engine;
using Modules.Automations.Domain.Flow;

namespace Modules.Automations.UnitTests;

/// <summary>Spec 2026-11-18 «Execution»: el archivo de un Mensaje se resuelve por tenant al enviar y sale con lo que Storage dice de él.</summary>
public sealed partial class RunProcessorTests
{
    private static readonly Guid MediaFileId = Guid.Parse("7d2f0c55-1a4e-4c1f-9b0e-3f6a2d8e9c11");

    private static readonly ChatbotMediaFile Catalog =
        new(MediaFileId, "image", "catalogo.jpg", "image/jpeg", 2048, new Uri($"https://public.test/{MediaFileId}/catalogo.jpg"));

    private static readonly string ImageFlow = $$"""
        { "schema": 1, "start": { "firstNodeId": "m1" }, "variables": [],
          "nodes": [
            { "id": "m1", "kind": "message", "title": "Catálogo", "text": "Mira, {nombre_contacto}",
              "urlButton": { "label": "Ver", "url": "https://qcode.co/catalogo" },
              "media": { "kind": "image", "fileId": "{{MediaFileId}}" }, "outputs": { "next": null } }
          ] }
        """;

    [Fact]
    public async Task ASendMediaCallsTheMessengerWithTheResolvedFile()
    {
        var store = new MemoryChatbotMedia(Catalog);
        var bed = new Bed(null, flow: ImageFlow, chatbotMedia: store);

        var outcome = await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        var run = Assert.Single(bed.Runs.Added);
        Assert.Equal(RunOutcomes.Ended, outcome);
        var (clientId, message) = Assert.Single(bed.Messenger.MediaSends);
        Assert.Equal(DeterministicClientId.For(run.Id, 0, 0), clientId);
        Assert.Equal(new BotMediaMessage(Catalog, "Mira, Laura", new UrlButton("Ver", "https://qcode.co/catalogo")), message);
        var (tenantId, fileIds) = Assert.Single(store.Asked);
        Assert.Equal((TenantId, MediaFileId), (tenantId, Assert.Single(fileIds)));
    }

    // Sin archivo no hay texto de respaldo: la corrida falla por el camino de un envío rechazado y pasa a un asesor.
    [Fact]
    public async Task AMissingFileFailsTheRunWithMediaNotFound()
    {
        var bed = new Bed(null, flow: ImageFlow, chatbotMedia: new MemoryChatbotMedia());

        await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

        Assert.Equal(
            new HandoffCause(HandoffCauseKind.SendFailed, AutomationsErrorCodes.MediaNotFound, "m1", "Catálogo", null, null, null),
            Assert.Single(bed.Conversations.Causes));
        Assert.Equal("automations.media.not_found", AutomationsErrorCodes.MediaNotFound);
        var run = Assert.Single(bed.Runs.Added);
        Assert.Equal((RunStatus.Failed, AutomationsErrorCodes.MediaNotFound), (run.Status, run.FailureCode));
        Assert.Empty(bed.Messenger.MediaSends);
        Assert.Empty(bed.Messenger.Texts);
    }

    /// <summary>
    /// Review Focus 4: el audio y su texto son dos envíos con dos clientIds. Si la unidad se repite después de que el audio salió, el audio
    /// vuelve con su mismo clientId (y Mensajería lo da por enviado sin mandarlo otra vez) y el texto con el suyo.
    /// </summary>
    [Fact]
    public async Task ARetriedUnitDoesNotResendTheAudio()
    {
        var flow = $$"""
            { "schema": 1, "start": { "firstNodeId": "q" }, "variables": [],
              "nodes": [
                { "id": "q", "kind": "openQuestion", "title": "Pregunta", "text": "¿En qué te ayudo?", "saveTo": "motivo",
                  "validation": { "kind": "none", "pattern": null, "keywords": null }, "maxAttempts": null,
                  "outputs": { "answered": "m1", "invalid": null } },
                { "id": "m1", "kind": "message", "title": "Saludo", "text": "Escucha este audio", "urlButton": null,
                  "media": { "kind": "audio", "fileId": "{{MediaFileId}}" }, "outputs": { "next": null } }
              ] }
            """;
        var audio = Catalog with { Kind = "audio", FileName = "saludo.mp3", ContentType = "audio/mpeg" };
        var runId = Guid.CreateVersion7();
        var attempts = new List<(Guid Audio, Guid Text)>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var bed = new Bed(null, ActiveState(), flow: flow, chatbotMedia: new MemoryChatbotMedia(audio));
            bed.Runs.Added.Add(ConversationRun.Start(
                runId, TenantId, ConversationId, ConnectionId, ChatbotId, VersionId,
                RunState.Initial(RunVariables.Empty()) with { Status = RunStatus.Waiting, CurrentNodeId = "q" }, DateTimeOffset.UtcNow.AddMinutes(-1)));
            bed.Messenger.Reply = _ => new BotSendOutcome.Rejected("messaging.message.rejected");

            await bed.Processor().ProcessAsync(Item, TestContext.Current.CancellationToken);

            var (audioClientId, message) = Assert.Single(bed.Messenger.MediaSends);
            Assert.Equal(new BotMediaMessage(audio, null, null), message);
            Assert.Equal(["Escucha este audio"], bed.Messenger.Texts);
            Assert.Equal(audioClientId, bed.Messenger.Sent[0]);
            attempts.Add((audioClientId, bed.Messenger.Sent[1]));
            Assert.Equal(HandoffCauseKind.SendFailed, Assert.Single(bed.Conversations.Causes).Kind);
        }

        Assert.NotEqual(attempts[0].Audio, attempts[0].Text);
        Assert.Equal(attempts[0], attempts[1]);
    }
}
