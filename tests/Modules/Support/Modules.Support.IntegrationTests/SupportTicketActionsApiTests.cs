using System.Net;
using System.Text.Json;
using Modules.Support.Application;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportTicketScenario;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Las acciones del ticket: estado (con la máquina, la pausa y los ciclos de resolución), responsable, tipo y cliente, cada una con
/// <c>If-Match</c>, un evento y una entrada de auditoría. Host propio de la clase con el reloj movible
/// (<see cref="ClockedSupportHost"/>); el alta es el viernes 2026-11-13 a las 10:00 de Bogotá con «Petición general» (vence el
/// 2026-12-07).
/// </summary>
public sealed class SupportTicketActionsApiTests(ClockedSupportHost host) : IClassFixture<ClockedSupportHost>
{
    [Fact]
    public async Task AStatusOutsideTheTableIs422()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var ticket = await scenario.CreateAsync();

        var response = await scenario.ActAsync(Id(ticket), "status", new ChangeTicketStatusRequest("WaitingInternal"), version: 1);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.ticket.transition_invalid", await CodeOfAsync(response));
        var detail = await scenario.DetailAsync(Id(ticket));
        Assert.Equal("New", detail.GetProperty("status").GetString());
        Assert.Equal(1L, detail.GetProperty("version").GetInt64());
        Assert.Single(detail.GetProperty("events").EnumerateArray());
    }

    [Fact]
    public async Task AnUnknownStatusIs422OnTheField()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var ticket = await scenario.CreateAsync();

        var response = await scenario.ActAsync(Id(ticket), "status", new ChangeTicketStatusRequest("open"), version: 1);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
        Assert.Equal(["Status"], await ErrorKeysAsync(response));
    }

    [Fact]
    public async Task WithoutIfMatchIs428AndWithAnOldVersionIs412()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var ticket = await scenario.CreateAsync();

        var withoutVersion = await scenario.ActAsync(Id(ticket), "status", new ChangeTicketStatusRequest("Open"), version: null);
        var opened = await scenario.ActAsync(Id(ticket), "status", new ChangeTicketStatusRequest("Open"), version: 1);
        var stale = await scenario.ActAsync(Id(ticket), "assignee", new AssignTicketRequest(scenario.CallerMemberId), version: 1);

        Assert.Equal(HttpStatusCode.PreconditionRequired, withoutVersion.StatusCode);
        Assert.Equal("precondition.if_match_required", await CodeOfAsync(withoutVersion));
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.Equal(2L, (await JsonAsync(opened)).GetProperty("version").GetInt64());
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("concurrency.conflict", await CodeOfAsync(stale));
        Assert.Equal(JsonValueKind.Null, (await scenario.DetailAsync(Id(ticket))).GetProperty("assignee").ValueKind);
    }

    [Fact]
    public async Task ATicketOfAnotherTenantUnderMyRouteIs404()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var other = await StartAsync(scenario.Factory);
        var foreign = await other.CreateAsync();

        var response = await scenario.ActAsync(Id(foreign), "status", new ChangeTicketStatusRequest("Open"), version: 1);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("support.ticket.not_found", await CodeOfAsync(response));
        Assert.Equal("New", (await other.DetailAsync(Id(foreign))).GetProperty("status").GetString());
    }

    [Fact]
    public async Task PausingAndResumingMovesTheDueDate()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var ticket = await scenario.CreateAsync();

        host.Clock.UtcNow = At(11, 16);
        ticket = await scenario.MoveAsync(ticket, "Open");
        host.Clock.UtcNow = At(11, 17);
        ticket = await scenario.MoveAsync(ticket, "WaitingOnCustomer");
        host.Clock.UtcNow = At(11, 23);
        ticket = await scenario.MoveAsync(ticket, "Open");

        var clock = ticket.GetProperty("clock");
        Assert.Equal("2026-12-11", clock.GetProperty("dueOn").GetString());
        Assert.Equal(3, clock.GetProperty("pausedBusinessDays").GetInt32());
        Assert.Equal("OnTrack", clock.GetProperty("dueState").GetString());
    }

    [Fact]
    public async Task WhilePausedTheClockSaysPaused()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var ticket = await scenario.CreateAsync();

        ticket = await scenario.MoveAsync(ticket, "WaitingOnCustomer");

        Assert.Equal("WaitingOnCustomer", ticket.GetProperty("status").GetString());
        Assert.Equal("Paused", ticket.GetProperty("clock").GetProperty("dueState").GetString());
        Assert.Equal(JsonValueKind.Null, ticket.GetProperty("clock").GetProperty("businessDaysLeft").ValueKind);
        Assert.Equal("2026-12-07", ticket.GetProperty("clock").GetProperty("dueOn").GetString());
        Assert.Equal(["Open", "WaitingInternal", "Resolved", "Closed"], Strings(ticket.GetProperty("allowedTransitions")));
    }

    [Fact]
    public async Task ReopeningCountsASecondResolutionCycle()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var ticket = await scenario.CreateAsync();

        ticket = await scenario.MoveAsync(ticket, "Resolved");
        Assert.Equal(1, ticket.GetProperty("resolutionCount").GetInt32());
        Assert.Equal("Finished", ticket.GetProperty("clock").GetProperty("dueState").GetString());
        ticket = await scenario.MoveAsync(ticket, "Open");
        ticket = await scenario.MoveAsync(ticket, "Resolved");

        Assert.Equal(2, ticket.GetProperty("resolutionCount").GetInt32());
        Assert.Equal(["Closed", "Open"], Strings(ticket.GetProperty("allowedTransitions")));
        Assert.Equal(
            [("Created", null, "New"), ("StatusChanged", "New", "Resolved"), ("StatusChanged", "Resolved", "Open"), ("StatusChanged", "Open", "Resolved")],
            ticket.GetProperty("events").EnumerateArray().Select(item => (
                item.GetProperty("kind").GetString()!,
                item.GetProperty("fromStatus").GetString(),
                item.GetProperty("toStatus").GetString()!)));
    }

    [Fact]
    public async Task ClosedIsTerminal()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var ticket = await scenario.CreateAsync();
        ticket = await scenario.MoveAsync(ticket, "Resolved");
        ticket = await scenario.MoveAsync(ticket, "Closed");

        var reopen = await scenario.ActAsync(Id(ticket), "status", new ChangeTicketStatusRequest("Open"), ticket.GetProperty("version").GetInt64());

        Assert.Equal([], Strings(ticket.GetProperty("allowedTransitions")));
        Assert.Equal("Finished", ticket.GetProperty("clock").GetProperty("dueState").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reopen.StatusCode);
        Assert.Equal("support.ticket.transition_invalid", await CodeOfAsync(reopen));
    }

    [Fact]
    public async Task ChangingTheTypeRecalculatesFromTheReception()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var ticket = await scenario.CreateAsync();
        host.Clock.UtcNow = At(11, 20);

        var documents = await scenario.ActAsync(
            Id(ticket), "type", new ChangeTicketTypeRequest(await scenario.TypeIdAsync("Petición de documentos o información")), version: 1);
        var documentsBody = await JsonAsync(documents);
        var support = await scenario.ActAsync(
            Id(ticket), "type", new ChangeTicketTypeRequest(await scenario.TypeIdAsync("Soporte técnico")), version: 2);
        var supportBody = await JsonAsync(support);

        Assert.Equal(HttpStatusCode.OK, documents.StatusCode);
        Assert.Equal("2026-11-30", documentsBody.GetProperty("clock").GetProperty("dueOn").GetString());
        Assert.Equal(10, documentsBody.GetProperty("clock").GetProperty("termBusinessDays").GetInt32());
        Assert.Equal(HttpStatusCode.OK, support.StatusCode);
        Assert.Equal(JsonValueKind.Null, supportBody.GetProperty("clock").GetProperty("dueOn").ValueKind);
        Assert.Equal("NoTerm", supportBody.GetProperty("clock").GetProperty("dueState").GetString());
        Assert.False(supportBody.GetProperty("clock").GetProperty("termIsLegal").GetBoolean());
        Assert.Equal("Soporte técnico", supportBody.GetProperty("type").GetProperty("name").GetString());
        var typeEvents = supportBody.GetProperty("events").EnumerateArray().Where(item => item.GetProperty("kind").GetString() == "TypeChanged");
        Assert.Equal(
            [("Petición general", "Petición de documentos o información"), ("Petición de documentos o información", "Soporte técnico")],
            typeEvents.Select(item => (item.GetProperty("fromTypeName").GetString()!, item.GetProperty("toTypeName").GetString()!)));
    }

    [Fact]
    public async Task AnInactiveTypeCannotBeChosen()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var ticket = await scenario.CreateAsync();
        var typeId = await scenario.TypeIdAsync("Sugerencia");
        var deactivated = await PutAsync(
            scenario.Client,
            $"{scenario.Url}/settings/ticket-types/{typeId}",
            new { name = "Sugerencia", termBusinessDays = 15, termIsLegal = true, requiresFormalResponse = false, isPublished = true, isActive = false },
            version: 1);
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);

        var response = await scenario.ActAsync(Id(ticket), "type", new ChangeTicketTypeRequest(typeId), version: 1);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.ticket_type.unavailable", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AssigningAndUnassigningWritesEvents()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var advisor = await SeedMemberAsync(
            scenario.Factory, $"advisor-{scenario.Tenant}@example.com", "Beto Asesor", "advisor", tenantId: scenario.Tenant);
        var ticket = await scenario.CreateAsync();

        var assigned = await scenario.ActAsync(Id(ticket), "assignee", new AssignTicketRequest(advisor), version: 1);
        var unassigned = await scenario.ActAsync(Id(ticket), "assignee", new AssignTicketRequest(null), version: 2);

        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        Assert.Equal("Beto Asesor", (await JsonAsync(assigned)).GetProperty("assignee").GetProperty("displayName").GetString());
        Assert.Equal(HttpStatusCode.OK, unassigned.StatusCode);
        var body = await JsonAsync(unassigned);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("assignee").ValueKind);
        var events = body.GetProperty("events").EnumerateArray().Where(item => item.GetProperty("kind").GetString() == "AssigneeChanged").ToList();
        Assert.Equal([2, 3], events.Select(item => item.GetProperty("ordinal").GetInt32()));
        Assert.Equal(advisor, events[0].GetProperty("assignee").GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, events[1].GetProperty("assignee").ValueKind);
        Assert.Equal(2, await CountAuditEntriesAsync(scenario.Factory, SupportAuditActions.TicketAssigned, Id(ticket).ToString()));
    }

    [Fact]
    public async Task AnAssigneeWhoCannotManageIs422()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var billing = await SeedMemberAsync(
            scenario.Factory, $"billing-{scenario.Tenant}@example.com", "Caro Cobros", "billing", tenantId: scenario.Tenant);
        var ticket = await scenario.CreateAsync();

        var response = await scenario.ActAsync(Id(ticket), "assignee", new AssignTicketRequest(billing), version: 1);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.ticket.assignee_invalid", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AClosedTicketCanStillBeAssignedRetypedAndLinked()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var customer = await SeedCustomerAsync(scenario.Factory, scenario.TenantId, "Novasteel SAS");
        var ticket = await scenario.CreateAsync();
        ticket = await scenario.MoveAsync(ticket, "Resolved");
        ticket = await scenario.MoveAsync(ticket, "Closed");
        var version = ticket.GetProperty("version").GetInt64();

        var assigned = await scenario.ActAsync(Id(ticket), "assignee", new AssignTicketRequest(scenario.CallerMemberId), version);
        var retyped = await scenario.ActAsync(Id(ticket), "type", new ChangeTicketTypeRequest(await scenario.TypeIdAsync("Queja")), version + 1);
        var linked = await scenario.ActAsync(Id(ticket), "customer", new LinkTicketCustomerRequest(customer), version + 2);

        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retyped.StatusCode);
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        var body = await JsonAsync(linked);
        Assert.Equal("Closed", body.GetProperty("status").GetString());
        Assert.Equal("Queja", body.GetProperty("type").GetProperty("name").GetString());
        Assert.Equal(customer, body.GetProperty("customer").GetProperty("id").GetGuid());
        Assert.Equal(scenario.CallerMemberId, body.GetProperty("assignee").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task LinkingAMergedCustomerIs422Inactive()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var merged = await SeedCustomerAsync(scenario.Factory, scenario.TenantId, "Novasteel SAS");
        await DeactivateCustomerAsync(scenario.Factory, merged);
        var ticket = await scenario.CreateAsync();

        var response = await scenario.ActAsync(Id(ticket), "customer", new LinkTicketCustomerRequest(merged), version: 1);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.ticket.customer_inactive", await CodeOfAsync(response));
        Assert.Equal(JsonValueKind.Null, (await scenario.DetailAsync(Id(ticket))).GetProperty("customer").ValueKind);
    }

    [Fact]
    public async Task UnlinkingTheOnlyNameIs422()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var customer = await SeedCustomerAsync(scenario.Factory, scenario.TenantId, "Novasteel SAS");
        var ticket = await scenario.CreateAsync(
            Body(await scenario.TypeIdAsync("Queja"), requester: new { email = "compras@novasteel.test" }, customerId: customer));

        var response = await scenario.ActAsync(Id(ticket), "customer", new LinkTicketCustomerRequest(null), version: 1);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.ticket.requester_required", await CodeOfAsync(response));
        Assert.Equal(customer, (await scenario.DetailAsync(Id(ticket))).GetProperty("customer").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task EveryActionWritesOneEventAndOneAuditEntry()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var customer = await SeedCustomerAsync(scenario.Factory, scenario.TenantId, "Novasteel SAS");
        var ticket = await scenario.CreateAsync();
        var id = Id(ticket);

        Assert.Equal(HttpStatusCode.OK, (await scenario.ActAsync(id, "status", new ChangeTicketStatusRequest("Open"), 1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await scenario.ActAsync(id, "assignee", new AssignTicketRequest(scenario.CallerMemberId), 2)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await scenario.ActAsync(id, "type", new ChangeTicketTypeRequest(await scenario.TypeIdAsync("Reclamo")), 3)).StatusCode);
        var last = await scenario.ActAsync(id, "customer", new LinkTicketCustomerRequest(customer), 4);
        // El mismo cliente otra vez no cambia nada: ni evento, ni versión, ni auditoría.
        var repeated = await scenario.ActAsync(id, "customer", new LinkTicketCustomerRequest(customer), 5);

        Assert.Equal(HttpStatusCode.OK, last.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        var body = await JsonAsync(repeated);
        Assert.Equal(5L, body.GetProperty("version").GetInt64());
        Assert.Equal(
            [(1, "Created"), (2, "StatusChanged"), (3, "AssigneeChanged"), (4, "TypeChanged"), (5, "CustomerChanged")],
            body.GetProperty("events").EnumerateArray().Select(item => (item.GetProperty("ordinal").GetInt32(), item.GetProperty("kind").GetString()!)));
        Assert.All(body.GetProperty("events").EnumerateArray(), item =>
            Assert.Equal(scenario.CallerMemberId, item.GetProperty("actor").GetProperty("id").GetGuid()));
        Assert.Equal("Novasteel SAS", body.GetProperty("events")[4].GetProperty("customer").GetProperty("name").GetString());
        foreach (var action in new[]
                 {
                     SupportAuditActions.TicketCreated, SupportAuditActions.TicketStatusChanged, SupportAuditActions.TicketAssigned,
                     SupportAuditActions.TicketTypeChanged, SupportAuditActions.TicketCustomerChanged,
                 })
        {
            Assert.Equal(1, await CountAuditEntriesAsync(scenario.Factory, action, id.ToString()));
        }
    }

    private static DateTimeOffset At(int month, int day) => new(2026, month, day, 15, 0, 0, TimeSpan.Zero);

    private static List<string> Strings(JsonElement array) => array.EnumerateArray().Select(item => item.GetString()!).ToList();
}
