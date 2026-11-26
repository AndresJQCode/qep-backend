using System.Security.Claims;
using System.Text.Json;
using Bootstrapper.Realtime;
using BuildingBlocks.Application.Realtime;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Tenancy.Application;

namespace Bootstrapper.UnitTests.Realtime;

/// <summary>Spec 2026-11-22 §9.1 and D-3: a member's own notices go to a group the hub joins from the connection's claims, so the client
/// never sends a member id and no <c>Subscribe</c> can ask for somebody else's group.</summary>
public sealed class AppHubMemberGroupTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Member = Guid.CreateVersion7();

    [Fact]
    public async Task OnConnectedJoinsTheMemberGroup()
    {
        var (hub, groups) = NewHub();

        await hub.OnConnectedAsync();

        Assert.Contains(("conn-1", RealtimeTopics.MemberGroupOf(Tenant, Member)), groups.Added);
        Assert.Equal($"{Tenant:D}:@member:{Member:D}", RealtimeTopics.MemberGroupOf(Tenant, Member));
    }

    [Fact]
    public async Task AMemberEnvelopeGoesToTheMemberGroupOnly()
    {
        var context = new RecordingHubContext();
        var dispatcher = new AppHubDispatcher(context, new AppHubConnections());
        var envelope = new RealtimeEnvelope(
            "notificationsChanged", Tenant, RealtimeTopics.MemberTopic, JsonSerializer.SerializeToElement(new { notificationId = (Guid?)null }), Member);

        await dispatcher.DispatchAsync(envelope, TestContext.Current.CancellationToken);

        var sent = Assert.Single(context.Sent);
        Assert.Equal("group:" + RealtimeTopics.MemberGroupOf(Tenant, Member), sent.ConnectionId);
        Assert.DoesNotContain(context.Sent, item => item.ConnectionId == "group:" + RealtimeTopics.GroupOf(Tenant, RealtimeTopics.MemberTopic));
    }

    [Fact]
    public async Task TheMemberGroupCannotBeSubscribed()
    {
        var (hub, groups) = NewHub();
        await hub.OnConnectedAsync();
        var other = Guid.CreateVersion7();

        var error = await Assert.ThrowsAsync<HubException>(() => hub.Subscribe("@member:" + other.ToString("D")));

        Assert.Equal(RealtimeTopics.ForbiddenCode, error.Message);
        Assert.DoesNotContain(groups.Added, item => item.Group.Contains(other.ToString("D"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheMemberGroupCannotBeUnsubscribed()
    {
        var (hub, groups) = NewHub();
        await hub.OnConnectedAsync();

        // A client that could leave its own group would stop receiving its alerts until the next reconnect.
        await hub.Unsubscribe("@member:" + Member.ToString("D"));
        await hub.Unsubscribe(RealtimeTopics.MemberTopic);

        Assert.Empty(groups.Removed);
    }

    [Fact]
    public async Task AnUnknownTopicIsRefusedWithTheGenericCode()
    {
        var (hub, _) = NewHub();
        await hub.OnConnectedAsync();

        var error = await Assert.ThrowsAsync<HubException>(() => hub.Subscribe("nadie:sabe"));

        Assert.Equal("realtime.topic_forbidden", error.Message);
        Assert.Equal("realtime.topic_forbidden", RealtimeTopics.ForbiddenCode);
    }

    [Fact]
    public void AnEnvelopeWithoutMemberIdStillDeserializes()
    {
        // What a pod from before this change publishes: no memberId at all.
        const string json = """{"kind":"supportTicketChanged","tenantId":"0190a000-0000-7000-8000-000000000001","topic":"support","payload":{}}""";

        var parsed = RealtimeEventJson.TryDeserialize(json);

        Assert.NotNull(parsed);
        Assert.Null(parsed.MemberId);
    }

    private static (AppHub Hub, RecordingHubContext.RecordingGroups Groups) NewHub()
    {
        var connections = new AppHubConnections();
        var context = new RecordingHubContext();
        var hub = new AppHub(
            new FixedMemberships(Member),
            [],
            connections,
            new AppHubDispatcher(context, connections),
            new NoPresence(),
            new LocalAppMemberPresence(connections),
            new FixedMemberNames(),
            new NoPublisher(),
            NullLogger<AppHub>.Instance)
        {
            Context = new TestCallerContext("conn-1", Tenant, Guid.CreateVersion7()),
            Groups = context.AddedGroups,
        };
        return (hub, context.AddedGroups);
    }

    private sealed class FixedMemberships(Guid member) : IMembershipDirectory
    {
        public Task<IReadOnlyCollection<string>?> FindActiveRolesAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<string>?>([]);

        public Task<Guid?> FindActiveMembershipIdAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<Guid?>(member);

        public Task<IReadOnlyList<Guid>> ListMembershipIdsByUserAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>([member]);
    }

    private sealed class NoPresence : IRealtimePresence
    {
        public Task<IReadOnlyList<Guid>> SetComposingAsync(Guid tenantId, string topic, Guid memberId, string connectionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<IReadOnlyList<Guid>> ClearAsync(Guid tenantId, string topic, Guid memberId, string connectionId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<IReadOnlyList<Guid>> ReadAsync(Guid tenantId, string topic, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    private sealed class NoPublisher : IRealtimePublisher
    {
        public Task PublishAsync(Guid tenantId, string topic, string kind, object payload, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class TestCallerContext(string connectionId, Guid tenantId, Guid subject) : HubCallerContext
    {
        public override string ConnectionId => connectionId;

        public override string? UserIdentifier => subject.ToString("D");

        public override ClaimsPrincipal? User { get; } = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("qep_sub", subject.ToString("D")), new Claim("tenant_id", tenantId.ToString("D"))], "test"));

        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort()
        {
        }
    }
}
