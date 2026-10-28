using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-11 §6.2 por HTTP y contra la base: crear, leer, editar, borrar, lanzar y cancelar campañas con sus 403 (otro
/// tenant, sin permiso), 404, 409, 412 y 422 por campo, los contadores siempre completos, la audiencia sin lo borrado, la auditoría en
/// la misma transacción y el preview de audiencia contra Customers de verdad.</summary>
public sealed class CampaignApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Catalog = """
        {"data":[
          {"id":"1","name":"promo","language":"es","status":"APPROVED","category":"MARKETING",
           "components":[{"type":"BODY","text":"Hola {{1}}, tu código es {{2}}"}]},
          {"id":"2","name":"aviso","language":"es","status":"APPROVED","category":"UTILITY",
           "components":[{"type":"BODY","text":"Tu pedido salió"}]}
        ]}
        """;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, HttpClient Client);

    private static string CampaignsUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/messaging/campaigns";

    private static string CampaignUrl(Fixture f, Guid id) => $"{CampaignsUrl(f.Tenant.TenantId)}/{id}";

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        factory.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, Catalog);
        return new Fixture(factory, connectionString, tenant, connectionId, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, CampaignPermissions));
    }

    private static object Input(Fixture f, string name = "Promo de octubre", Guid[]? tagIds = null, Guid[]? customerIds = null, string? scheduledAt = null, string template = "promo") =>
        new
        {
            name,
            connectionId = f.ConnectionId,
            template = new { name = template, language = "es" },
            parameters = template == "promo"
                ? new Dictionary<string, object> { ["1"] = new { source = "customerName" }, ["2"] = new { source = "text", value = "A1234" } }
                : new Dictionary<string, object>(),
            audience = new { tagIds = tagIds ?? [Guid.CreateVersion7()], customerIds = customerIds ?? [] },
            scheduledAt,
        };

    private static async Task<JsonElement> CreateAsync(Fixture f, object? input = null)
    {
        var response = await SendAsync(f.Client, HttpMethod.Post, CampaignsUrl(f.Tenant.TenantId), input ?? Input(f));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static string Status(JsonElement campaign) => campaign.GetProperty("status").GetString()!;

    [Fact]
    public async Task CreatingStoresADraftWithTheCallersNameZeroCountsAndItsAudit()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var campaign = await CreateAsync(f);

        Assert.Equal(("Draft", "Promo de octubre", 1), (Status(campaign), campaign.GetProperty("name").GetString(), campaign.GetProperty("version").GetInt32()));
        Assert.Equal(("promo", "es", "MARKETING"), (
            campaign.GetProperty("template").GetProperty("name").GetString(), campaign.GetProperty("template").GetProperty("language").GetString(), campaign.GetProperty("template").GetProperty("category").GetString()));
        Assert.Equal("Ventas", campaign.GetProperty("connection").GetProperty("name").GetString());
        Assert.Equal(await OwnerMembershipIdAsync(f.ConnectionString, f.Tenant), campaign.GetProperty("createdBy").GetProperty("memberId").GetGuid());
        Assert.False(string.IsNullOrWhiteSpace(campaign.GetProperty("createdBy").GetProperty("displayName").GetString()));
        var counts = campaign.GetProperty("counts");
        foreach (var field in new[] { "total", "pending", "sent", "delivered", "read", "replied", "failed", "skipped" })
        {
            Assert.Equal(0, counts.GetProperty(field).GetInt32());
        }

        Assert.Equal("customerName", campaign.GetProperty("parameters").GetProperty("1").GetProperty("source").GetString());
        Assert.Equal("A1234", campaign.GetProperty("parameters").GetProperty("2").GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, campaign.GetProperty("failureReason").ValueKind);
        Assert.Equal(1L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM audit.entries WHERE action = 'messaging.campaign.created' AND resource_type = 'campaign' AND resource_id = @id", ("id", campaign.GetProperty("id").GetGuid().ToString())));
    }

    [Fact]
    public async Task TheListIsNewestFirstAndPagedAndTheDetailMatches()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var first = await CreateAsync(f, Input(f, name: "Primera"));
        var second = await CreateAsync(f, Input(f, name: "Segunda"));

        var page = await f.Client.GetFromJsonAsync<JsonElement>($"{CampaignsUrl(f.Tenant.TenantId)}?page=1&pageSize=1", Ct);
        var detail = await f.Client.GetFromJsonAsync<JsonElement>(CampaignUrl(f, first.GetProperty("id").GetGuid()), Ct);

        Assert.Equal((2, 1, 1), (page.GetProperty("total").GetInt32(), page.GetProperty("page").GetInt32(), page.GetProperty("pageSize").GetInt32()));
        Assert.Equal(second.GetProperty("id").GetGuid(), page.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal("Primera", detail.GetProperty("name").GetString());
        Assert.Equal(0, detail.GetProperty("audience").GetProperty("customers").GetArrayLength());
    }

    [Fact]
    public async Task EveryRouteIs403ForAnotherTenantAndForWhoeverLacksThePermission()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var campaign = await CreateAsync(f);
        var id = campaign.GetProperty("id").GetGuid();
        using var intruder = CreateClient(f.Factory, f.Tenant.OwnerUserId, Guid.CreateVersion7(), CampaignPermissions);
        using var advisor = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ManagePermissions);
        var url = CampaignsUrl(f.Tenant.TenantId);

        foreach (var client in new[] { intruder, advisor })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Get, url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Get, $"{url}/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Get, $"{url}/{id}/recipients")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Post, $"{url}/audience-preview", new { tagIds = Array.Empty<Guid>(), customerIds = Array.Empty<Guid>(), templateCategory = "MARKETING" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Post, url, Input(f))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Put, $"{url}/{id}", new { name = "x", expectedVersion = 1 })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Delete, $"{url}/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Post, $"{url}/{id}/launch", new { expectedVersion = 1 })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, HttpMethod.Post, $"{url}/{id}/cancel", new { expectedVersion = 1 })).StatusCode);
        }

        // Y la campaña sigue intacta.
        Assert.Equal("Draft", await ScalarAsync<string>(f.ConnectionString, "SELECT status FROM messaging.campaigns"));
    }

    [Fact]
    public async Task AnUnknownCampaignIs404WithItsCode()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        var response = await SendAsync(f.Client, HttpMethod.Get, CampaignUrl(f, Guid.CreateVersion7()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("messaging.campaign.not_found", (await ProblemAsync(response)).Code);
    }

    [Fact]
    public async Task ABadBodyIs422OnTheExactKeysTheFormReads()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var body = new
        {
            name = "",
            connectionId = Guid.Empty,
            template = new { name = "", language = "" },
            parameters = new Dictionary<string, object> { ["1"] = new { source = "text", value = "Ana\nMaría" } },
            audience = new { tagIds = Array.Empty<Guid>(), customerIds = Array.Empty<Guid>() },
            scheduledAt = DateTimeOffset.UtcNow.AddDays(31).ToString("o"),
        };

        var response = await SendAsync(f.Client, HttpMethod.Post, CampaignsUrl(f.Tenant.TenantId), body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, keys) = await ProblemAsync(response);
        Assert.Equal("validation.failed", code);
        Assert.Equal(["audience", "connectionId", "name", "parameters.1", "scheduledAt", "template.language", "template.name"], keys);
    }

    [Fact]
    public async Task ParametersThatAreNotExactlyTheTemplatesAndATemplateNotInTheCatalogAre422WithTheirCodes()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var url = CampaignsUrl(f.Tenant.TenantId);

        var mismatch = await SendAsync(f.Client, HttpMethod.Post, url, new
        {
            name = "x",
            connectionId = f.ConnectionId,
            template = new { name = "promo", language = "es" },
            parameters = new Dictionary<string, object> { ["1"] = new { source = "customerName" } },
            audience = new { tagIds = new[] { Guid.CreateVersion7() }, customerIds = Array.Empty<Guid>() },
        });
        var missing = await SendAsync(f.Client, HttpMethod.Post, url, Input(f, template: "noexiste"));

        Assert.Equal("messaging.template.parameters_mismatch", (await ProblemAsync(mismatch)).Code);
        Assert.Equal("messaging.template.not_available", (await ProblemAsync(missing)).Code);
        Assert.Equal(0L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.campaigns"));
    }

    [Fact]
    public async Task UpdatingADraftChangesItAndAStaleVersionIs412()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var campaign = await CreateAsync(f);
        var id = campaign.GetProperty("id").GetGuid();
        var body = new Dictionary<string, object?>(ToDictionary(Input(f, name: "Renombrada"))) { ["expectedVersion"] = 1 };

        var updated = await SendAsync(f.Client, HttpMethod.Put, CampaignUrl(f, id), body);
        var stale = await SendAsync(f.Client, HttpMethod.Put, CampaignUrl(f, id), body);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var detail = await updated.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("Renombrada", 2), (detail.GetProperty("name").GetString(), detail.GetProperty("version").GetInt32()));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task DeletingADraftIs204AndAFinishedOrLaunchedOneIs409NotEditable()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var draft = (await CreateAsync(f)).GetProperty("id").GetGuid();
        var launched = await CreateAsync(f);
        var launchedId = launched.GetProperty("id").GetGuid();
        await SendAsync(f.Client, HttpMethod.Post, $"{CampaignUrl(f, launchedId)}/launch", new { expectedVersion = 1 });

        var deleted = await SendAsync(f.Client, HttpMethod.Delete, CampaignUrl(f, draft));
        var refused = await SendAsync(f.Client, HttpMethod.Delete, CampaignUrl(f, launchedId));
        var edit = await SendAsync(f.Client, HttpMethod.Put, CampaignUrl(f, launchedId), new Dictionary<string, object?>(ToDictionary(Input(f))) { ["expectedVersion"] = 2 });

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("messaging.campaign.not_editable", (await ProblemAsync(refused)).Code);
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.campaigns"));
    }

    [Fact]
    public async Task LaunchingWithoutADateStartsSendingAndAuditsItAndWithAFutureDateSchedulesIt()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var now = await CreateAsync(f);
        var later = await CreateAsync(f, Input(f, name: "Programada", scheduledAt: DateTimeOffset.UtcNow.AddDays(2).ToString("o")));

        var sending = await SendAsync(f.Client, HttpMethod.Post, $"{CampaignUrl(f, now.GetProperty("id").GetGuid())}/launch", new { expectedVersion = 1 });
        var scheduled = await SendAsync(f.Client, HttpMethod.Post, $"{CampaignUrl(f, later.GetProperty("id").GetGuid())}/launch", new { expectedVersion = 1 });

        var sendingBody = await sending.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var scheduledBody = await scheduled.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("Sending", 2), (Status(sendingBody), sendingBody.GetProperty("version").GetInt32()));
        Assert.NotEqual(JsonValueKind.Null, sendingBody.GetProperty("startedAt").ValueKind);
        Assert.Equal(("Scheduled", JsonValueKind.Null), (Status(scheduledBody), scheduledBody.GetProperty("startedAt").ValueKind));
        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'messaging.campaign.launched'"));
    }

    [Fact]
    public async Task LaunchingWithAStaleVersionIs412AndRelaunchingIs409()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var id = (await CreateAsync(f)).GetProperty("id").GetGuid();

        var stale = await SendAsync(f.Client, HttpMethod.Post, $"{CampaignUrl(f, id)}/launch", new { expectedVersion = 9 });
        var launched = await SendAsync(f.Client, HttpMethod.Post, $"{CampaignUrl(f, id)}/launch", new { expectedVersion = 1 });
        var again = await SendAsync(f.Client, HttpMethod.Post, $"{CampaignUrl(f, id)}/launch", new { expectedVersion = 2 });
        var noVersion = await SendAsync(f.Client, HttpMethod.Post, $"{CampaignUrl(f, id)}/cancel", new { });

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(HttpStatusCode.OK, launched.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noVersion.StatusCode);
        Assert.Equal(["expectedVersion"], (await ProblemAsync(noVersion)).ErrorKeys);
    }

    [Fact]
    public async Task CancellingSkipsThePendingAndKeepsWhatWasInFlightAndAuditsIt()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var id = (await CreateAsync(f)).GetProperty("id").GetGuid();
        await SendAsync(f.Client, HttpMethod.Post, $"{CampaignUrl(f, id)}/launch", new { expectedVersion = 1 });
        await SeedRecipientAsync(f.ConnectionString, f.Tenant.TenantId, id, "Pending");
        await SeedRecipientAsync(f.ConnectionString, f.Tenant.TenantId, id, "Pending");
        await SeedRecipientAsync(f.ConnectionString, f.Tenant.TenantId, id, "Sending");
        await SeedRecipientAsync(f.ConnectionString, f.Tenant.TenantId, id, "Sent");

        var response = await SendAsync(f.Client, HttpMethod.Post, $"{CampaignUrl(f, id)}/cancel", new { expectedVersion = 2 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("Cancelled", 3), (Status(body), body.GetProperty("version").GetInt32()));
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("completedAt").ValueKind);
        var counts = body.GetProperty("counts");
        Assert.Equal((4, 1, 1, 0, 2), (counts.GetProperty("total").GetInt32(), counts.GetProperty("pending").GetInt32(), counts.GetProperty("sent").GetInt32(), counts.GetProperty("failed").GetInt32(), counts.GetProperty("skipped").GetInt32()));
        Assert.Equal(2L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.campaign_recipients WHERE status = 'Skipped' AND failure_code = 'campaign_cancelled'"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'messaging.campaign.cancelled'"));
    }

    [Fact]
    public async Task CancellingADraftIs409NotCancellable()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var id = (await CreateAsync(f)).GetProperty("id").GetGuid();

        var response = await SendAsync(f.Client, HttpMethod.Post, $"{CampaignUrl(f, id)}/cancel", new { expectedVersion = 1 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("messaging.campaign.not_cancellable", (await ProblemAsync(response)).Code);
    }

    [Fact]
    public async Task RetryingRequeuesTheFailedButTheOptOutReopensTheCampaignAndNumbersTheRetry()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var id = (await CreateAsync(f)).GetProperty("id").GetGuid();
        await SendAsync(f.Client, HttpMethod.Post, $"{CampaignUrl(f, id)}/launch", new { expectedVersion = 1 });
        await SeedRecipientAsync(f.ConnectionString, f.Tenant.TenantId, id, "Failed", name: "Ana", failureCode: "131049");
        await SeedRecipientAsync(f.ConnectionString, f.Tenant.TenantId, id, "Failed", name: "Beto", failureCode: "131050");
        await SeedRecipientAsync(f.ConnectionString, f.Tenant.TenantId, id, "Skipped", name: "Caro", failureCode: "campaign_failed");
        await SeedRecipientAsync(f.ConnectionString, f.Tenant.TenantId, id, "Skipped", name: "Dani", failureCode: "opted_out");
        await SeedRecipientAsync(f.ConnectionString, f.Tenant.TenantId, id, "Sent", name: "Eva");
        // Lo que haría el worker: la campaña se detuvo (o terminó) con la versión arriba.
        await ExecuteAsync(f.ConnectionString,
            "UPDATE messaging.campaigns SET status = 'Failed', failure_code = '132015', completed_at = now(), audience_frozen_at = now(), version = 3 WHERE id = @id",
            ("id", id));
        var before = await (await f.Client.GetAsync(CampaignUrl(f, id), Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        var stale = await SendAsync(f.Client, HttpMethod.Post, $"{CampaignUrl(f, id)}/retry", new { expectedVersion = 2 });
        var response = await SendAsync(f.Client, HttpMethod.Post, $"{CampaignUrl(f, id)}/retry", new { expectedVersion = 3 });
        var again = await SendAsync(f.Client, HttpMethod.Post, $"{CampaignUrl(f, id)}/retry", new { expectedVersion = 4 });

        Assert.Equal(2, before.GetProperty("counts").GetProperty("retriable").GetInt32());
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(("Sending", 4, 1), (Status(body), body.GetProperty("version").GetInt32(), body.GetProperty("retryCount").GetInt32()));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("failureCode").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("completedAt").ValueKind);
        var counts = body.GetProperty("counts");
        Assert.Equal((5, 2, 1, 1, 1, 0), (counts.GetProperty("total").GetInt32(), counts.GetProperty("pending").GetInt32(), counts.GetProperty("sent").GetInt32(),
            counts.GetProperty("failed").GetInt32(), counts.GetProperty("skipped").GetInt32(), counts.GetProperty("retriable").GetInt32()));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("messaging.campaign.not_retriable", (await ProblemAsync(again)).Code);
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM audit.entries WHERE action = 'messaging.campaign.retried'"));
        Assert.Equal(2L, await CountAsync(f.ConnectionString,
            "SELECT count(*) FROM messaging.campaign_recipients WHERE status = 'Pending' AND retry_count = 1 AND attempts = 0 AND failure_code IS NULL"));

        var recipients = await (await f.Client.GetAsync($"{CampaignUrl(f, id)}/recipients", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var rows = recipients.GetProperty("items").EnumerateArray()
            .Select(item => (item.GetProperty("customerName").GetString(), item.GetProperty("status").GetString(), item.GetProperty("retryCount").GetInt32(), item.GetProperty("retriable").GetBoolean()))
            .ToList();
        Assert.Equal(
            [("Ana", "Pending", 1, false), ("Beto", "Failed", 0, false), ("Caro", "Pending", 1, false), ("Dani", "Skipped", 0, false), ("Eva", "Sent", 0, false)],
            rows);
    }

    [Fact]
    public async Task RetryingACampaignWhoseOnlyFailuresAreOptOutsIs409NothingToRetry()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var id = (await CreateAsync(f)).GetProperty("id").GetGuid();
        await SendAsync(f.Client, HttpMethod.Post, $"{CampaignUrl(f, id)}/launch", new { expectedVersion = 1 });
        await SeedRecipientAsync(f.ConnectionString, f.Tenant.TenantId, id, "Failed", failureCode: "131050");
        await ExecuteAsync(f.ConnectionString,
            "UPDATE messaging.campaigns SET status = 'Completed', completed_at = now(), audience_frozen_at = now(), version = 3 WHERE id = @id", ("id", id));

        var response = await SendAsync(f.Client, HttpMethod.Post, $"{CampaignUrl(f, id)}/retry", new { expectedVersion = 3 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("messaging.campaign.nothing_to_retry", (await ProblemAsync(response)).Code);
        Assert.Equal("Completed", await ScalarAsync<string>(f.ConnectionString, "SELECT status FROM messaging.campaigns"));
    }

    [Fact]
    public async Task TheRecipientsComeFilteredWithTheirTextsAndTheMessageStatusOfWhatMetaAccepted()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var id = (await CreateAsync(f)).GetProperty("id").GetGuid();
        await SendAsync(f.Client, HttpMethod.Post, $"{CampaignUrl(f, id)}/launch", new { expectedVersion = 1 });
        var conversation = await SeedBsuidConversationAsync(f.Factory, f.ConnectionString, f.Tenant.TenantId, f.ConnectionId, "CO.573001234567", "573001234567");
        var delivered = await SeedOutboundAsync(f.ConnectionString, conversation, f.Tenant.TenantId, f.ConnectionId, "wamid.1", status: 2);
        await SeedRecipientAsync(f.ConnectionString, f.Tenant.TenantId, id, "Sent", name: "Ana", conversationId: conversation, messageId: delivered);
        await SeedRecipientAsync(f.ConnectionString, f.Tenant.TenantId, id, "Skipped", name: "Bruno", failureCode: "opted_out");
        await SeedRecipientAsync(f.ConnectionString, f.Tenant.TenantId, id, "Failed", name: "Carla", failureCode: "131049");

        var all = await f.Client.GetFromJsonAsync<JsonElement>($"{CampaignUrl(f, id)}/recipients", Ct);
        var skipped = await f.Client.GetFromJsonAsync<JsonElement>($"{CampaignUrl(f, id)}/recipients?status=Skipped&pageSize=10", Ct);
        var bad = await SendAsync(f.Client, HttpMethod.Get, $"{CampaignUrl(f, id)}/recipients?status=Nope");

        Assert.Equal(3, all.GetProperty("total").GetInt32());
        Assert.Equal(["Ana", "Bruno", "Carla"], all.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("customerName").GetString()));
        var ana = all.GetProperty("items")[0];
        Assert.Equal(("Sent", "Delivered", conversation), (ana.GetProperty("status").GetString(), ana.GetProperty("messageStatus").GetString(), ana.GetProperty("conversationId").GetGuid()));
        var bruno = skipped.GetProperty("items")[0];
        Assert.Equal(("opted_out", "No quiere recibir promociones."), (bruno.GetProperty("failureCode").GetString(), bruno.GetProperty("failureReason").GetString()));
        Assert.Equal(1, skipped.GetProperty("total").GetInt32());
        var carla = all.GetProperty("items")[2];
        Assert.Equal(JsonValueKind.Null, carla.GetProperty("messageStatus").ValueKind);
        Assert.StartsWith("WhatsApp no entregó", carla.GetProperty("failureReason").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Equal(["status"], (await ProblemAsync(bad)).ErrorKeys);
    }

    [Fact]
    public async Task TheCountsFollowTheRecipientsAndTheRepliesCountAsReadAndAlwaysTravelComplete()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var id = (await CreateAsync(f)).GetProperty("id").GetGuid();
        var conversation = await SeedBsuidConversationAsync(f.Factory, f.ConnectionString, f.Tenant.TenantId, f.ConnectionId, "CO.573001234567", "573001234567");
        var read = await SeedOutboundAsync(f.ConnectionString, conversation, f.Tenant.TenantId, f.ConnectionId, "wamid.read", status: 3);
        var delivered = await SeedOutboundAsync(f.ConnectionString, conversation, f.Tenant.TenantId, f.ConnectionId, "wamid.dlv", status: 2);
        var sentAt = DateTimeOffset.UtcNow.AddHours(-2);
        await SeedRecipientAsync(f.ConnectionString, f.Tenant.TenantId, id, "Sent", name: "Leída", conversationId: conversation, messageId: read, sentAt: sentAt);
        await SeedRecipientAsync(f.ConnectionString, f.Tenant.TenantId, id, "Sent", name: "Sin confirmación", conversationId: conversation, messageId: delivered, sentAt: sentAt);
        await SeedRecipientAsync(f.ConnectionString, f.Tenant.TenantId, id, "Sent", name: "Callada");
        await SeedRecipientAsync(f.ConnectionString, f.Tenant.TenantId, id, "Failed", failureCode: "131026");
        await SeedRecipientAsync(f.ConnectionString, f.Tenant.TenantId, id, "Pending");
        await SeedRecipientAsync(f.ConnectionString, f.Tenant.TenantId, id, "Sending");
        await SeedRecipientAsync(f.ConnectionString, f.Tenant.TenantId, id, "Skipped", failureCode: "inactive");
        // Un entrante de la persona después del envío: respondió (los dos Sent de esa conversación comparten el hilo).
        await ExecuteAsync(f.ConnectionString,
            "INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, wamid, created_at) VALUES (@id, @c, @t, @k, now() - interval '1 hour', 1, 1, 1, 'gracias', 'wamid.in', now())",
            ("id", Guid.CreateVersion7()), ("c", conversation), ("t", f.Tenant.TenantId), ("k", f.ConnectionId));

        var detail = await f.Client.GetFromJsonAsync<JsonElement>(CampaignUrl(f, id), Ct);

        var counts = detail.GetProperty("counts");
        // Read 2: el de wamid.dlv no avisó lectura, pero quien lo recibió respondió; lo leyó con las confirmaciones apagadas.
        Assert.Equal(
            (7, 2, 3, 2, 2, 2, 1, 1),
            (counts.GetProperty("total").GetInt32(), counts.GetProperty("pending").GetInt32(), counts.GetProperty("sent").GetInt32(), counts.GetProperty("delivered").GetInt32(),
             counts.GetProperty("read").GetInt32(), counts.GetProperty("replied").GetInt32(), counts.GetProperty("failed").GetInt32(), counts.GetProperty("skipped").GetInt32()));

        var sent = await f.Client.GetFromJsonAsync<JsonElement>($"{CampaignUrl(f, id)}/recipients?status=Sent", Ct);
        Assert.Equal(
            [("Callada", false, false), ("Leída", true, false), ("Sin confirmación", true, true)],
            sent.GetProperty("items").EnumerateArray().Select(item =>
                (item.GetProperty("customerName").GetString(), item.GetProperty("replied").GetBoolean(), item.GetProperty("readWithoutReceipt").GetBoolean())));
    }

    [Fact]
    public async Task TheAudienceOfADetailOmitsTheTagsAndCustomersThatNoLongerExistAndThePreviewCountsRealCustomers()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var laura = await CreateCustomerAsync(f.Factory, f.Tenant, "Laura Gómez", "300 123 4567");
        using var customers = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, "customers.customer.read", "customers.customer.manage");
        var customersUrl = $"/api/v1/tenants/{f.Tenant.TenantId}/customers";
        var tagResponse = await SendAsync(customers, HttpMethod.Post, $"{customersUrl}/tags", new { name = "Mayoristas", color = "blue" });
        var tagId = (await tagResponse.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        await SendAsync(customers, HttpMethod.Put, $"{customersUrl}/{laura}/tags", new { tagIds = new[] { tagId } });
        var gone = Guid.CreateVersion7();

        var campaign = await CreateAsync(f, Input(f, tagIds: [tagId, gone], customerIds: [laura, Guid.CreateVersion7()]));
        var preview = await SendAsync(f.Client, HttpMethod.Post, $"{CampaignsUrl(f.Tenant.TenantId)}/audience-preview", new { tagIds = new[] { tagId }, customerIds = Array.Empty<Guid>(), templateCategory = "MARKETING" });
        var empty = await SendAsync(f.Client, HttpMethod.Post, $"{CampaignsUrl(f.Tenant.TenantId)}/audience-preview", new { tagIds = Array.Empty<Guid>(), customerIds = Array.Empty<Guid>(), templateCategory = "MARKETING" });

        var audience = campaign.GetProperty("audience");
        Assert.Equal(["Mayoristas"], audience.GetProperty("tags").EnumerateArray().Select(tag => tag.GetProperty("name").GetString()));
        Assert.Equal("blue", audience.GetProperty("tags")[0].GetProperty("color").GetString());
        Assert.Equal(["Laura Gómez"], audience.GetProperty("customers").EnumerateArray().Select(customer => customer.GetProperty("name").GetString()));
        var counts = await preview.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal((1, 1, 0, 0, 0), (counts.GetProperty("total").GetInt32(), counts.GetProperty("reachable").GetInt32(), counts.GetProperty("optedOut").GetInt32(), counts.GetProperty("inactive").GetInt32(), counts.GetProperty("withoutWhatsApp").GetInt32()));
        Assert.Equal(0, (await empty.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task ThePreviewCountsTheNumberOptOut()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        var pair = await CreateCustomersAsync(f.Factory, f.Tenant, ("Laura Gómez", "300 123 4567"), ("Laura Segunda", "300 123 4567"));
        var (a, b) = (pair[0], pair[1]);
        await ExecuteAsync(f.ConnectionString, "UPDATE customers.customers SET marketing_opt_out = true WHERE id = @id", ("id", a));

        var preview = await SendAsync(f.Client, HttpMethod.Post, $"{CampaignsUrl(f.Tenant.TenantId)}/audience-preview", new { tagIds = Array.Empty<Guid>(), customerIds = new[] { a, b }, templateCategory = "MARKETING" });

        var counts = await preview.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal((2, 0, 2), (counts.GetProperty("total").GetInt32(), counts.GetProperty("reachable").GetInt32(), counts.GetProperty("optedOut").GetInt32()));
    }

    private static Dictionary<string, object?> ToDictionary(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!.ToDictionary(pair => pair.Key, pair => (object?)pair.Value);

    private static Task<int> SeedRecipientAsync(
        string connectionString, Guid tenantId, Guid campaignId, string status, string name = "Cliente", string? failureCode = null,
        Guid? conversationId = null, Guid? messageId = null, DateTimeOffset? sentAt = null) =>
        ExecuteAsync(connectionString,
            """
            INSERT INTO messaging.campaign_recipients (id, campaign_id, tenant_id, customer_id, customer_name, wa_id, status, failure_code, conversation_id, message_id, attempts, next_attempt_at, sent_at)
            VALUES (@id, @campaign, @tenant, @customer, @name, '573001234567', @status, @code, @conversation, @message, 0, now(), @sentAt)
            """,
            ("id", Guid.CreateVersion7()), ("campaign", campaignId), ("tenant", tenantId), ("customer", Guid.CreateVersion7()), ("name", name), ("status", status),
            ("code", (object?)failureCode ?? DBNull.Value), ("conversation", (object?)conversationId ?? DBNull.Value), ("message", (object?)messageId ?? DBNull.Value),
            ("sentAt", (object?)(sentAt ?? (status == "Sent" ? DateTimeOffset.UtcNow.AddMinutes(-5) : null))?.ToUniversalTime() ?? DBNull.Value));
}
