using System.Net;
using System.Text.Json;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

// Spec 2026-11-18 («Lifecycle»), plan Task 12: borrar un chatbot escribe, en la misma transacción, el aviso que Storage consume para soltar
// los archivos de sus bloques «Mensaje». Sin archivos no escribe nada, y un borrado que se rechaza tampoco.
public sealed partial class ChatbotsApiTests
{
    internal const string MediaReleasedEvent = "automations.chatbot.media-released.v1";

    [Fact]
    public async Task DeletingAChatbotWithMediaWritesOneReleaseInTheSameTransaction()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (id, version) = await PublishableAsync(setup);
        var published = await ReadEditorAsync(await ActAsync(setup.Client, setup, id, "publish", version));
        var draftFile = Guid.CreateVersion7();
        var versionFile = Guid.CreateVersion7();
        // Una pestaña vieja guardó el borrador sin el archivo de la versión publicada: los dos cuentan.
        Assert.Equal(1, await ExecuteAsync(connectionString,
            "UPDATE automations.chatbots SET draft_flow = @flow::jsonb WHERE id = @id",
            ("flow", ChatbotMediaReferencesTests.MediaFlow(draftFile)), ("id", id)));
        Assert.Equal(1, await ExecuteAsync(connectionString,
            "UPDATE automations.chatbot_versions SET flow = @flow::jsonb WHERE chatbot_id = @id",
            ("flow", ChatbotMediaReferencesTests.MediaFlow(versionFile)), ("id", id)));

        var response = await DeleteAsync(setup, id, published.Chatbot.Version);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0L, await CountAsync(connectionString, "SELECT count(*) FROM automations.chatbots WHERE id = @id", ("id", id)));
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM platform.outbox_messages WHERE event_name = @event", ("event", MediaReleasedEvent)));
        using var payload = JsonDocument.Parse(await ScalarAsync<string>(connectionString,
            "SELECT payload::text FROM platform.outbox_messages WHERE event_name = @event", ("event", MediaReleasedEvent)));
        Assert.Equal(setup.Tenant.TenantId, payload.RootElement.GetProperty("tenantId").GetGuid());
        Assert.Equal(
            new HashSet<Guid> { draftFile, versionFile },
            payload.RootElement.GetProperty("fileIds").EnumerateArray().Select(element => element.GetGuid()).ToHashSet());
    }

    [Fact]
    public async Task DeletingAChatbotWithoutMediaWritesNothing()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (id, version) = await PublishableAsync(setup);
        var published = await ReadEditorAsync(await ActAsync(setup.Client, setup, id, "publish", version));

        var response = await DeleteAsync(setup, id, published.Chatbot.Version);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0L, await CountAsync(connectionString,
            "SELECT count(*) FROM platform.outbox_messages WHERE event_name = @event", ("event", MediaReleasedEvent)));
    }
}
