using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Support.Application;
using Modules.Support.Domain;
using static Modules.Support.IntegrationTests.AppHubHarness;
using static Modules.Support.IntegrationTests.SupportApiHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Spec 2026-11-15 §5.3 and plan Task 3: «está respondiendo» per connection. Each connection receives the present members without its
/// own (the SPA does not know its <c>memberId</c>), the photo arrives on subscribe, and closing one tab of a member keeps the member
/// present from the other. One host for the class (Valkey off: the pod-local registry), each test with its own tenant and ticket.
/// </summary>
public sealed class AppHubPresenceTests(SupportHostWithoutWorkers host) : IClassFixture<SupportHostWithoutWorkers>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ComposingReachesTheOtherMemberWithoutTheirOwnEntry()
    {
        var room = await RoomAsync(host.Factory);
        await using var ana = room.Ana;
        await using var bruno = room.Bruno;
        await ana.Connection.InvokeAsync("Subscribe", room.Topic, Ct);
        await bruno.Connection.InvokeAsync("Subscribe", room.Topic, Ct);

        await ana.Connection.InvokeAsync("SetPresence", room.Topic, "composing", Ct);

        var seenByBruno = await bruno.Received.WaitForPresenceAsync(payload => Members(payload).Count == 1);
        var member = Assert.Single(Members(seenByBruno));
        Assert.Equal(room.Topic, seenByBruno.GetProperty("topic").GetString());
        Assert.Equal(ana.MemberId, member.GetProperty("memberId").GetGuid());
        Assert.Equal("Ana", member.GetProperty("name").GetString());
        Assert.Equal("composing", member.GetProperty("state").GetString());
        // Ana gets the same notice, without herself: nobody else is writing.
        await Task.Delay(500, Ct);
        Assert.All(ana.Received.Entries.Where(entry => entry.Event == "presenceChanged"), entry => Assert.Empty(Members(entry.Payload)));
    }

    [Fact]
    public async Task SubscribingReceivesTheCurrentSnapshot()
    {
        var room = await RoomAsync(host.Factory);
        await using var ana = room.Ana;
        await using var bruno = room.Bruno;
        await ana.Connection.InvokeAsync("Subscribe", room.Topic, Ct);
        await ana.Connection.InvokeAsync("SetPresence", room.Topic, "composing", Ct);

        await bruno.Connection.InvokeAsync("Subscribe", room.Topic, Ct);

        var photo = await bruno.Received.WaitForPresenceAsync(payload => Members(payload).Count == 1);
        Assert.Equal(ana.MemberId, Assert.Single(Members(photo)).GetProperty("memberId").GetGuid());
    }

    [Fact]
    public async Task NoneAndDisconnectClearThePresence()
    {
        var room = await RoomAsync(host.Factory);
        await using var ana = room.Ana;
        await using var bruno = room.Bruno;
        await ana.Connection.InvokeAsync("Subscribe", room.Topic, Ct);
        await bruno.Connection.InvokeAsync("Subscribe", room.Topic, Ct);
        await ana.Connection.InvokeAsync("SetPresence", room.Topic, "composing", Ct);
        await bruno.Received.WaitForPresenceAsync(payload => Members(payload).Count == 1);
        bruno.Received.Clear();

        await ana.Connection.InvokeAsync("SetPresence", room.Topic, "none", Ct);

        await bruno.Received.WaitForPresenceAsync(payload => Members(payload).Count == 0);
        await ana.Connection.InvokeAsync("SetPresence", room.Topic, "composing", Ct);
        await bruno.Received.WaitForPresenceAsync(payload => Members(payload).Count == 1);
        bruno.Received.Clear();

        await ana.Connection.StopAsync(Ct);

        await bruno.Received.WaitForPresenceAsync(payload => Members(payload).Count == 0);
    }

    [Fact]
    public async Task ClosingOneTabKeepsTheMemberPresentFromTheOther()
    {
        var room = await RoomAsync(host.Factory);
        await using var ana = room.Ana;
        await using var bruno = room.Bruno;
        await using var anaSecondTab = Connect(host.Factory, room.Tenant, ana.SubjectId);
        var secondTabReceived = Listen(anaSecondTab);
        await anaSecondTab.StartAsync(Ct);
        await ana.Connection.InvokeAsync("Subscribe", room.Topic, Ct);
        await anaSecondTab.InvokeAsync("Subscribe", room.Topic, Ct);
        await bruno.Connection.InvokeAsync("Subscribe", room.Topic, Ct);
        await ana.Connection.InvokeAsync("SetPresence", room.Topic, "composing", Ct);
        await anaSecondTab.InvokeAsync("SetPresence", room.Topic, "composing", Ct);
        // One announcement per SetPresence: once both reached Bruno nothing from before the stop is still in flight.
        await bruno.Received.WaitForPresenceAsync(payload => Members(payload).Count == 1, times: 2);
        var mark = bruno.Received.Count;

        await ana.Connection.StopAsync(Ct);

        // The disconnect announces again (an entry after the mark), and Ana is still there through her second tab.
        var after = await bruno.Received.WaitForPresenceAfterAsync(mark);
        Assert.Equal(ana.MemberId, Assert.Single(Members(after)).GetProperty("memberId").GetGuid());
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        Assert.All(bruno.Received.PresenceAfter(mark), payload =>
            Assert.Equal(ana.MemberId, Assert.Single(Members(payload)).GetProperty("memberId").GetGuid()));
        Assert.DoesNotContain(secondTabReceived.Entries, entry => entry.Event == "presenceChanged" && Members(entry.Payload).Count > 0);
    }

    [Fact]
    public async Task SetPresenceNeedsASubscriptionATicketTopicAndAKnownState()
    {
        var room = await RoomAsync(host.Factory);
        await using var ana = room.Ana;
        await using var bruno = room.Bruno;

        var notSubscribed = await Assert.ThrowsAsync<HubException>(
            () => ana.Connection.InvokeAsync("SetPresence", room.Topic, "composing", Ct));
        await ana.Connection.InvokeAsync("Subscribe", SupportRealtimeTopics.Inbox, Ct);
        var inbox = await Assert.ThrowsAsync<HubException>(
            () => ana.Connection.InvokeAsync("SetPresence", SupportRealtimeTopics.Inbox, "composing", Ct));
        await ana.Connection.InvokeAsync("Subscribe", room.Topic, Ct);
        var unknownState = await Assert.ThrowsAsync<HubException>(
            () => ana.Connection.InvokeAsync("SetPresence", room.Topic, "watching", Ct));

        Assert.All([notSubscribed, inbox], error =>
            Assert.Contains(SupportErrorCodes.RealtimeTopicForbidden, error.Message, StringComparison.Ordinal));
        // An unknown state is refused by the hub itself, before any authorizer: its generic code.
        Assert.Contains("realtime.topic_forbidden", unknownState.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("support.realtime", unknownState.Message, StringComparison.Ordinal);
    }

    /// <summary>No factory role grants read without manage, so this host answers <c>Read</c> yes and <c>Manage</c> no for one member.</summary>
    [Fact]
    public async Task SetPresenceNeedsManage()
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
        var room = await RoomAsync(factory);
        await using var ana = room.Ana;
        await using var bruno = room.Bruno;
        readOnly.ReadOnlyMember = ana.MemberId;
        await ana.Connection.InvokeAsync("Subscribe", room.Topic, Ct);

        var error = await Assert.ThrowsAsync<HubException>(() => ana.Connection.InvokeAsync("SetPresence", room.Topic, "composing", Ct));

        Assert.Contains(SupportErrorCodes.RealtimeTopicForbidden, error.Message, StringComparison.Ordinal);
    }

    private static IReadOnlyList<JsonElement> Members(JsonElement payload) => [.. payload.GetProperty("members").EnumerateArray()];

    /// <summary>A tenant with one ticket and two advisors, Ana and Bruno, each with a started connection.</summary>
    private static async Task<Room> RoomAsync(QepApiFactory factory)
    {
        var tenant = NewTenantId();
        await SeedTenantAsync(factory, tenant);
        var ticket = await SupportTicketSeed.SaveAnonymousTicketAsync(factory, Guid.Parse(tenant));
        return new Room(
            tenant,
            SupportRealtimeTopics.Ticket(ticket.Id.Value),
            await AdvisorAsync(factory, tenant, "Ana"),
            await AdvisorAsync(factory, tenant, "Bruno"));
    }

    private static async Task<Advisor> AdvisorAsync(QepApiFactory factory, string tenant, string name)
    {
        var subject = Guid.CreateVersion7().ToString();
        var member = await SeedCallerMembershipAsync(factory, name, "advisor", tenant, subject);
        var connection = Connect(factory, tenant, subject);
        var received = Listen(connection);
        await connection.StartAsync(Ct);
        return new Advisor(subject, member, connection, received);
    }

    private sealed record Room(string Tenant, string Topic, Advisor Ana, Advisor Bruno);

    private sealed record Advisor(string SubjectId, Guid MemberId, HubConnection Connection, Received Received) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Connection.DisposeAsync();
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
