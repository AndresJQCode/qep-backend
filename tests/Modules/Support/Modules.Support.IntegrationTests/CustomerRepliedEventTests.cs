using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Support.Application;
using Modules.Support.Domain;
using Testcontainers.PostgreSql;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;
using static Modules.Support.IntegrationTests.SupportTicketSeed;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Spec 2026-11-22 §6 and §12: the thread follower reopens a ticket that was waiting on the customer and, if it has an assignee, writes
/// <c>support.ticket.customer-replied.v1</c> to the platform outbox in the same save. Own host with the worker off: the test calls the
/// follower by hand.
/// </summary>
public sealed class CustomerRepliedEventTests : IAsyncLifetime
{
    private const string EventName = "support.ticket.customer-replied.v1";

    private static readonly DateTimeOffset Since = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    private PostgreSqlContainer? _database;
    private QepApiFactory? _factory;

    private QepApiFactory Factory => _factory ?? throw new InvalidOperationException("The host is not started.");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => (_database, _factory) = await OpenOwnHostAsync();

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Fact]
    public async Task ALinkedInboundOnAWaitingTicketWritesTheEvent()
    {
        var (tenant, conversation) = await NewConversationAsync();
        var assignee = Guid.CreateVersion7();
        var ticket = await WaitingTicketAsync(tenant, conversation, assignee);
        await SeedMessageAsync(Factory, tenant, conversation, Since.AddMinutes(5));

        await ProcessAsync();

        Assert.Equal(SupportTicketStatus.Open, (await FindTicketAsync(Factory, tenant, ticket.Id)).Status);
        Assert.Equal(1, await EventCountAsync(ticket.Id));
        var json = await TextAsync(
            Factory,
            "SELECT payload::text FROM platform.outbox_messages WHERE event_name = @name AND correlation_id = @id",
            ("name", EventName), ("id", ticket.Id.Value.ToString("D")));
        var payload = JsonDocument.Parse(json!).RootElement;
        Assert.Equal(tenant, payload.GetProperty("tenantId").GetGuid());
        Assert.Equal(ticket.Id.Value, payload.GetProperty("ticketId").GetGuid());
        Assert.Equal(ticket.DisplayNumber, payload.GetProperty("ticketNumber").GetString());
        Assert.Equal(assignee, payload.GetProperty("assigneeMemberId").GetGuid());
    }

    [Fact]
    public async Task AWaitingTicketWithoutAssigneeWritesNoEvent()
    {
        var (tenant, conversation) = await NewConversationAsync();
        var ticket = await WaitingTicketAsync(tenant, conversation, assignee: null);
        await SeedMessageAsync(Factory, tenant, conversation, Since.AddMinutes(5));

        await ProcessAsync();

        Assert.Equal(SupportTicketStatus.Open, (await FindTicketAsync(Factory, tenant, ticket.Id)).Status);
        Assert.Equal(0, await EventCountAsync(ticket.Id));
    }

    private async Task<SupportTicket> WaitingTicketAsync(Guid tenant, Guid conversation, Guid? assignee)
    {
        var ticket = await SaveConversationTicketAsync(Factory, tenant, conversation, Since);
        return await UpdateTicketAsync(Factory, tenant, ticket.Id, value =>
        {
            if (assignee is { } member)
            {
                value.Assign(member, Guid.CreateVersion7(), Since.AddMinutes(1));
            }

            value.ChangeStatus(SupportTicketStatus.WaitingOnCustomer, Guid.CreateVersion7(), Since.AddMinutes(2), Clock);
        });
    }

    private async Task<(Guid Tenant, Guid Conversation)> NewConversationAsync()
    {
        var tenant = NewTenantId();
        await SeedTenantAsync(Factory, tenant);
        var tenantId = Guid.Parse(tenant);
        return (tenantId, await SeedConversationAsync(Factory, tenantId));
    }

    private async Task<int> ProcessAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SupportThreadFollower>().ProcessBatchAsync(Ct);
    }

    private Task<long> EventCountAsync(SupportTicketId ticketId) =>
        ScalarAsync(
            Factory,
            "SELECT count(*) FROM platform.outbox_messages WHERE event_name = @name AND correlation_id = @id",
            ("name", EventName), ("id", ticketId.Value.ToString("D")));
}
