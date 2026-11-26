using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Support.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using static Modules.Support.IntegrationTests.AppHubHarness;
using static Modules.Support.IntegrationTests.SupportApiHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Spec 2026-11-22 §5.6 (Block 2, Task 7): the app hub applies the same access table as HTTP (<see cref="SupportTicketAccessPolicy"/>),
/// resolved from the database for the connection's member. Members have the <c>cashier</c> role, which already grants
/// <c>support.task.use</c> and nothing else of Support. One host for the class, with the thread follower off; each test seeds its tenant.
/// </summary>
public sealed class AppHubTaskTopicsTests(SupportHostWithoutWorkers host) : IClassFixture<SupportHostWithoutWorkers>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void AssertForbidden(HubException error) =>
        Assert.Contains(SupportErrorCodes.RealtimeTopicForbidden, error.Message, StringComparison.Ordinal);

    [Fact]
    public async Task ATaskOnlyMemberCannotSubscribeToTheInbox()
    {
        var (_, _, connection, _) = await ConnectMemberAsync(host.Factory, role: "cashier");
        await using var _ = connection;

        var error = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Subscribe", SupportRealtimeTopics.Inbox, Ct));

        AssertForbidden(error);
    }

    [Fact]
    public async Task AMemberSubscribesToHerOwnTasksTopic()
    {
        var (_, member, connection, _) = await ConnectMemberAsync(host.Factory, role: "cashier");
        await using var _ = connection;

        await connection.InvokeAsync("Subscribe", SupportRealtimeTopics.Tasks(member), Ct);
    }

    [Fact]
    public async Task NobodySubscribesToAnotherMembersTasksTopic()
    {
        var (tenant, cashier, cashierConnection, _) = await ConnectMemberAsync(host.Factory, role: "cashier");
        await using var _ = cashierConnection;
        var (_, advisor, advisorConnection, _) = await ConnectMemberAsync(host.Factory, role: "advisor", tenant: tenant);
        await using var __ = advisorConnection;

        // Not even someone who manages every ticket listens to another member's tasks.
        var byAdvisor = await Assert.ThrowsAsync<HubException>(
            () => advisorConnection.InvokeAsync("Subscribe", SupportRealtimeTopics.Tasks(cashier), Ct));
        var byCashier = await Assert.ThrowsAsync<HubException>(
            () => cashierConnection.InvokeAsync("Subscribe", SupportRealtimeTopics.Tasks(advisor), Ct));

        AssertForbidden(byAdvisor);
        AssertForbidden(byCashier);
    }

    [Fact]
    public async Task ATaskOnlyMemberSubscribesToHerTaskButNotToAnExternalTicket()
    {
        var (tenant, cashier, connection, _) = await ConnectMemberAsync(host.Factory, role: "cashier");
        await using var _ = connection;
        var (_, advisor, advisorConnection, _) = await ConnectMemberAsync(host.Factory, role: "advisor", tenant: tenant);
        await using var __ = advisorConnection;
        var tenantId = Guid.Parse(tenant);
        var own = await SupportTicketSeed.SaveInternalTicketAsync(host.Factory, tenantId, advisor, cashier);
        var foreign = await SupportTicketSeed.SaveInternalTicketAsync(host.Factory, tenantId, advisor, advisor);
        var external = await SupportTicketSeed.SaveAnonymousTicketAsync(host.Factory, tenantId);

        await connection.InvokeAsync("Subscribe", SupportRealtimeTopics.Ticket(own.Id.Value), Ct);
        var onExternal = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync("Subscribe", SupportRealtimeTopics.Ticket(external.Id.Value), Ct));
        var onForeign = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync("Subscribe", SupportRealtimeTopics.Ticket(foreign.Id.Value), Ct));

        AssertForbidden(onExternal);
        AssertForbidden(onForeign);
        // The advisor manages Support: she listens to the same three.
        foreach (var ticket in new[] { own, foreign, external })
        {
            await advisorConnection.InvokeAsync("Subscribe", SupportRealtimeTopics.Ticket(ticket.Id.Value), Ct);
        }
    }

    /// <summary>A participant works the task, so her presence is allowed; whoever only reads it listens but does not write. No factory role
    /// grants read without manage, so this host answers <c>Read</c> yes and nothing else for one member.</summary>
    [Fact]
    public async Task PresenceOnATaskIsForParticipantsNotForReaders()
    {
        var readOnly = new ReadOnlyMemberPermissions();
        var (database, factory) = await OpenOwnHostAsync(services =>
        {
            var real = services.Single(descriptor => descriptor.ServiceType == typeof(ISupportMemberPermissions));
            services.RemoveAll<ISupportMemberPermissions>();
            services.AddScoped(real.ImplementationType!);
            services.AddScoped<ISupportMemberPermissions>(provider =>
            {
                readOnly.Inner = (ISupportMemberPermissions)provider.GetRequiredService(real.ImplementationType!);
                return readOnly;
            });
        });
        await using var _ = database;
        await using var __ = factory;
        var (tenant, participant, participantConnection, _) = await ConnectMemberAsync(factory, role: "cashier");
        await using var ___ = participantConnection;
        var (_, reader, readerConnection, _) = await ConnectMemberAsync(factory, role: "cashier", tenant: tenant);
        await using var ____ = readerConnection;
        readOnly.ReadOnlyMember = reader;
        var task = await SupportTicketSeed.SaveInternalTicketAsync(factory, Guid.Parse(tenant), participant, participant);
        var topic = SupportRealtimeTopics.Ticket(task.Id.Value);

        await participantConnection.InvokeAsync("Subscribe", topic, Ct);
        await participantConnection.InvokeAsync("SetPresence", topic, "composing", Ct);
        await readerConnection.InvokeAsync("Subscribe", topic, Ct);
        var error = await Assert.ThrowsAsync<HubException>(() => readerConnection.InvokeAsync("SetPresence", topic, "composing", Ct));

        AssertForbidden(error);
    }

    [Fact]
    public async Task AParticipantWhoLostTaskUseCannotSubscribe()
    {
        var (tenant, member, connection, _) = await ConnectMemberAsync(host.Factory, role: "cashier");
        await using var _ = connection;
        var task = await SupportTicketSeed.SaveInternalTicketAsync(host.Factory, Guid.Parse(tenant), member, member);
        var topic = SupportRealtimeTopics.Ticket(task.Id.Value);
        await connection.InvokeAsync("Subscribe", topic, Ct);
        await connection.InvokeAsync("Unsubscribe", topic, Ct);

        // She is still creator, assignee and an active member, but her role no longer grants support.task.use: the permissions are
        // read from the database on every Subscribe, never remembered from the handshake.
        await ChangeRoleAsync(host.Factory, tenant, member, "no-such-role");
        var onTask = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Subscribe", topic, Ct));
        var onTasks = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync("Subscribe", SupportRealtimeTopics.Tasks(member), Ct));

        AssertForbidden(onTask);
        AssertForbidden(onTasks);
    }

    [Theory]
    [InlineData("support:tasks:")]
    [InlineData("support:tasks:no-es-guid")]
    [InlineData("support:tasks:{00000000-0000-0000-0000-000000000001}")]
    [InlineData("support:tasks:00000000000000000000000000000001")]
    public async Task AMalformedTasksTopicIsForbidden(string topic)
    {
        var (_, _, connection, _) = await ConnectMemberAsync(host.Factory, role: "cashier");
        await using var _ = connection;

        var error = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Subscribe", topic, Ct));

        AssertForbidden(error);
    }

    [Fact]
    public async Task AnUppercaseTasksTopicOfHerOwnIsForbidden()
    {
        var (_, member, connection, _) = await ConnectMemberAsync(host.Factory, role: "cashier");
        await using var _ = connection;
        var upper = "support:tasks:" + member.ToString("D").ToUpperInvariant();

        var error = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Subscribe", upper, Ct));

        // Same member, other casing: it would be another SignalR group, so only the exact form is a topic.
        AssertForbidden(error);
    }

    private static async Task ChangeRoleAsync(QepApiFactory factory, string tenantId, Guid memberId, string role)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var tenant = await tenancy.Tenants.FindAsync([new TenantId(Guid.Parse(tenantId))], Ct)
            ?? throw new InvalidOperationException("The tenant was not found.");
        var membership = await tenancy.Memberships.FindAsync([new MembershipId(memberId)], Ct)
            ?? throw new InvalidOperationException("The membership was not found.");
        membership.ChangeRoles(tenant, [role], DateTimeOffset.UtcNow);
        await tenancy.SaveChangesAsync(Ct);
    }

    private sealed class ReadOnlyMemberPermissions : ISupportMemberPermissions
    {
        public ISupportMemberPermissions? Inner { get; set; }

        public Guid? ReadOnlyMember { get; set; }

        public Task<bool> HasAnyAsync(
            Guid tenantId, Guid memberId, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken) =>
            memberId == ReadOnlyMember
                ? Task.FromResult(permissions.Contains(SupportPermissions.Read))
                : Inner!.HasAnyAsync(tenantId, memberId, permissions, cancellationToken);
    }
}
