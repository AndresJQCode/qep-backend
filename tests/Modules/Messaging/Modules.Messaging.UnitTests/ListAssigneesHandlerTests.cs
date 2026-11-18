using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>The SPA has no membership id of its own (D-F1): the transfer list says which item is the caller, so the dialog
/// can leave them out without guessing.</summary>
public sealed class ListAssigneesHandlerTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    [Fact]
    public async Task MarksTheCallerAsMe()
    {
        var user = Guid.CreateVersion7();
        var ana = Guid.CreateVersion7();
        var luis = Guid.CreateVersion7();
        var membership = new FakeMembershipDirectory();
        membership.Active[(user, Tenant)] = ana;
        var handler = Handler(user, membership, new AssigneeDto(ana, "Ana"), new AssigneeDto(luis, "Luis"));

        var result = await handler.HandleAsync(new ListAssigneesQuery(Tenant), TestContext.Current.CancellationToken);

        Assert.Equal([new AssigneeItemDto(ana, "Ana", IsMe: true), new AssigneeItemDto(luis, "Luis", IsMe: false)], result.Items);
    }

    [Fact]
    public async Task NoItemIsMeWhenTheCallerHasNoActiveMembership()
    {
        var ana = Guid.CreateVersion7();
        var handler = Handler(Guid.CreateVersion7(), new FakeMembershipDirectory(), new AssigneeDto(ana, "Ana"));

        var result = await handler.HandleAsync(new ListAssigneesQuery(Tenant), TestContext.Current.CancellationToken);

        Assert.Equal([new AssigneeItemDto(ana, "Ana", IsMe: false)], result.Items);
    }

    private static ListAssigneesHandler Handler(Guid user, FakeMembershipDirectory membership, params AssigneeDto[] items)
    {
        var context = new FakeExecutionContext(Tenant, user, MessagingPermissions.ConversationManage);
        return new ListAssigneesHandler(new StubAssignees(items), new FakeTenantModules(), context, new CallerMembership(membership, context));
    }

    private sealed class StubAssignees(IReadOnlyList<AssigneeDto> items) : IMessagingAssignees
    {
        public Task<bool> CanReplyAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken) =>
            Task.FromResult(items.Any(item => item.MemberId == memberId));

        public Task<IReadOnlyList<AssigneeDto>> ListAsync(Guid tenantId, CancellationToken cancellationToken) => Task.FromResult(items);
    }
}
