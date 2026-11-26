using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Modules.Support.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Domain;
using static Modules.Support.IntegrationTests.AppHubHarness;
using static Modules.Support.IntegrationTests.SupportApiHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Spec 2026-11-15 §5.1 and plan Task 1: the global hub belongs to no module; Support authorizes its own topics. The tenant of a
/// topic is always the connection's, never one the client sends, and the SignalR group carries it. One host for the class, with
/// the thread follower off; each test seeds its own tenant.
/// </summary>
public sealed class AppHubSupportTests(SupportHostWithoutWorkers host) : IClassFixture<SupportHostWithoutWorkers>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnAdvisorSubscribesToTheInboxAndReceivesATicketChange()
    {
        var (tenant, _, connection, received) = await ConnectMemberAsync(host.Factory);
        await using var _ = connection;

        await connection.InvokeAsync("Subscribe", SupportRealtimeTopics.Inbox, Ct);
        await WaitUntilDeliveredAsync(host.Factory, Guid.Parse(tenant), SupportRealtimeTopics.Inbox, received);
        var ticket = Guid.CreateVersion7();

        await PublishTicketChangedAsync(host.Factory, Guid.Parse(tenant), SupportRealtimeTopics.Inbox, ticket);

        await received.WaitForTicketAsync(ticket);
    }

    [Fact]
    public async Task AMemberWithoutSupportCannotSubscribe()
    {
        var (_, _, connection, _) = await ConnectMemberAsync(host.Factory, role: "billing");
        await using var _ = connection;

        var error = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Subscribe", SupportRealtimeTopics.Inbox, Ct));

        Assert.Contains(SupportErrorCodes.RealtimeTopicForbidden, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATicketOfAnotherTenantIsForbidden()
    {
        var other = NewTenantId();
        await SeedTenantAsync(host.Factory, other);
        var foreign = await SupportTicketSeed.SaveAnonymousTicketAsync(host.Factory, Guid.Parse(other));
        var (tenant, _, connection, _) = await ConnectMemberAsync(host.Factory);
        await using var _ = connection;
        var own = await SupportTicketSeed.SaveAnonymousTicketAsync(host.Factory, Guid.Parse(tenant));

        var error = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync("Subscribe", SupportRealtimeTopics.Ticket(foreign.Id.Value), Ct));

        Assert.Contains(SupportErrorCodes.RealtimeTopicForbidden, error.Message, StringComparison.Ordinal);
        // The same connection can subscribe to a ticket of its own tenant: the rejection above is about the tenant, not the shape.
        await connection.InvokeAsync("Subscribe", SupportRealtimeTopics.Ticket(own.Id.Value), Ct);
    }

    [Theory]
    [InlineData("support:ticket:no-es-guid")]
    [InlineData("support:otro")]
    [InlineData("nadie:sabe")]
    [InlineData("")]
    public async Task AnUnknownTicketOrMalformedTopicIsForbidden(string topic)
    {
        var (_, _, connection, _) = await ConnectMemberAsync(host.Factory);
        await using var _ = connection;

        var error = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Subscribe", topic, Ct));

        // A topic with no authorizer (unknown prefix or empty) gets the hub's generic code; Support's own refusals keep Support's.
        var expected = topic.StartsWith("support:", StringComparison.Ordinal) ? SupportErrorCodes.RealtimeTopicForbidden : "realtime.topic_forbidden";
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        // «support.realtime.topic_forbidden» also contains the generic code, so the generic case has to rule Support's out.
        if (expected == "realtime.topic_forbidden")
        {
            Assert.DoesNotContain("support.realtime", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnUnknownTicketOrATopicTooLongIsForbidden()
    {
        var (_, _, connection, _) = await ConnectMemberAsync(host.Factory);
        await using var _ = connection;

        var unknown = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync("Subscribe", SupportRealtimeTopics.Ticket(Guid.CreateVersion7()), Ct));
        var tooLong = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync("Subscribe", "support:" + new string('x', 193), Ct));

        Assert.Contains(SupportErrorCodes.RealtimeTopicForbidden, unknown.Message, StringComparison.Ordinal);
        // Over the 200-character cap no authorizer is even looked up, so the hub's generic code answers.
        Assert.Contains("realtime.topic_forbidden", tooLong.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("support.realtime", tooLong.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUppercaseTicketTopicIsForbidden()
    {
        var (tenant, _, connection, _) = await ConnectMemberAsync(host.Factory);
        await using var _ = connection;
        var own = await SupportTicketSeed.SaveAnonymousTicketAsync(host.Factory, Guid.Parse(tenant));
        var upper = "support:ticket:" + own.Id.Value.ToString("D").ToUpperInvariant();

        var error = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Subscribe", upper, Ct));

        // Same ticket, other casing: it would be another group and another presence room, so only the exact form is a topic.
        Assert.Contains(SupportErrorCodes.RealtimeTopicForbidden, error.Message, StringComparison.Ordinal);
        await connection.InvokeAsync("Subscribe", SupportRealtimeTopics.Ticket(own.Id.Value), Ct);
    }

    [Fact]
    public async Task WithTheSupportModuleOffNobodySubscribes()
    {
        var tenant = NewTenantId();
        await SeedTenantAsync(host.Factory, tenant, TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Support));
        var (_, _, connection, _) = await ConnectMemberAsync(host.Factory, tenant: tenant);
        await using var _ = connection;

        var error = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Subscribe", SupportRealtimeTopics.Inbox, Ct));

        Assert.Contains(SupportErrorCodes.RealtimeTopicForbidden, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoTenantsOnTheSameTopicDoNotSeeEachOther()
    {
        var (mineTenant, _, mine, mineReceived) = await ConnectMemberAsync(host.Factory);
        await using var _ = mine;
        var (theirsTenant, _, theirs, theirsReceived) = await ConnectMemberAsync(host.Factory);
        await using var __ = theirs;
        await mine.InvokeAsync("Subscribe", SupportRealtimeTopics.Inbox, Ct);
        await theirs.InvokeAsync("Subscribe", SupportRealtimeTopics.Inbox, Ct);
        await WaitUntilDeliveredAsync(host.Factory, Guid.Parse(mineTenant), SupportRealtimeTopics.Inbox, mineReceived);
        await WaitUntilDeliveredAsync(host.Factory, Guid.Parse(theirsTenant), SupportRealtimeTopics.Inbox, theirsReceived);
        var ticket = Guid.CreateVersion7();

        await PublishTicketChangedAsync(host.Factory, Guid.Parse(mineTenant), SupportRealtimeTopics.Inbox, ticket);

        await mineReceived.WaitForTicketAsync(ticket);
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        // Only the published ticket counts: late probes of the other tenant's own warm-up can still arrive.
        Assert.False(theirsReceived.HasTicket(ticket));
    }

    [Fact]
    public async Task UnsubscribeStopsTheEvents()
    {
        var (tenant, _, connection, received) = await ConnectMemberAsync(host.Factory);
        await using var _ = connection;
        await connection.InvokeAsync("Subscribe", SupportRealtimeTopics.Inbox, Ct);
        await WaitUntilDeliveredAsync(host.Factory, Guid.Parse(tenant), SupportRealtimeTopics.Inbox, received);

        await connection.InvokeAsync("Unsubscribe", SupportRealtimeTopics.Inbox, Ct);
        var ticket = Guid.CreateVersion7();
        await PublishTicketChangedAsync(host.Factory, Guid.Parse(tenant), SupportRealtimeTopics.Inbox, ticket);

        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        // Only the published ticket counts: a probe still in flight from the warm-up may land after the unsubscribe.
        Assert.False(received.HasTicket(ticket));
        // Unsubscribing never fails, not even from a topic the connection does not have.
        await connection.InvokeAsync("Unsubscribe", "nadie:sabe", Ct);
    }

    [Fact]
    public async Task WithoutMembershipTheConnectionIsAborted()
    {
        var tenant = NewTenantId();
        await SeedTenantAsync(host.Factory, tenant);
        await using var connection = Connect(host.Factory, tenant, Guid.CreateVersion7().ToString());
        var closed = new TaskCompletionSource();
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        await TryStartAsync(connection);

        if (connection.State != HubConnectionState.Disconnected)
        {
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }

        Assert.Equal(HubConnectionState.Disconnected, connection.State);
    }
}
