using System.Net;
using System.Net.Http.Json;
using Modules.Automations.Application;
using Modules.Automations.Domain.Flow;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>Spec 2026-11-18 (versiones del chatbot): <c>GET /chatbots/{id}/versions</c> lista las publicaciones y
/// <c>POST /chatbots/{id}/versions/{number}/restore</c> devuelve una al borrador sin tocar estado, versión viva ni conexiones.</summary>
public sealed partial class ChatbotsApiTests
{
    private static string VersionsUrl(Setup setup, Guid chatbotId) => $"{ChatbotsUrl(setup.Tenant.TenantId)}/{chatbotId}/versions";

    private static Task<HttpResponseMessage> RestoreAsync(Setup setup, Guid chatbotId, int number, long? version)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{VersionsUrl(setup, chatbotId)}/{number}/restore");
        if (version is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        }

        return setup.Client.SendAsync(request, Ct);
    }

    private static string EmptyFlow() => FlowJson.Serialize(new FlowDefinition(FlowDefinition.CurrentSchema, new FlowStart(null), [], []));

    [Fact]
    public async Task VersionsListIsNewestFirstAndMarksTheLiveOne()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString, ReadPermissions);
        using var _ = setup.Client;
        // Publicar exige manage: el chatbot se arma con un cliente que puede, y la lista se lee con el de sólo lectura.
        using var manager = CreateClient(factory, setup.Tenant.OwnerUserId, setup.Tenant.TenantId, ManagePermissions);
        var writer = setup with { Client = manager };
        var (id, version) = await PublishableAsync(writer);
        var first = await ReadEditorAsync(await ActAsync(manager, writer, id, "publish", version));
        await ReadEditorAsync(await ActAsync(manager, writer, id, "publish", first.Chatbot.Version));

        var response = await setup.Client.GetAsync(VersionsUrl(setup, id), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = (await response.Content.ReadFromJsonAsync<ChatbotVersionListResponse>(Ct))!;
        Assert.Equal([2, 1], list.Items.Select(item => item.Number));
        Assert.Equal([true, false], list.Items.Select(item => item.IsLive));
        Assert.All(list.Items, item =>
        {
            Assert.NotEqual(Guid.Empty, item.PublishedBy);
            Assert.True(item.BlockCount > 0);
        });
    }

    [Fact]
    public async Task RestoreCopiesTheVersionIntoTheDraftAndTouchesNothingElse()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (id, version) = await PublishableAsync(setup);
        var published = await ReadEditorAsync(await ActAsync(setup.Client, setup, id, "publish", version));
        var edited = await SaveDraftAsync(setup, published, EmptyFlow(), """{ "positions": { "other": { "x": 1, "y": 2 } } }""");
        var draftVersion = (await edited.Content.ReadFromJsonAsync<DraftSavedDto>(Ct))!.Version;

        var response = await RestoreAsync(setup, id, 1, draftVersion);

        var restored = await ReadEditorAsync(response);
        Assert.True(SameJson(restored.Draft.Flow, StoredAnnexAFlow()));
        Assert.True(SameJson(restored.Draft.Layout, """{ "positions": { "n1": { "x": 10, "y": 20 } } }"""));
        Assert.True(restored.Chatbot.Version > draftVersion);
        Assert.Equal(("Active", 1), (restored.Chatbot.Status, restored.Published!.Number));
        Assert.Equal(1L, await ActiveRowsAsync(connectionString, setup.WhatsAppId));
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM audit.entries WHERE resource_id = @id AND action = @action",
            ("id", id.ToString()), ("action", "automations.chatbot.version_restored")));
    }

    [Fact]
    public async Task RestoreGuardsVersionNumberPermissionAndConcurrency()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setup = await SetupAsync(factory, connectionString);
        using var _ = setup.Client;
        var (id, version) = await PublishableAsync(setup);
        var published = await ReadEditorAsync(await ActAsync(setup.Client, setup, id, "publish", version));
        using var reader = CreateClient(factory, setup.Tenant.OwnerUserId, setup.Tenant.TenantId, ReadPermissions);
        var readOnly = setup with { Client = reader };

        var withoutIfMatch = await RestoreAsync(setup, id, 1, null);
        var stale = await RestoreAsync(setup, id, 1, published.Chatbot.Version - 1);
        var unknown = await RestoreAsync(setup, id, 9, published.Chatbot.Version);
        var forbidden = await RestoreAsync(readOnly, id, 1, published.Chatbot.Version);

        Assert.Equal(HttpStatusCode.PreconditionRequired, withoutIfMatch.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("automations.chatbot.version_not_found", (await ProblemAsync(unknown)).Code);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(0L, await CountAsync(connectionString,
            "SELECT count(*) FROM audit.entries WHERE resource_id = @id AND action = @action",
            ("id", id.ToString()), ("action", "automations.chatbot.version_restored")));
    }

    [Fact]
    public async Task VersionsAreScopedToTheirChatbotAndTenant()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString, FakeExternalApis.WithAnnexA());
        var setupA = await SetupAsync(factory, connectionString);
        var setupB = await SetupAsync(factory, connectionString);
        using var a = setupA.Client;
        using var b = setupB.Client;
        var (id, version) = await PublishableAsync(setupA);
        await ReadEditorAsync(await ActAsync(a, setupA, id, "publish", version));

        var underForeignRoute = await b.GetAsync($"{ChatbotsUrl(setupA.Tenant.TenantId)}/{id}/versions", Ct);
        var underOwnRoute = await b.GetAsync($"{ChatbotsUrl(setupB.Tenant.TenantId)}/{id}/versions", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, underForeignRoute.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, underOwnRoute.StatusCode);
    }
}
