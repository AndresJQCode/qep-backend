using Bootstrapper.Realtime;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests.Realtime;

/// <summary>Spec 2026-11-22 §9.2 and D-9: the alert of an inbound message reaches the assigned member's personal group (only if the module is
/// on and the member can read conversations), or the <c>messaging</c> topic when nobody has the conversation. Ids only, never the text.</summary>
public sealed class AppHubMessagingAlertSinkTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Member = Guid.CreateVersion7();
    private static readonly Guid Conversation = Guid.CreateVersion7();
    private static readonly Guid Message = Guid.CreateVersion7();

    private static (AppHubMessagingAlertSink Sink, RecordingHubContext Hub) NewSink(
        bool canRead = true, TenantModuleSet? modules = null, bool tenantExists = true)
    {
        var hub = new RecordingHubContext();
        var provider = new ServiceCollection()
            .AddSingleton<ITenantModules>(new FakeModules(tenantExists ? modules ?? TenantModuleSet.FromStored(TenantModuleKeys.All) : null))
            .AddSingleton<IMessagingMemberPermissions>(new FakePermissions(canRead))
            .BuildServiceProvider();
        return (new AppHubMessagingAlertSink(hub, provider.GetRequiredService<IServiceScopeFactory>(), new MemoryCache(new MemoryCacheOptions())), hub);
    }

    private static MessagingEvent Inbound(Guid? assigned) =>
        new(MessagingEventKinds.InboundMessage, Tenant, Conversation, Message, assigned);

    [Fact]
    public async Task AnAssignedInboundAlertsOnlyThatMember()
    {
        var (sink, hub) = NewSink();

        await sink.DispatchAsync(Inbound(Member), TestContext.Current.CancellationToken);

        var sent = Assert.Single(hub.Sent);
        Assert.Equal("group:" + RealtimeTopics.MemberGroupOf(Tenant, Member), sent.ConnectionId);
        Assert.Equal("inboundMessageAlert", sent.Method);
        Assert.Equal(MessagingRealtimeEvents.InboundMessageAlert, sent.Method);
    }

    [Fact]
    public async Task AnAssignedMemberWithoutConversationReadGetsNoAlert()
    {
        var (sink, hub) = NewSink(canRead: false);

        await sink.DispatchAsync(Inbound(Member), TestContext.Current.CancellationToken);

        Assert.Empty(hub.Sent);
    }

    [Fact]
    public async Task WithTheMessagingModuleOffTheAssignedMemberGetsNoAlert()
    {
        var (sink, hub) = NewSink(modules: TenantModuleSet.FromStored(TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Messaging)));

        await sink.DispatchAsync(Inbound(Member), TestContext.Current.CancellationToken);

        Assert.Empty(hub.Sent);
    }

    // The development stub has no tenant row: the same rule as TenantModuleGuard, which never denies a tenant it cannot find.
    [Fact]
    public async Task ATenantWithoutARowIsNotDeniedByModule()
    {
        var (sink, hub) = NewSink(tenantExists: false);

        await sink.DispatchAsync(Inbound(Member), TestContext.Current.CancellationToken);

        Assert.Single(hub.Sent);
    }

    [Fact]
    public async Task AnUnassignedInboundAlertsTheMessagingTopic()
    {
        var (sink, hub) = NewSink();

        await sink.DispatchAsync(Inbound(null), TestContext.Current.CancellationToken);

        var sent = Assert.Single(hub.Sent);
        Assert.Equal("group:" + RealtimeTopics.GroupOf(Tenant, "messaging"), sent.ConnectionId);
        Assert.Equal("inboundMessageAlert", sent.Method);
    }

    [Fact]
    public async Task AConversationChangedSendsNoAlert()
    {
        var (sink, hub) = NewSink();

        await sink.DispatchAsync(
            new MessagingEvent(MessagingEventKinds.ConversationChanged, Tenant, Conversation, null, Member), TestContext.Current.CancellationToken);

        Assert.Empty(hub.Sent);
    }

    [Fact]
    public async Task TheAlertCarriesIdsOnly()
    {
        var (sink, hub) = NewSink();

        await sink.DispatchAsync(Inbound(Member), TestContext.Current.CancellationToken);

        var payload = Assert.Single(hub.Sent).Payload;
        Assert.Equal(["conversationId", "messageId"], payload.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(Conversation, payload.GetProperty("conversationId").GetGuid());
        Assert.Equal(Message, payload.GetProperty("messageId").GetGuid());
    }

    // Every inbound message of an assigned conversation asks the same question on every pod: the second one is answered from the cache.
    [Fact]
    public async Task TheDecisionForAMemberIsCachedBetweenMessages()
    {
        var hub = new RecordingHubContext();
        var permissions = new CountingPermissions();
        var provider = new ServiceCollection()
            .AddSingleton<ITenantModules>(new FakeModules(TenantModuleSet.FromStored(TenantModuleKeys.All)))
            .AddSingleton<IMessagingMemberPermissions>(permissions)
            .BuildServiceProvider();
        var sink = new AppHubMessagingAlertSink(
            hub, provider.GetRequiredService<IServiceScopeFactory>(), new MemoryCache(new MemoryCacheOptions()));

        await sink.DispatchAsync(Inbound(Member), TestContext.Current.CancellationToken);
        await sink.DispatchAsync(Inbound(Member), TestContext.Current.CancellationToken);

        Assert.Equal(2, hub.Sent.Count);
        Assert.Equal(1, permissions.Calls);
    }

    private sealed class CountingPermissions : IMessagingMemberPermissions
    {
        public int Calls { get; private set; }

        public Task<bool> HasAnyAsync(
            Guid tenantId, Guid memberId, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeModules(TenantModuleSet? set) : ITenantModules
    {
        public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(set);
    }

    private sealed class FakePermissions(bool canRead) : IMessagingMemberPermissions
    {
        public Task<bool> HasAnyAsync(
            Guid tenantId, Guid memberId, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken) =>
            Task.FromResult(canRead && permissions.Contains(MessagingPermissions.ConversationRead));
    }
}
