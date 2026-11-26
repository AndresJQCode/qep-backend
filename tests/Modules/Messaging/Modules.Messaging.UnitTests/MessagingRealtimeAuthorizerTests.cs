using Modules.Messaging.Api.Realtime;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-11-22 §9.2: who may subscribe to the <c>messaging</c> topic of the app hub. The same rule as the endpoint of
/// <c>MessagingHub</c> (module on, <c>messaging.conversation.read</c>), read from the database for the connection's member.</summary>
public sealed class MessagingRealtimeAuthorizerTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Member = Guid.CreateVersion7();

    private static MessagingRealtimeAuthorizer NewAuthorizer(bool canRead = true, bool moduleOn = true) =>
        new(
            new FakeModules(TenantModuleSet.FromStored(TenantModuleKeys.All.Where(key => moduleOn || key != TenantModuleKeys.Messaging))),
            new FakePermissions(canRead));

    [Theory]
    [InlineData("messaging", true)]
    [InlineData("messaging:", false)]
    [InlineData("messaging:conversation:1", false)]
    [InlineData("Messaging", false)]
    [InlineData("messaging ", false)]
    [InlineData("", false)]
    public async Task OnlyTheExactMessagingTopicIsAccepted(string topic, bool accepted)
    {
        var authorizer = NewAuthorizer();

        Assert.Equal(accepted, await authorizer.CanSubscribeAsync(Tenant, Member, topic, TestContext.Current.CancellationToken));
        Assert.Equal("messaging", authorizer.Prefix);
        Assert.Equal("messaging.realtime.topic_forbidden", authorizer.ForbiddenCode);
        Assert.Equal(MessagingErrorCodes.RealtimeTopicForbidden, authorizer.ForbiddenCode);
    }

    [Fact]
    public async Task WithoutConversationReadItIsRefused() =>
        Assert.False(await NewAuthorizer(canRead: false).CanSubscribeAsync(Tenant, Member, "messaging", TestContext.Current.CancellationToken));

    [Fact]
    public async Task WithTheModuleOffItIsRefused() =>
        Assert.False(await NewAuthorizer(moduleOn: false).CanSubscribeAsync(Tenant, Member, "messaging", TestContext.Current.CancellationToken));

    [Fact]
    public async Task PresenceIsAlwaysRefused()
    {
        var authorizer = NewAuthorizer();

        Assert.False(await authorizer.CanSetPresenceAsync(Tenant, Member, "messaging", TestContext.Current.CancellationToken));
        Assert.False(await authorizer.CanSetPresenceAsync(Tenant, Member, "messaging:conversation:1", TestContext.Current.CancellationToken));
    }

    private sealed class FakeModules(TenantModuleSet set) : ITenantModules
    {
        public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult<TenantModuleSet?>(set);
    }

    private sealed class FakePermissions(bool canRead) : IMessagingMemberPermissions
    {
        public Task<bool> HasAnyAsync(
            Guid tenantId, Guid memberId, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken) =>
            Task.FromResult(canRead && permissions.Contains(MessagingPermissions.ConversationRead));
    }
}
