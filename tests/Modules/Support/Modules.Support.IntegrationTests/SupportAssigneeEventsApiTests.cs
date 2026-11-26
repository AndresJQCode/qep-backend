using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Support.Application;
using Npgsql;
using static Modules.Support.IntegrationTests.SupportApiHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Task-notices spec 2026-11-22 §5.2 and D-7: assigning a customer ticket publishes <c>assigned</c> (tasks keep <c>task-assigned</c>), and
/// every hand-over, resolution or closing of an assigned ticket publishes <c>assignee-released</c> in the same transaction. Reads
/// <c>platform.outbox_messages</c> like <see cref="SupportTaskRulesApiTests"/>.
/// </summary>
public sealed class SupportAssigneeEventsApiTests(ClockedSupportHost host) : IClassFixture<ClockedSupportHost>
{
    private const string Assigned = "support.ticket.assigned.v1";
    private const string TaskAssigned = "support.ticket.task-assigned.v1";
    private const string Released = "support.ticket.assignee-released.v1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record World(SupportTicketScenario Manager, Guid TaskOnlyMember, Guid AdvisorMember, Guid OtherAdvisorMember)
    {
        public QepApiFactory Factory => Manager.Factory;

        public Guid TenantId => Manager.TenantId;
    }

    private async Task<World> StartAsync()
    {
        var factory = await host.StartTestAsync();
        var manager = await SupportTicketScenario.StartAsync(factory);
        var taskOnly = await SeedCallerMembershipAsync(factory, "Cora Cajera", "cashier", manager.Tenant, Guid.CreateVersion7().ToString());
        var advisor = await SeedMemberAsync(
            factory, $"asesor-{Guid.CreateVersion7():N}@example.com", "Aldo Asesor", "advisor", tenantId: manager.Tenant);
        var other = await SeedMemberAsync(
            factory, $"asesor-{Guid.CreateVersion7():N}@example.com", "Ana Asesora", "advisor", tenantId: manager.Tenant);
        return new World(manager, taskOnly, advisor, other);
    }

    private static async Task<(Guid Id, long Version)> SeedTaskAsync(World world, Guid assignee)
    {
        var task = await SupportTicketSeed.SaveInternalTicketAsync(world.Factory, world.TenantId, world.Manager.CallerMemberId, assignee);
        return (task.Id.Value, task.Version);
    }

    private static async Task<(Guid Id, long Version)> CreateCustomerTicketAsync(World world)
    {
        var ticket = await world.Manager.CreateAsync();
        return (SupportTicketScenario.Id(ticket), SupportTicketScenario.Version(ticket));
    }

    private static async Task<long> AssignAsync(World world, Guid ticketId, long version, Guid? assignee)
    {
        var response = await world.Manager.ActAsync(ticketId, "assignee", new { assigneeMemberId = assignee }, version);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return SupportTicketScenario.Version(await JsonAsync(response));
    }

    private static async Task<IReadOnlyList<JsonElement>> EventsAsync(QepApiFactory factory, string eventName, Guid ticketId)
    {
        var connectionString = factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT payload::text FROM platform.outbox_messages WHERE event_name = @name AND correlation_id = @id::text ORDER BY occurred_at",
            connection);
        command.Parameters.AddWithValue("name", eventName);
        command.Parameters.AddWithValue("id", ticketId);
        var payloads = new List<JsonElement>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            payloads.Add(JsonDocument.Parse(reader.GetString(0)).RootElement.Clone());
        }

        return payloads;
    }

    [Fact]
    public async Task AssigningACustomerTicketToSomeoneElsePublishesAssigned()
    {
        var world = await StartAsync();
        var ticket = await CreateCustomerTicketAsync(world);

        await AssignAsync(world, ticket.Id, ticket.Version, world.AdvisorMember);

        var assigned = Assert.Single(await EventsAsync(world.Factory, Assigned, ticket.Id));
        Assert.Equal(world.AdvisorMember, assigned.GetProperty("assigneeMemberId").GetGuid());
        Assert.Equal(world.Manager.CallerMemberId, assigned.GetProperty("assignedByMemberId").GetGuid());
        Assert.Equal(world.TenantId, assigned.GetProperty("tenantId").GetGuid());
        Assert.Equal(ticket.Id, assigned.GetProperty("ticketId").GetGuid());
        Assert.Empty(await EventsAsync(world.Factory, TaskAssigned, ticket.Id));
    }

    [Fact]
    public async Task AssigningACustomerTicketToOneselfPublishesNoAssigned()
    {
        var world = await StartAsync();
        var ticket = await CreateCustomerTicketAsync(world);

        await AssignAsync(world, ticket.Id, ticket.Version, world.Manager.CallerMemberId);

        Assert.Empty(await EventsAsync(world.Factory, Assigned, ticket.Id));
    }

    [Fact]
    public async Task UnassigningACustomerTicketPublishesNoAssigned()
    {
        var world = await StartAsync();
        var ticket = await CreateCustomerTicketAsync(world);
        var version = await AssignAsync(world, ticket.Id, ticket.Version, world.AdvisorMember);

        await AssignAsync(world, ticket.Id, version, null);

        Assert.Single(await EventsAsync(world.Factory, Assigned, ticket.Id));
        Assert.Single(await EventsAsync(world.Factory, Released, ticket.Id));
    }

    [Fact]
    public async Task TakingACustomerTicketPublishesNoAssigned()
    {
        var world = await StartAsync();
        var ticket = await CreateCustomerTicketAsync(world);

        var response = await world.Manager.PostActionAsync($"tickets/{ticket.Id}/take", null, ticket.Version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await EventsAsync(world.Factory, Assigned, ticket.Id));
        Assert.Empty(await EventsAsync(world.Factory, Released, ticket.Id));
    }

    [Fact]
    public async Task CreatingACustomerTicketForSomeoneElsePublishesAssigned()
    {
        var world = await StartAsync();
        var body = SupportTicketScenario.Body(await world.Manager.TypeIdAsync("Petición general"), assignee: world.AdvisorMember);

        var created = await world.Manager.CreateAsync(body);

        var id = SupportTicketScenario.Id(created);
        var assigned = Assert.Single(await EventsAsync(world.Factory, Assigned, id));
        Assert.Equal(world.AdvisorMember, assigned.GetProperty("assigneeMemberId").GetGuid());
        Assert.Equal(world.Manager.CallerMemberId, assigned.GetProperty("assignedByMemberId").GetGuid());
        Assert.Empty(await EventsAsync(world.Factory, TaskAssigned, id));
    }

    [Fact]
    public async Task CreatingACustomerTicketForOneselfPublishesNoAssigned()
    {
        var world = await StartAsync();
        var body = SupportTicketScenario.Body(await world.Manager.TypeIdAsync("Petición general"), assignee: world.Manager.CallerMemberId);

        var created = await world.Manager.CreateAsync(body);

        Assert.Empty(await EventsAsync(world.Factory, Assigned, SupportTicketScenario.Id(created)));
    }

    [Fact]
    public async Task OpeningATicketFromAConversationForSomeoneElsePublishesAssigned()
    {
        var world = await StartAsync();
        var conversation = await SupportMessagingSeed.SeedConversationAsync(world.Factory, world.TenantId);

        var created = await world.Manager.CreateFromConversationAsync(conversation, assignee: world.AdvisorMember);

        var assigned = Assert.Single(await EventsAsync(world.Factory, Assigned, SupportTicketScenario.Id(created)));
        Assert.Equal(world.AdvisorMember, assigned.GetProperty("assigneeMemberId").GetGuid());
    }

    [Fact]
    public async Task AssigningATaskStillPublishesTaskAssignedOnly()
    {
        var world = await StartAsync();
        var task = await SeedTaskAsync(world, world.TaskOnlyMember);

        await AssignAsync(world, task.Id, task.Version, world.AdvisorMember);

        Assert.Single(await EventsAsync(world.Factory, TaskAssigned, task.Id));
        Assert.Empty(await EventsAsync(world.Factory, Assigned, task.Id));
    }

    [Fact]
    public async Task ReassigningATaskReleasesTheFormerAssignee()
    {
        var world = await StartAsync();
        var task = await SeedTaskAsync(world, world.TaskOnlyMember);

        await AssignAsync(world, task.Id, task.Version, world.AdvisorMember);

        var released = Assert.Single(await EventsAsync(world.Factory, Released, task.Id));
        Assert.Equal(world.TaskOnlyMember, released.GetProperty("formerAssigneeMemberId").GetGuid());
        Assert.Equal(SupportAssigneeReleaseReasons.Reassigned, released.GetProperty("reason").GetString());
        Assert.True(released.GetProperty("isInternal").GetBoolean());
        Assert.Equal(world.TenantId, released.GetProperty("tenantId").GetGuid());
        Assert.Equal(task.Id, released.GetProperty("ticketId").GetGuid());
        Assert.True(released.TryGetProperty("ticketNumber", out _));
        var assigned = Assert.Single(await EventsAsync(world.Factory, TaskAssigned, task.Id));
        Assert.Equal(world.AdvisorMember, assigned.GetProperty("assigneeMemberId").GetGuid());
    }

    [Fact]
    public async Task ReassigningACustomerTicketReleasesTheFormerAssignee()
    {
        var world = await StartAsync();
        var ticket = await CreateCustomerTicketAsync(world);
        var version = await AssignAsync(world, ticket.Id, ticket.Version, world.AdvisorMember);

        await AssignAsync(world, ticket.Id, version, world.OtherAdvisorMember);

        var released = Assert.Single(await EventsAsync(world.Factory, Released, ticket.Id));
        Assert.Equal(world.AdvisorMember, released.GetProperty("formerAssigneeMemberId").GetGuid());
        Assert.Equal(SupportAssigneeReleaseReasons.Reassigned, released.GetProperty("reason").GetString());
        Assert.False(released.GetProperty("isInternal").GetBoolean());
        var assigned = await EventsAsync(world.Factory, Assigned, ticket.Id);
        Assert.Equal(2, assigned.Count);
        Assert.Contains(assigned, payload => payload.GetProperty("assigneeMemberId").GetGuid() == world.OtherAdvisorMember);
    }

    [Fact]
    public async Task TakingSomeoneElsesTicketReleasesThem()
    {
        var world = await StartAsync();
        var ticket = await CreateCustomerTicketAsync(world);
        var version = await AssignAsync(world, ticket.Id, ticket.Version, world.AdvisorMember);

        var response = await world.Manager.PostActionAsync($"tickets/{ticket.Id}/take", null, version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var released = Assert.Single(await EventsAsync(world.Factory, Released, ticket.Id));
        Assert.Equal(world.AdvisorMember, released.GetProperty("formerAssigneeMemberId").GetGuid());
        Assert.Equal(SupportAssigneeReleaseReasons.Reassigned, released.GetProperty("reason").GetString());
        // Taking is never told to the one who takes: only the first assignment has an assigned event.
        Assert.Single(await EventsAsync(world.Factory, Assigned, ticket.Id));
    }

    [Fact]
    public async Task ResolvingATicketReleasesItsAssignee()
    {
        var world = await StartAsync();
        var task = await SeedTaskAsync(world, world.AdvisorMember);
        var opened = await world.Manager.ActAsync(task.Id, "status", new { status = "Open" }, task.Version);
        var openedVersion = SupportTicketScenario.Version(await JsonAsync(opened));

        var resolved = await world.Manager.ActAsync(task.Id, "status", new { status = "Resolved" }, openedVersion);

        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        var released = Assert.Single(await EventsAsync(world.Factory, Released, task.Id));
        Assert.Equal(world.AdvisorMember, released.GetProperty("formerAssigneeMemberId").GetGuid());
        Assert.Equal(SupportAssigneeReleaseReasons.Resolved, released.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ClosingAResolvedTicketPublishesNothingNew()
    {
        var world = await StartAsync();
        var task = await SeedTaskAsync(world, world.AdvisorMember);
        var opened = await world.Manager.ActAsync(task.Id, "status", new { status = "Open" }, task.Version);
        var resolved = await world.Manager.ActAsync(
            task.Id, "status", new { status = "Resolved" }, SupportTicketScenario.Version(await JsonAsync(opened)));
        var resolvedVersion = SupportTicketScenario.Version(await JsonAsync(resolved));

        var closed = await world.Manager.ActAsync(task.Id, "status", new { status = "Closed" }, resolvedVersion);

        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        Assert.Single(await EventsAsync(world.Factory, Released, task.Id));
    }

    [Fact]
    public async Task SavingTheSameAssigneePublishesNothing()
    {
        var world = await StartAsync();
        var ticket = await CreateCustomerTicketAsync(world);
        var version = await AssignAsync(world, ticket.Id, ticket.Version, world.AdvisorMember);

        await AssignAsync(world, ticket.Id, version, world.AdvisorMember);

        Assert.Single(await EventsAsync(world.Factory, Assigned, ticket.Id));
        Assert.Empty(await EventsAsync(world.Factory, Released, ticket.Id));
    }

    [Fact]
    public async Task ReplyingAndResolvingReleasesTheAssignee()
    {
        var world = await StartAsync();
        var ticket = await CreateCustomerTicketAsync(world);
        var version = await AssignAsync(world, ticket.Id, ticket.Version, world.AdvisorMember);

        var replied = await world.Manager.PostActionAsync(
            $"tickets/{ticket.Id}/replies",
            new { clientId = Guid.CreateVersion7(), channel = "Email", text = "Ya quedó.", resolve = true },
            version);

        Assert.True(replied.StatusCode == HttpStatusCode.Created, await BodyAsync(replied));
        var released = Assert.Single(await EventsAsync(world.Factory, Released, ticket.Id));
        Assert.Equal(world.AdvisorMember, released.GetProperty("formerAssigneeMemberId").GetGuid());
        Assert.Equal(SupportAssigneeReleaseReasons.Resolved, released.GetProperty("reason").GetString());
        Assert.False(released.GetProperty("isInternal").GetBoolean());
    }

    [Fact]
    public async Task ClosingAnOpenTicketReleasesWithClosed()
    {
        var world = await StartAsync();
        var ticket = await CreateCustomerTicketAsync(world);
        var version = await AssignAsync(world, ticket.Id, ticket.Version, world.AdvisorMember);
        var waiting = await world.Manager.ActAsync(ticket.Id, "status", new { status = "WaitingOnCustomer" }, version);
        var waitingVersion = SupportTicketScenario.Version(await JsonAsync(waiting));

        var closed = await world.Manager.ActAsync(ticket.Id, "status", new { status = "Closed" }, waitingVersion);

        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        var released = Assert.Single(await EventsAsync(world.Factory, Released, ticket.Id));
        Assert.Equal(SupportAssigneeReleaseReasons.Closed, released.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ResolvingAnUnassignedTicketPublishesNoRelease()
    {
        var world = await StartAsync();
        var ticket = await CreateCustomerTicketAsync(world);

        var resolved = await world.Manager.ActAsync(ticket.Id, "status", new { status = "Resolved" }, ticket.Version);

        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        Assert.Empty(await EventsAsync(world.Factory, Released, ticket.Id));
    }

    // The Closed branch of the handler with a conversation runs inside its own transaction: the release must travel in it too.
    [Fact]
    public async Task ClosingATicketWithAConversationReleasesItsAssignee()
    {
        var world = await StartAsync();
        var conversation = await SupportMessagingSeed.SeedConversationAsync(world.Factory, world.TenantId);
        var created = await world.Manager.CreateFromConversationAsync(conversation);
        var id = SupportTicketScenario.Id(created);
        var version = await AssignAsync(world, id, SupportTicketScenario.Version(created), world.AdvisorMember);
        var waiting = await world.Manager.ActAsync(id, "status", new { status = "WaitingOnCustomer" }, version);
        var waitingVersion = SupportTicketScenario.Version(await JsonAsync(waiting));

        var closed = await world.Manager.ActAsync(id, "status", new { status = "Closed" }, waitingVersion);

        Assert.True(closed.StatusCode == HttpStatusCode.OK, await BodyAsync(closed));
        var released = Assert.Single(await EventsAsync(world.Factory, Released, id));
        Assert.Equal(SupportAssigneeReleaseReasons.Closed, released.GetProperty("reason").GetString());
    }
}
