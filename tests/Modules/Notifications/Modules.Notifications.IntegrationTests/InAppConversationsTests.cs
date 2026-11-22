using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;
using Modules.Notifications.Domain;
using static Modules.Notifications.IntegrationTests.InAppTestSupport;
using static Modules.Notifications.IntegrationTests.NotificationsDeliveryHarness;

namespace Modules.Notifications.IntegrationTests;

/// <summary>Spec 2026-11-22 §6, §8.5 and §12: a conversation handed to a member reaches that member's bell, and the entry names the
/// contact when it is read.</summary>
public sealed class InAppConversationsTests
{
    private const string Consumer = "notifications.in-app.conversations";
    private const string AssignedEvent = "messaging.conversation.assigned.v1";

    [Fact]
    public async Task AnAssignedEventCreatesAConversationEntryWithReason()
    {
        await using var database = await StartDatabaseAsync();
        var publisher = new RecordingMemberPublisher();
        using var factory = NewFactory(database.GetConnectionString(), publisher);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var actor = await SeedMemberAsync(database.GetConnectionString(), tenantId, "Ana Pérez");
        var conversationId = Guid.CreateVersion7();
        var transferred = await InsertOutboxAsync(
            database.GetConnectionString(),
            AssignedEvent,
            JsonSerializer.Serialize(new
            {
                tenantId,
                conversationId,
                memberId = owner,
                reason = "Transferred",
                actorMemberId = actor.MemberId,
                occurredAt = DateTimeOffset.UtcNow,
            }),
            DateTimeOffset.UtcNow.AddSeconds(-2));
        // A heritage has no actor: the payload carries it as null.
        var inherited = await InsertOutboxAsync(
            database.GetConnectionString(),
            AssignedEvent,
            JsonSerializer.Serialize(new
            {
                tenantId,
                conversationId = Guid.CreateVersion7(),
                memberId = owner,
                reason = "Inherited",
                actorMemberId = (Guid?)null,
                occurredAt = DateTimeOffset.UtcNow,
            }),
            DateTimeOffset.UtcNow.AddSeconds(-1));

        await DrainAsync(factory, Consumer);

        var entries = await EntriesAsync(database.GetConnectionString(), tenantId);
        Assert.Equal(2, entries.Count);
        var first = entries.Single(entry => entry.SourceMessageId == transferred);
        Assert.Equal(nameof(InAppNotificationKind.ConversationAssigned), first.Kind);
        Assert.Equal(owner, first.MemberId);
        Assert.Equal(conversationId, first.ResourceId);
        Assert.Equal(transferred.ToString("D"), first.DedupeKey);
        Assert.Equal(conversationId, first.Data.GetProperty("conversationId").GetGuid());
        Assert.Equal("Transferred", first.Data.GetProperty("reason").GetString());
        Assert.Equal(actor.MemberId, first.Data.GetProperty("actorMemberId").GetGuid());
        var second = entries.Single(entry => entry.SourceMessageId == inherited);
        Assert.Equal("Inherited", second.Data.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, second.Data.GetProperty("actorMemberId").ValueKind);
        Assert.Equal(2, publisher.Published.Count(notice => notice.MemberId == owner && notice.Kind == "notificationsChanged"));
        Assert.NotNull((await FindInboxAsync(database.GetConnectionString(), Consumer, transferred))!.ProcessedAt);
    }

    [Fact]
    public async Task TheEntryResolvesTheContactName()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var named = await SeedConversationAsync(factory, tenantId, "573001234567", "Laura Pérez");
        var unnamed = await SeedConversationAsync(factory, tenantId, "573001234568", profileName: null);
        foreach (var conversationId in new[] { named, unnamed })
        {
            await InsertOutboxAsync(
                database.GetConnectionString(),
                AssignedEvent,
                JsonSerializer.Serialize(new
                {
                    tenantId,
                    conversationId,
                    memberId = owner,
                    reason = "Inherited",
                    actorMemberId = (Guid?)null,
                    occurredAt = DateTimeOffset.UtcNow,
                }));
        }

        await DrainAsync(factory, Consumer);
        using var client = ClientFor(factory, tenantId, ownerUserId);
        var page = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/tenants/{tenantId}/notifications", TestContext.Current.CancellationToken);

        var names = page.GetProperty("items").EnumerateArray().ToDictionary(
            item => item.GetProperty("data").GetProperty("conversationId").GetGuid(),
            item => item.GetProperty("data").GetProperty("contactName"));
        Assert.Equal("Laura Pérez", names[named].GetString());
        // A contact without a name is null, never an empty string: the screen says «un contacto».
        Assert.Equal(JsonValueKind.Null, names[unnamed].ValueKind);
    }

    // The conversation is read with the tenant of the entry: the name of another tenant's conversation is never served.
    [Fact]
    public async Task AConversationOfAnotherTenantHasNoName()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var foreign = await SeedConversationAsync(factory, Guid.CreateVersion7(), "573001234567", "Laura Pérez");
        await InsertOutboxAsync(
            database.GetConnectionString(),
            AssignedEvent,
            JsonSerializer.Serialize(new
            {
                tenantId,
                conversationId = foreign,
                memberId = owner,
                reason = "Handoff",
                actorMemberId = (Guid?)null,
                occurredAt = DateTimeOffset.UtcNow,
            }));

        await DrainAsync(factory, Consumer);
        using var client = ClientFor(factory, tenantId, ownerUserId);
        var page = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/tenants/{tenantId}/notifications", TestContext.Current.CancellationToken);

        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("data").GetProperty("contactName").ValueKind);
    }

    /// <summary>A conversation written by the Messaging context of the host, as the ingestion would leave it. The connection is not
    /// checked: a conversation has no foreign key to another module's connection.</summary>
    private static async Task<Guid> SeedConversationAsync(
        NotificationsApiFactory factory, Guid tenantId, string waId, string? profileName)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var conversation = Conversation.Start(Guid.CreateVersion7(), tenantId, Guid.CreateVersion7(), waId, profileName, DateTimeOffset.UtcNow);
        dbContext.Conversations.Add(conversation);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return conversation.Id;
    }
}
