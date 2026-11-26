using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Support.Application;
using Modules.Support.Domain;
using Npgsql;
using static Modules.Support.IntegrationTests.SupportApiHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Spec 2026-11-22 §5.2 (Task 11): <c>PUT /tickets/{id}/due-date</c>, la fecha libre de una tarea. La cambia quien la creó, quien la
/// trabaja o quien gestiona; una fecha anterior a hoy en la zona del tenant se rechaza; sobre un externo es 422 <c>not_internal</c>. El
/// reloj del host fija «hoy» en el viernes 13 de noviembre de 2026 (Bogotá). Las tareas se siembran por el dominio con una fecha lejana.
/// </summary>
public sealed class SupportTaskDueDateApiTests(ClockedSupportHost host) : IClassFixture<ClockedSupportHost>
{
    private static readonly DateOnly FarDate = new(2030, 1, 1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record World(SupportTicketScenario Manager, HttpClient TaskOnly, Guid TaskOnlyMember, Guid AdvisorMember)
    {
        public QepApiFactory Factory => Manager.Factory;

        public string Url => Manager.Url;

        public Guid TenantId => Manager.TenantId;
    }

    private async Task<World> StartAsync()
    {
        var factory = await host.StartTestAsync();
        var manager = await SupportTicketScenario.StartAsync(factory);
        var subject = Guid.CreateVersion7().ToString();
        var taskOnlyMember = await SeedCallerMembershipAsync(factory, "Cora Cajera", "cashier", manager.Tenant, subject);
        var advisor = await SeedMemberAsync(
            factory, $"asesor-{Guid.CreateVersion7():N}@example.com", "Aldo Asesor", "advisor", tenantId: manager.Tenant);
        return new World(manager, CreateClient(factory, subject, manager.Tenant, SupportPermissions.TaskUse), taskOnlyMember, advisor);
    }

    private static async Task<SupportTicket> SeedTaskAsync(World world, Guid creator, Guid assignee, DateOnly? dueOn = null) =>
        await SupportTicketSeed.SaveInternalTicketAsync(world.Factory, world.TenantId, creator, assignee, dueOn, ClockedSupportHost.Reception);

    private static Task<HttpResponseMessage> PutDueDateAsync(HttpClient client, string url, Guid ticketId, string? dueOn, long? version) =>
        PutAsync(client, $"{url}/tickets/{ticketId}/due-date", new { dueOn }, version);

    private static async Task AssertCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await BodyAsync(response);
        Assert.True(response.StatusCode == status, body);
        Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    private static async Task<long> CountEventsAsync(QepApiFactory factory, Guid ticketId, string kind)
    {
        var connectionString = factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM support.ticket_events WHERE ticket_id = @id AND kind = @kind", connection);
        command.Parameters.AddWithValue("id", ticketId);
        command.Parameters.AddWithValue("kind", kind);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task TheCreatorChangesTheDueDate()
    {
        var world = await StartAsync();
        var task = await SeedTaskAsync(world, world.Manager.CallerMemberId, world.AdvisorMember, FarDate);

        var response = await PutDueDateAsync(world.Manager.Client, world.Url, task.Id.Value, "2026-11-20", task.Version);

        var body = await BodyAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        var detail = JsonDocument.Parse(body).RootElement;
        Assert.Equal("2026-11-20", detail.GetProperty("clock").GetProperty("dueOn").GetString());
        var change = detail.GetProperty("events").EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "DueDateChanged");
        Assert.Equal("2030-01-01", change.GetProperty("fromDueOn").GetString());
        Assert.Equal("2026-11-20", change.GetProperty("toDueOn").GetString());
        Assert.Equal(task.Version + 1, detail.GetProperty("version").GetInt64());
        Assert.Equal(
            1, await CountAuditEntriesAsync(world.Factory, SupportAuditActions.TicketDueDateChanged, task.Id.ToString()));
    }

    // La traza pasa por el CHECK de kind que Task 3 ensanchó: guardar el evento por SQL es lo que lo ejerce.
    [Fact]
    public async Task ADueDateChangeIsSavedWithItsEventKind()
    {
        var world = await StartAsync();
        var task = await SeedTaskAsync(world, world.Manager.CallerMemberId, world.AdvisorMember);

        await SupportTicketSeed.UpdateTicketAsync(
            world.Factory,
            world.TenantId,
            task.Id,
            ticket => ticket.ChangeDueOn(FarDate, world.Manager.CallerMemberId, ClockedSupportHost.Reception, SupportTicketSeed.Clock));

        Assert.Equal(1, await CountEventsAsync(world.Factory, task.Id.Value, nameof(SupportTicketEventKind.DueDateChanged)));
        var saved = await SupportTicketSeed.FindTicketAsync(world.Factory, world.TenantId, task.Id);
        Assert.Equal(FarDate, saved.DueOn);
    }

    [Fact]
    public async Task TheAssigneeChangesItToo()
    {
        var world = await StartAsync();
        var task = await SeedTaskAsync(world, world.Manager.CallerMemberId, world.TaskOnlyMember);

        var response = await PutDueDateAsync(world.TaskOnly, world.Url, task.Id.Value, "2026-11-27", task.Version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("2026-11-27", (await JsonAsync(response)).GetProperty("clock").GetProperty("dueOn").GetString());
    }

    [Fact]
    public async Task ClearingTheDueDateLeavesNone()
    {
        var world = await StartAsync();
        var task = await SeedTaskAsync(world, world.Manager.CallerMemberId, world.AdvisorMember, FarDate);

        var response = await PutDueDateAsync(world.Manager.Client, world.Url, task.Id.Value, null, task.Version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var clock = (await JsonAsync(response)).GetProperty("clock");
        Assert.Equal(JsonValueKind.Null, clock.GetProperty("dueOn").ValueKind);
        Assert.Equal("NoTerm", clock.GetProperty("dueState").GetString());
    }

    [Fact]
    public async Task ADueDateInThePastIsRejected()
    {
        var world = await StartAsync();
        var task = await SeedTaskAsync(world, world.Manager.CallerMemberId, world.AdvisorMember);

        var past = await PutDueDateAsync(world.Manager.Client, world.Url, task.Id.Value, "2026-11-12", task.Version);
        var today = await PutDueDateAsync(world.Manager.Client, world.Url, task.Id.Value, "2026-11-13", task.Version);

        await AssertCodeAsync(past, HttpStatusCode.UnprocessableEntity, "support.ticket.internal_due_in_past");
        Assert.Equal(HttpStatusCode.OK, today.StatusCode);
    }

    [Fact]
    public async Task AResolvedTaskKeepsItsDueDate()
    {
        var world = await StartAsync();
        var task = await SeedTaskAsync(world, world.Manager.CallerMemberId, world.AdvisorMember, FarDate);
        var opened = await JsonAsync(await world.Manager.ActAsync(task.Id.Value, "status", new { status = "Open" }, task.Version));
        var resolved = await JsonAsync(
            await world.Manager.ActAsync(task.Id.Value, "status", new { status = "Resolved" }, SupportTicketScenario.Version(opened)));

        var response = await PutDueDateAsync(
            world.Manager.Client, world.Url, task.Id.Value, "2026-11-20", SupportTicketScenario.Version(resolved));

        await AssertCodeAsync(response, HttpStatusCode.UnprocessableEntity, "support.ticket.not_editable");
        var detail = await world.Manager.DetailAsync(task.Id.Value);
        Assert.Equal("2030-01-01", detail.GetProperty("clock").GetProperty("dueOn").GetString());
    }

    [Fact]
    public async Task AnExternalTicketHasNoEditableDueDate()
    {
        var world = await StartAsync();
        var external = await world.Manager.CreateAsync();

        var response = await PutDueDateAsync(
            world.Manager.Client, world.Url, SupportTicketScenario.Id(external), "2026-11-20", SupportTicketScenario.Version(external));

        await AssertCodeAsync(response, HttpStatusCode.UnprocessableEntity, "support.ticket.not_internal");
    }

    [Fact]
    public async Task WithoutIfMatchIs428AndStaleIs412()
    {
        var world = await StartAsync();
        var task = await SeedTaskAsync(world, world.Manager.CallerMemberId, world.AdvisorMember);

        var missing = await PutDueDateAsync(world.Manager.Client, world.Url, task.Id.Value, "2026-11-20", version: null);
        var stale = await PutDueDateAsync(world.Manager.Client, world.Url, task.Id.Value, "2026-11-20", task.Version + 5);

        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task SomeoneElsesTaskIs404()
    {
        var world = await StartAsync();
        var foreign = await SeedTaskAsync(world, world.Manager.CallerMemberId, world.AdvisorMember);

        var response = await PutDueDateAsync(world.TaskOnly, world.Url, foreign.Id.Value, "2026-11-20", foreign.Version);

        await AssertCodeAsync(response, HttpStatusCode.NotFound, "support.ticket.not_found");
    }

    // No oracle: a stale version on someone else's task is still 404, never 412.
    [Fact]
    public async Task SomeoneElsesTaskWithAStaleVersionIs404NotAPreconditionFailure()
    {
        var world = await StartAsync();
        var foreign = await SeedTaskAsync(world, world.Manager.CallerMemberId, world.AdvisorMember);

        var response = await PutDueDateAsync(world.TaskOnly, world.Url, foreign.Id.Value, "2026-11-20", foreign.Version + 5);

        await AssertCodeAsync(response, HttpStatusCode.NotFound, "support.ticket.not_found");
    }
}
