using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Notifications.IntegrationTests.InAppTestSupport;
using static Modules.Notifications.IntegrationTests.NotificationsDeliveryHarness;

namespace Modules.Notifications.IntegrationTests;

/// <summary>Spec 2026-11-22 §8: the four routes of the bell. Each caller reads and writes only their own entries.</summary>
public sealed class InAppNotificationEndpointsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] ExportReadyKeys = ["downloadUrl", "expiresAt", "exportKind", "fileName", "rowCount"];

    [Fact]
    public async Task ListReturnsOnlyTheCallersEntriesNewestFirst()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var other = await SeedMemberAsync(database.GetConnectionString(), tenantId, "Otra Persona");
        var now = DateTimeOffset.UtcNow;
        var oldest = await InsertEntryAsync(database.GetConnectionString(), tenantId, owner, "ExportFailed", """{"exportKind":"Orders"}""", now.AddMinutes(-30));
        var newest = await InsertEntryAsync(database.GetConnectionString(), tenantId, owner, "ExportFailed", """{"exportKind":"Quotations"}""", now.AddMinutes(-10));
        var middle = await InsertEntryAsync(database.GetConnectionString(), tenantId, owner, "ExportFailed", """{"exportKind":"Orders"}""", now.AddMinutes(-20));
        await InsertEntryAsync(database.GetConnectionString(), tenantId, other.MemberId, "ExportFailed", """{"exportKind":"Orders"}""", now);
        using var client = ClientFor(factory, tenantId, ownerUserId);

        var page = await GetJsonAsync(client, $"/api/v1/tenants/{tenantId}/notifications");

        Assert.Equal([newest, middle, oldest], page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
        Assert.Equal(3, page.GetProperty("unreadCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
        var first = page.GetProperty("items")[0];
        Assert.Equal("ExportFailed", first.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("readAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, first.GetProperty("resolvedAt").ValueKind);
    }

    [Fact]
    public async Task TheCursorPagesWithoutRepeating()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var now = DateTimeOffset.UtcNow;
        var expected = new List<Guid>();
        // Two entries share the same instant: the id breaks the tie, so the page boundary cannot repeat or skip one.
        foreach (var minutes in new[] { 5, 4, 4, 3, 2 })
        {
            expected.Add(await InsertEntryAsync(
                database.GetConnectionString(), tenantId, owner, "ExportFailed", """{"exportKind":"Orders"}""", now.AddMinutes(-minutes).AddTicks(0)));
        }

        using var client = ClientFor(factory, tenantId, ownerUserId);
        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = $"/api/v1/tenants/{tenantId}/notifications?limit=2" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await GetJsonAsync(client, url);
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
            cursor = page.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(5, seen.Distinct().Count());
        Assert.Equal(expected.OrderBy(id => id).ToList(), seen.OrderBy(id => id).ToList());
    }

    [Fact]
    public async Task UnreadOnlyFilters()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var now = DateTimeOffset.UtcNow;
        var unread = await InsertEntryAsync(database.GetConnectionString(), tenantId, owner, "ExportFailed", """{"exportKind":"Orders"}""", now.AddMinutes(-2));
        await InsertEntryAsync(database.GetConnectionString(), tenantId, owner, "ExportFailed", """{"exportKind":"Orders"}""", now.AddMinutes(-1), readAt: now);
        using var client = ClientFor(factory, tenantId, ownerUserId);

        var page = await GetJsonAsync(client, $"/api/v1/tenants/{tenantId}/notifications?unreadOnly=true");
        var count = await GetJsonAsync(client, $"/api/v1/tenants/{tenantId}/notifications/unread-count");

        Assert.Equal([unread], page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
        Assert.Equal(1, page.GetProperty("unreadCount").GetInt32());
        Assert.Equal(1, count.GetProperty("count").GetInt32());
    }

    [Theory]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=51", "limit")]
    [InlineData("cursor=no-es-un-cursor", "cursor")]
    public async Task AMalformedQueryIs422OnTheField(string query, string field)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        using var client = ClientFor(factory, tenantId, ownerUserId);

        var response = await client.GetAsync($"/api/v1/tenants/{tenantId}/notifications?{query}", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task AnotherTenantInTheRouteIs403()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        using var client = ClientFor(factory, tenantId, ownerUserId);
        var foreign = Guid.CreateVersion7();

        var list = await client.GetAsync($"/api/v1/tenants/{foreign}/notifications", Ct);
        var count = await client.GetAsync($"/api/v1/tenants/{foreign}/notifications/unread-count", Ct);
        var read = await client.PostAsync($"/api/v1/tenants/{foreign}/notifications/{Guid.CreateVersion7()}/read", null, Ct);
        var readAll = await client.PostAsync($"/api/v1/tenants/{foreign}/notifications/read-all", null, Ct);

        Assert.All([list, count, read, readAll], response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
        var problem = await list.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("authorization.denied", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AnotherMembersEntryIs404OnRead()
    {
        await using var database = await StartDatabaseAsync();
        var publisher = new RecordingMemberPublisher();
        using var factory = NewFactory(database.GetConnectionString(), publisher);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var other = await SeedMemberAsync(database.GetConnectionString(), tenantId, "Otra Persona");
        var theirs = await InsertEntryAsync(database.GetConnectionString(), tenantId, other.MemberId, "ExportFailed", """{"exportKind":"Orders"}""", DateTimeOffset.UtcNow);
        using var client = ClientFor(factory, tenantId, ownerUserId);

        var foreign = await client.PostAsync($"/api/v1/tenants/{tenantId}/notifications/{theirs}/read", null, Ct);
        var missing = await client.PostAsync($"/api/v1/tenants/{tenantId}/notifications/{Guid.CreateVersion7()}/read", null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var problem = await foreign.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("notifications.notification.not_found", problem.GetProperty("code").GetString());
        // Nothing of the other member changed, and nobody was told.
        Assert.Null((await EntriesAsync(database.GetConnectionString(), tenantId)).Single().ReadAt);
        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task MarkReadIsIdempotentAndReturnsTheUnreadCount()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var now = DateTimeOffset.UtcNow;
        var first = await InsertEntryAsync(database.GetConnectionString(), tenantId, owner, "ExportFailed", """{"exportKind":"Orders"}""", now.AddMinutes(-2));
        await InsertEntryAsync(database.GetConnectionString(), tenantId, owner, "ExportFailed", """{"exportKind":"Orders"}""", now.AddMinutes(-1));
        using var client = ClientFor(factory, tenantId, ownerUserId);

        var once = await PostJsonAsync(client, $"/api/v1/tenants/{tenantId}/notifications/{first}/read");
        var twice = await PostJsonAsync(client, $"/api/v1/tenants/{tenantId}/notifications/{first}/read");

        Assert.Equal(1, once.GetProperty("unreadCount").GetInt32());
        Assert.Equal(1, twice.GetProperty("unreadCount").GetInt32());
        Assert.Equal(first, once.GetProperty("notification").GetProperty("id").GetGuid());
        var readAt = once.GetProperty("notification").GetProperty("readAt").GetDateTimeOffset();
        Assert.Equal(readAt, twice.GetProperty("notification").GetProperty("readAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task ReadAllRespectsUpTo()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddMinutes(-10);
        await InsertEntryAsync(database.GetConnectionString(), tenantId, owner, "ExportFailed", """{"exportKind":"Orders"}""", now.AddMinutes(-20));
        await InsertEntryAsync(database.GetConnectionString(), tenantId, owner, "ExportFailed", """{"exportKind":"Orders"}""", cutoff.AddMinutes(-1));
        var later = await InsertEntryAsync(database.GetConnectionString(), tenantId, owner, "ExportFailed", """{"exportKind":"Orders"}""", cutoff.AddMinutes(1));
        using var client = ClientFor(factory, tenantId, ownerUserId);

        var result = await PostJsonAsync(client, $"/api/v1/tenants/{tenantId}/notifications/read-all", new { upTo = cutoff.ToString("O") });

        Assert.Equal(1, result.GetProperty("unreadCount").GetInt32());
        var rows = await EntriesAsync(database.GetConnectionString(), tenantId);
        Assert.Null(rows.Single(row => row.Id == later).ReadAt);
        Assert.Equal(2, rows.Count(row => row.ReadAt is not null));
    }

    // Spec §8.4: the screen sends back the createdAt text it received, and the entry it came from must be included. A reformatting or a
    // precision loss on the way would leave that very entry unread.
    [Fact]
    public async Task ReadAllWithTheCreatedAtAsReceivedIncludesThatEntry()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        await InsertEntryAsync(
            database.GetConnectionString(), tenantId, owner, "ExportFailed", """{"exportKind":"Orders"}""", DateTimeOffset.UtcNow.AddMinutes(-1));
        using var client = ClientFor(factory, tenantId, ownerUserId);
        var page = await GetJsonAsync(client, $"/api/v1/tenants/{tenantId}/notifications");
        var createdAtText = page.GetProperty("items")[0].GetProperty("createdAt").GetString();

        var result = await PostJsonAsync(client, $"/api/v1/tenants/{tenantId}/notifications/read-all", new { upTo = createdAtText });

        Assert.Equal(0, result.GetProperty("unreadCount").GetInt32());
    }

    [Fact]
    public async Task ReadAllWithoutABodyMarksEverything()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        await InsertEntryAsync(database.GetConnectionString(), tenantId, owner, "ExportFailed", """{"exportKind":"Orders"}""", DateTimeOffset.UtcNow);
        using var client = ClientFor(factory, tenantId, ownerUserId);

        var result = await PostJsonAsync(client, $"/api/v1/tenants/{tenantId}/notifications/read-all");

        Assert.Equal(0, result.GetProperty("unreadCount").GetInt32());
    }

    [Fact]
    public async Task AMalformedUpToIs422OnUpTo()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        using var client = ClientFor(factory, tenantId, ownerUserId);

        var response = await client.PostAsJsonAsync($"/api/v1/tenants/{tenantId}/notifications/read-all", new { upTo = "mañana" }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("upTo", out _));
    }

    [Fact]
    public async Task AnExpiredExportHasNoDownloadUrl()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var now = DateTimeOffset.UtcNow;
        var live = await InsertEntryAsync(database.GetConnectionString(), tenantId, owner, "ExportReady", ExportReadyData(now.AddHours(2)), now.AddMinutes(-1));
        var expired = await InsertEntryAsync(database.GetConnectionString(), tenantId, owner, "ExportReady", ExportReadyData(now.AddHours(-1)), now.AddMinutes(-5));
        using var client = ClientFor(factory, tenantId, ownerUserId);

        var page = await GetJsonAsync(client, $"/api/v1/tenants/{tenantId}/notifications");

        var items = page.GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("id").GetGuid(), item => item.GetProperty("data"));
        Assert.Equal("https://r2.test/x.xlsx", items[live].GetProperty("downloadUrl").GetString());
        Assert.Equal(JsonValueKind.Null, items[expired].GetProperty("downloadUrl").ValueKind);
        // Every key of the kind is always there, with null explicit, never omitted.
        Assert.Equal(ExportReadyKeys, items[expired].EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ConversationAssignedResolvesTheActorName()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var actor = await SeedMemberAsync(database.GetConnectionString(), tenantId, "Ana Pérez");
        var conversation = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var transferred = await InsertEntryAsync(
            database.GetConnectionString(), tenantId, owner, "ConversationAssigned",
            $$"""{"conversationId":"{{conversation}}","reason":"Transferred","actorMemberId":"{{actor.MemberId}}"}""",
            now.AddMinutes(-1),
            resourceId: conversation);
        var inherited = await InsertEntryAsync(
            database.GetConnectionString(), tenantId, owner, "ConversationAssigned",
            $$"""{"conversationId":"{{conversation}}","reason":"Inherited","actorMemberId":"{{actor.MemberId}}"}""",
            now.AddMinutes(-2),
            resourceId: conversation);
        using var client = ClientFor(factory, tenantId, ownerUserId);

        var page = await GetJsonAsync(client, $"/api/v1/tenants/{tenantId}/notifications");

        var items = page.GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("id").GetGuid(), item => item.GetProperty("data"));
        Assert.Equal("Ana Pérez", items[transferred].GetProperty("actorName").GetString());
        Assert.Equal("Transferred", items[transferred].GetProperty("reason").GetString());
        Assert.Equal(conversation, items[transferred].GetProperty("conversationId").GetGuid());
        // The system did it (a heritage or a handoff): no actor is named, even when the row remembers one.
        Assert.Equal(JsonValueKind.Null, items[inherited].GetProperty("actorName").ValueKind);
        // The conversation does not exist, so the contact has no name (InAppConversationsTests covers the one that does).
        Assert.Equal(JsonValueKind.Null, items[transferred].GetProperty("contactName").ValueKind);
    }

    [Fact]
    public async Task WritesPublishNotificationsChangedToTheMember()
    {
        await using var database = await StartDatabaseAsync();
        var publisher = new RecordingMemberPublisher();
        using var factory = NewFactory(database.GetConnectionString(), publisher);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var entry = await InsertEntryAsync(database.GetConnectionString(), tenantId, owner, "ExportFailed", """{"exportKind":"Orders"}""", DateTimeOffset.UtcNow);
        using var client = ClientFor(factory, tenantId, ownerUserId);

        await PostJsonAsync(client, $"/api/v1/tenants/{tenantId}/notifications/{entry}/read");
        await PostJsonAsync(client, $"/api/v1/tenants/{tenantId}/notifications/read-all");

        Assert.Equal(2, publisher.Published.Count);
        Assert.All(publisher.Published, notice => Assert.Equal((tenantId, owner, "notificationsChanged"), (notice.TenantId, notice.MemberId, notice.Kind)));
        Assert.Equal(entry, publisher.Published[0].Payload.GetProperty("notificationId").GetGuid());
        Assert.Equal(JsonValueKind.Null, publisher.Published[1].Payload.GetProperty("notificationId").ValueKind);
    }

    private static string ExportReadyData(DateTimeOffset expiresAt) =>
        JsonSerializer.Serialize(new
        {
            exportKind = "Products",
            fileName = "x.xlsx",
            rowCount = 3,
            expiresAt = expiresAt.ToString("O"),
            downloadUrl = "https://r2.test/x.xlsx",
        });

    [Fact]
    public async Task ReadByResourceMarksOnlyTheCallersEntriesOfThatTicket()
    {
        await using var database = await StartDatabaseAsync();
        var publisher = new RecordingMemberPublisher();
        using var factory = NewFactory(database.GetConnectionString(), publisher);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var other = await SeedMemberAsync(database.GetConnectionString(), tenantId, "Otra Persona");
        var ticket = Guid.CreateVersion7();
        var otherTicket = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        const string data = """{"ticketNumber":"SOP-000127","resolution":null}""";
        var first = await InsertEntryAsync(database.GetConnectionString(), tenantId, owner, "SupportTaskAssigned", data, now.AddMinutes(-3), resourceId: ticket);
        var second = await InsertEntryAsync(database.GetConnectionString(), tenantId, owner, "SupportTaskAssigned", data, now.AddMinutes(-2), resourceId: ticket);
        var elsewhere = await InsertEntryAsync(database.GetConnectionString(), tenantId, owner, "SupportTaskAssigned", data, now.AddMinutes(-1), resourceId: otherTicket);
        var theirs = await InsertEntryAsync(database.GetConnectionString(), tenantId, other.MemberId, "SupportTaskAssigned", data, now, resourceId: ticket);
        using var client = ClientFor(factory, tenantId, ownerUserId);

        var result = await PostJsonAsync(
            client, $"/api/v1/tenants/{tenantId}/notifications/read-by-resource", new { kind = "SupportTaskAssigned", resourceId = ticket });

        Assert.Equal(2, result.GetProperty("marked").GetInt32());
        Assert.Equal(1, result.GetProperty("unreadCount").GetInt32());
        var rows = await EntriesAsync(database.GetConnectionString(), tenantId);
        Assert.NotNull(rows.Single(row => row.Id == first).ReadAt);
        Assert.NotNull(rows.Single(row => row.Id == second).ReadAt);
        Assert.Null(rows.Single(row => row.Id == elsewhere).ReadAt);
        Assert.Null(rows.Single(row => row.Id == theirs).ReadAt);
        var notice = Assert.Single(publisher.Published);
        Assert.Equal((tenantId, owner, "notificationsChanged"), (notice.TenantId, notice.MemberId, notice.Kind));
        Assert.Equal(JsonValueKind.Null, notice.Payload.GetProperty("notificationId").ValueKind);
    }

    [Fact]
    public async Task ReadByResourceTwiceMarksNothingTheSecondTime()
    {
        await using var database = await StartDatabaseAsync();
        var publisher = new RecordingMemberPublisher();
        using var factory = NewFactory(database.GetConnectionString(), publisher);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var ticket = Guid.CreateVersion7();
        await InsertEntryAsync(
            database.GetConnectionString(), tenantId, owner, "SupportTicketAssigned", """{"ticketNumber":"SOP-000127"}""", DateTimeOffset.UtcNow, resourceId: ticket);
        using var client = ClientFor(factory, tenantId, ownerUserId);
        var url = $"/api/v1/tenants/{tenantId}/notifications/read-by-resource";
        var body = new { kind = "SupportTicketAssigned", resourceId = ticket };

        var first = await PostJsonAsync(client, url, body);
        var second = await PostJsonAsync(client, url, body);

        Assert.Equal(1, first.GetProperty("marked").GetInt32());
        Assert.Equal(0, second.GetProperty("marked").GetInt32());
        Assert.Equal(0, second.GetProperty("unreadCount").GetInt32());
        Assert.Single(publisher.Published);
    }

    [Fact]
    public async Task ReadByResourceForAnotherTenantIs403()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        using var client = ClientFor(factory, tenantId, ownerUserId);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{Guid.CreateVersion7()}/notifications/read-by-resource",
            new { kind = "SupportTaskAssigned", resourceId = Guid.CreateVersion7() },
            Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ReadByResourceWithABadBodyIs422()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        using var client = ClientFor(factory, tenantId, ownerUserId);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/notifications/read-by-resource", new { kind = "nope", resourceId = Guid.Empty }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("kind", out _));
        Assert.True(problem.GetProperty("errors").TryGetProperty("resourceId", out _));
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string url, object? body = null)
    {
        var response = body is null ? await client.PostAsync(url, null, Ct) : await client.PostAsJsonAsync(url, body, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }
}
