using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modules.Support.Application;
using Npgsql;
using static Modules.Support.IntegrationTests.SupportApiHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Spec 2026-11-22 §5.2 (Task 10): lo que una tarea no puede hacer de lo que ya existía (cliente, respuestas, PIN, prórroga, esperar al
/// cliente), el tipo interno cerrado a toda otra entrada y la regla de responsable de una tarea. Host con reloj propio; cada prueba
/// siembra su tenant.
/// </summary>
public sealed class SupportTaskRulesApiTests(ClockedSupportHost host) : IClassFixture<ClockedSupportHost>
{
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

    // La tarea del manager, asignada a quien se pase, sembrada por el dominio.
    private static async Task<(Guid Id, long Version)> SeedTaskAsync(World world, Guid assignee, Guid? creator = null)
    {
        var task = await SupportTicketSeed.SaveInternalTicketAsync(world.Factory, world.TenantId, creator ?? world.Manager.CallerMemberId, assignee);
        return (task.Id.Value, task.Version);
    }

    private static async Task<Guid> InternalTypeIdAsync(World world) =>
        (await JsonAsync(await world.Manager.Client.GetAsync($"{world.Url}/ticket-types", Ct)))
        .EnumerateArray().Single(type => type.GetProperty("isInternal").GetBoolean()).GetProperty("id").GetGuid();

    private static async Task AssertCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await BodyAsync(response);
        Assert.True(response.StatusCode == status, body);
        Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    private static async Task<long> ScalarAsync(QepApiFactory factory, string sql, Guid id)
    {
        var connectionString = factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static Task<long> OutboxCountAsync(QepApiFactory factory, Guid ticketId) => ScalarAsync(
        factory,
        "SELECT count(*) FROM platform.outbox_messages WHERE event_name = 'support.ticket.task-assigned.v1' AND correlation_id = @id::text",
        ticketId);

    [Fact]
    public async Task TheInternalTypeIsLockedOutOfEveryOtherEntry()
    {
        var world = await StartAsync();
        var internalType = await InternalTypeIdAsync(world);
        var external = await world.Manager.CreateAsync();
        var externalTypeId = await world.Manager.TypeIdAsync("Consulta");
        var task = await SeedTaskAsync(world, world.AdvisorMember);
        var conversation = await SupportMessagingSeed.SeedConversationAsync(world.Factory, world.TenantId);

        var viaTickets = await world.Manager.PostAsync(SupportTicketScenario.Body(internalType));
        var viaConversation = await world.Manager.Client.PostAsJsonAsync(
            $"{world.Url}/conversations/{conversation}/tickets",
            new { ticketTypeId = internalType, subject = "Pedido sin llegar", description = "El pedido no ha llegado." },
            Ct);
        var externalToInternal = await world.Manager.ActAsync(
            SupportTicketScenario.Id(external), "type", new { ticketTypeId = internalType }, SupportTicketScenario.Version(external));
        var taskToExternal = await world.Manager.ActAsync(task.Id, "type", new { ticketTypeId = externalTypeId }, task.Version);

        await AssertCodeAsync(viaTickets, HttpStatusCode.UnprocessableEntity, "support.ticket.internal_type_locked");
        await AssertCodeAsync(viaConversation, HttpStatusCode.UnprocessableEntity, "support.ticket.internal_type_locked");
        await AssertCodeAsync(externalToInternal, HttpStatusCode.UnprocessableEntity, "support.ticket.internal_type_locked");
        await AssertCodeAsync(taskToExternal, HttpStatusCode.UnprocessableEntity, "support.ticket.internal_type_locked");
    }

    [Fact]
    public async Task ExternalOnlyActionsRefuseATask()
    {
        var world = await StartAsync();
        var task = await SeedTaskAsync(world, world.AdvisorMember);
        var url = $"{world.Url}/tickets/{task.Id}";

        var reply = await world.Manager.PostActionAsync(
            $"tickets/{task.Id}/replies", new { clientId = Guid.CreateVersion7(), channel = "Email", text = "Ya revisamos." }, task.Version);
        var options = await world.Manager.Client.GetAsync($"{url}/reply-options", Ct);
        var pin = await world.Manager.PostActionAsync($"tickets/{task.Id}/pin", null, task.Version);
        var customer = await world.Manager.ActAsync(task.Id, "customer", new { customerId = Guid.CreateVersion7() }, task.Version);
        var follow = await world.Manager.ActAsync(task.Id, "follow-thread", new { follow = true }, task.Version);
        var extension = await world.Manager.PostActionAsync(
            $"tickets/{task.Id}/extension", new { businessDays = 3, reason = "Falta un dato." }, task.Version);

        foreach (var response in new[] { reply, options, pin, customer, follow })
        {
            await AssertCodeAsync(response, HttpStatusCode.UnprocessableEntity, "support.ticket.internal_no_reply");
        }

        // Una tarea no tiene término: la prórroga ya respondía «sin término» (D13).
        await AssertCodeAsync(extension, HttpStatusCode.UnprocessableEntity, "support.ticket.extension_no_term");
    }

    [Fact]
    public async Task ATaskCannotWaitOnTheCustomer()
    {
        var world = await StartAsync();
        var task = await SeedTaskAsync(world, world.AdvisorMember);

        var response = await world.Manager.ActAsync(task.Id, "status", new { status = "WaitingOnCustomer" }, task.Version);

        await AssertCodeAsync(response, HttpStatusCode.UnprocessableEntity, "support.ticket.transition_invalid");
    }

    [Fact]
    public async Task ResolvingATaskRequestsNoSurvey()
    {
        var world = await StartAsync();
        var task = await SeedTaskAsync(world, world.AdvisorMember);
        var opened = await world.Manager.ActAsync(task.Id, "status", new { status = "Open" }, task.Version);
        var openedDetail = await JsonAsync(opened);

        var resolved = await world.Manager.ActAsync(task.Id, "status", new { status = "Resolved" }, SupportTicketScenario.Version(openedDetail));

        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        Assert.Equal("Resolved", (await JsonAsync(resolved)).GetProperty("status").GetString());
        Assert.Equal(0, await ScalarAsync(world.Factory, "SELECT count(*) FROM support.ticket_surveys WHERE ticket_id = @id", task.Id));
    }

    [Fact]
    public async Task ReassigningATaskToSomeoneElseWritesTheEvent()
    {
        var world = await StartAsync();
        var task = await SeedTaskAsync(world, world.TaskOnlyMember);

        var response = await world.Manager.ActAsync(task.Id, "assignee", new { assigneeMemberId = world.AdvisorMember }, task.Version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await OutboxCountAsync(world.Factory, task.Id));
        // Dejárselo a quien actúa no avisa a nadie.
        var detail = await JsonAsync(response);
        var self = await world.Manager.ActAsync(
            task.Id, "assignee", new { assigneeMemberId = world.Manager.CallerMemberId }, SupportTicketScenario.Version(detail));
        Assert.Equal(HttpStatusCode.OK, self.StatusCode);
        Assert.Equal(1, await OutboxCountAsync(world.Factory, task.Id));
    }

    // Spec §5.2.1: a task always has an assignee, so the assignee route cannot clear it (a task is not an external ticket).
    [Fact]
    public async Task ATaskCannotBeLeftWithoutAnAssignee()
    {
        var world = await StartAsync();
        var task = await SeedTaskAsync(world, world.AdvisorMember);

        var response = await world.Manager.ActAsync(task.Id, "assignee", new { assigneeMemberId = (Guid?)null }, task.Version);

        await AssertCodeAsync(response, HttpStatusCode.UnprocessableEntity, "support.ticket.assignee_invalid");
    }

    [Fact]
    public async Task TakingATaskWritesNoEvent()
    {
        var world = await StartAsync();
        var task = await SeedTaskAsync(world, world.AdvisorMember, creator: world.TaskOnlyMember);

        var taken = await SendTakeAsync(world.TaskOnly, world.Url, task.Id, task.Version);
        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
        Assert.Equal(world.TaskOnlyMember, (await JsonAsync(taken)).GetProperty("assignee").GetProperty("id").GetGuid());
        Assert.Equal(0, await OutboxCountAsync(world.Factory, task.Id));
    }

    private static async Task<HttpResponseMessage> SendTakeAsync(HttpClient client, string url, Guid ticketId, long version)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{url}/tickets/{ticketId}/take");
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return await client.SendAsync(request, Ct);
    }

    // D19: la tarea la puede recibir quien sólo usa tareas; un externo sigue pidiendo gestionar.
    [Fact]
    public async Task ATaskCanGoToATaskOnlyMember()
    {
        var world = await StartAsync();
        var task = await SeedTaskAsync(world, world.AdvisorMember);

        var response = await world.Manager.ActAsync(task.Id, "assignee", new { assigneeMemberId = world.TaskOnlyMember }, task.Version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(world.TaskOnlyMember, (await JsonAsync(response)).GetProperty("assignee").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task AnExternalTicketStillNeedsAManager()
    {
        var world = await StartAsync();
        var external = await world.Manager.CreateAsync();

        var response = await world.Manager.ActAsync(
            SupportTicketScenario.Id(external), "assignee", new { assigneeMemberId = world.TaskOnlyMember }, SupportTicketScenario.Version(external));

        await AssertCodeAsync(response, HttpStatusCode.UnprocessableEntity, "support.ticket.assignee_invalid");
    }

    [Fact]
    public async Task TheInternalTypeCannotBeUpdatedNorDeleted()
    {
        var world = await StartAsync();
        var internalType = await InternalTypeIdAsync(world);
        var settingsUrl = $"{world.Url}/settings/ticket-types/{internalType}";

        var update = await PutAsync(
            world.Manager.Client,
            settingsUrl,
            new { name = "Tarea renombrada", termBusinessDays = 15, termIsLegal = true, requiresFormalResponse = false, isPublished = true, isActive = true },
            version: 1);
        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, settingsUrl);
        deleteRequest.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        var delete = await world.Manager.Client.SendAsync(deleteRequest, Ct);

        await AssertCodeAsync(update, HttpStatusCode.UnprocessableEntity, "support.ticket.internal_type_locked");
        await AssertCodeAsync(delete, HttpStatusCode.UnprocessableEntity, "support.ticket.internal_type_locked");
    }

    // Domain rule: the internal type's own exact values are a no-op (the frontend may PUT it unchanged); only a difference is locked.
    [Fact]
    public async Task ThePutOfTheInternalTypeWithItsOwnValuesIsANoOp()
    {
        var world = await StartAsync();
        var types = (await JsonAsync(await world.Manager.Client.GetAsync($"{world.Url}/ticket-types", Ct))).EnumerateArray();
        var internalType = types.Single(type => type.GetProperty("isInternal").GetBoolean());
        var version = internalType.GetProperty("version").GetInt64();

        var response = await PutAsync(
            world.Manager.Client,
            $"{world.Url}/settings/ticket-types/{internalType.GetProperty("id").GetGuid()}",
            new
            {
                name = internalType.GetProperty("name").GetString(),
                termBusinessDays = (int?)null,
                termIsLegal = internalType.GetProperty("termIsLegal").GetBoolean(),
                requiresFormalResponse = internalType.GetProperty("requiresFormalResponse").GetBoolean(),
                isPublished = internalType.GetProperty("isPublished").GetBoolean(),
                isActive = internalType.GetProperty("isActive").GetBoolean(),
            },
            version);

        var body = await BodyAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        Assert.Equal(version, JsonDocument.Parse(body).RootElement.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task ReorderingIgnoresTheInternalType()
    {
        var world = await StartAsync();
        var types = (await JsonAsync(await world.Manager.Client.GetAsync($"{world.Url}/settings/ticket-types", Ct))).EnumerateArray().ToList();
        Assert.Equal(8, types.Count);
        var reversed = types.AsEnumerable().Reverse().Select(type => type.GetProperty("id").GetGuid()).ToList();

        var response = await PutAsync(world.Manager.Client, $"{world.Url}/settings/ticket-types/order", new { typeIds = reversed }, version: null);

        var body = await BodyAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        var ordered = JsonDocument.Parse(body).RootElement.EnumerateArray().Select(type => type.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(reversed, ordered);
    }
}
