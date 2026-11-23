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
/// Spec 2026-11-22 §5.2 (Task 9): el alta de una tarea interna por <c>POST /tasks</c>, los candidatos a responsable y el evento
/// <c>support.ticket.task-assigned.v1</c> en el outbox. Host con reloj propio: «hoy» es el viernes 13 de noviembre de 2026 en Bogotá.
/// </summary>
public sealed class SupportTasksApiTests(ClockedSupportHost host) : IClassFixture<ClockedSupportHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // El tenant de la prueba: quien gestiona (admin), una membresía sólo de tareas (cashier con support.task.use), un asesor y quien
    // sólo lee (sin membresía de tareas).
    private sealed record World(
        SupportTicketScenario Manager,
        HttpClient TaskOnly,
        Guid TaskOnlyMember,
        Guid AdvisorMember,
        HttpClient Reader)
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
        var taskOnly = CreateClient(factory, subject, manager.Tenant, SupportPermissions.TaskUse);
        var reader = CreateClient(factory, Guid.CreateVersion7().ToString(), manager.Tenant, SupportPermissions.Read);
        return new World(manager, taskOnly, taskOnlyMember, advisor, reader);
    }

    private static Task<HttpResponseMessage> PostTaskAsync(
        HttpClient client, string url, Guid? assignee, string? priority = null, string? dueOn = null, string? subject = "Cuadrar el inventario") =>
        client.PostAsJsonAsync(
            $"{url}/tasks",
            new
            {
                subject,
                description = "Revisar las diferencias de octubre.",
                assigneeMemberId = assignee,
                priority,
                dueOn,
            },
            Ct);

    private static async Task<JsonElement> CreatedAsync(HttpResponseMessage response)
    {
        var body = await BodyAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<List<string>> OutboxPayloadsAsync(QepApiFactory factory, Guid ticketId)
    {
        var connectionString = factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT event_name || '|' || payload::text FROM platform.outbox_messages WHERE correlation_id = @id AND event_name LIKE 'support.%'",
            connection);
        command.Parameters.AddWithValue("id", ticketId.ToString("D"));
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private static async Task<long> ConsultAccessRowsAsync(QepApiFactory factory, Guid ticketId)
    {
        var connectionString = factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM support.ticket_access WHERE ticket_id = @id", connection);
        command.Parameters.AddWithValue("id", ticketId);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task ATaskOnlyMemberCreatesATaskForAnotherMember()
    {
        var world = await StartAsync();

        var response = await PostTaskAsync(world.TaskOnly, world.Url, world.AdvisorMember, priority: "High", dueOn: "2026-11-20");

        var created = await CreatedAsync(response);
        Assert.True(created.GetProperty("isInternal").GetBoolean());
        Assert.Equal("Tarea interna", created.GetProperty("type").GetProperty("name").GetString());
        Assert.Equal("Internal", created.GetProperty("intake").GetString());
        Assert.Equal("High", created.GetProperty("priority").GetString());
        Assert.Equal("2026-11-20", created.GetProperty("clock").GetProperty("dueOn").GetString());
        Assert.Equal(world.AdvisorMember, created.GetProperty("assignee").GetProperty("id").GetGuid());
        var createdBy = created.GetProperty("createdBy");
        Assert.Equal(world.TaskOnlyMember, createdBy.GetProperty("id").GetGuid());
        Assert.True(createdBy.GetProperty("isMe").GetBoolean());
        Assert.Equal(
            $"{world.Url}/tickets/{created.GetProperty("id").GetGuid()}",
            response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task ATaskCanBeAssignedToItsCreator()
    {
        var world = await StartAsync();

        var created = await CreatedAsync(await PostTaskAsync(world.TaskOnly, world.Url, world.TaskOnlyMember));

        Assert.Equal(world.TaskOnlyMember, created.GetProperty("assignee").GetProperty("id").GetGuid());
        Assert.Equal(world.TaskOnlyMember, created.GetProperty("createdBy").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task TheAssigneeIsRequired()
    {
        var world = await StartAsync();

        var response = await PostTaskAsync(world.TaskOnly, world.Url, assignee: null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
        Assert.Equal(["AssigneeMemberId"], await ErrorKeysAsync(response));
    }

    [Fact]
    public async Task ARemovedOrForeignAssigneeIsInvalid()
    {
        var world = await StartAsync();
        var removed = await SeedMemberAsync(
            world.Factory, $"quitada-{Guid.CreateVersion7():N}@example.com", "Quitada", "cashier", removed: true, tenantId: world.Manager.Tenant);
        var otherTenant = NewTenantId();
        await SeedTenantAsync(world.Factory, otherTenant);
        var foreign = await SeedMemberAsync(
            world.Factory, $"otra-{Guid.CreateVersion7():N}@example.com", "De otra empresa", "cashier", tenantId: otherTenant);

        foreach (var assignee in new[] { removed, foreign, Guid.CreateVersion7() })
        {
            var response = await PostTaskAsync(world.TaskOnly, world.Url, assignee);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("support.ticket.assignee_invalid", await CodeOfAsync(response));
        }
    }

    [Fact]
    public async Task ADueDateBeforeTodayIsRejected()
    {
        var world = await StartAsync();

        var past = await PostTaskAsync(world.TaskOnly, world.Url, world.AdvisorMember, dueOn: "2026-11-12");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, past.StatusCode);
        Assert.Equal("support.ticket.internal_due_in_past", await CodeOfAsync(past));

        // «Hoy» es el 13 en Bogotá (10:00 locales): ese mismo día vale.
        var today = await PostTaskAsync(world.TaskOnly, world.Url, world.AdvisorMember, dueOn: "2026-11-13");
        Assert.Equal(HttpStatusCode.Created, today.StatusCode);
    }

    [Fact]
    public async Task AMalformedDueDateIs422OnItsField()
    {
        var world = await StartAsync();

        var response = await PostTaskAsync(world.TaskOnly, world.Url, world.AdvisorMember, dueOn: "13/11/2026");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
        Assert.Equal(["DueOn"], await ErrorKeysAsync(response));
    }

    [Fact]
    public async Task ATaskTakesTheNextNumberOfTheTenant()
    {
        var world = await StartAsync();
        var external = await world.Manager.CreateAsync();
        Assert.Equal("SOP-000001", external.GetProperty("number").GetString());

        var created = await CreatedAsync(await PostTaskAsync(world.TaskOnly, world.Url, world.AdvisorMember));

        Assert.Equal("SOP-000002", created.GetProperty("number").GetString());
    }

    [Fact]
    public async Task ATaskHasNoConsultAccess()
    {
        var world = await StartAsync();

        var created = await CreatedAsync(await PostTaskAsync(world.TaskOnly, world.Url, world.AdvisorMember));

        Assert.Equal(JsonValueKind.Null, created.GetProperty("consultAccess").ValueKind);
        Assert.Equal(JsonValueKind.Null, created.GetProperty("issuedPin").ValueKind);
        Assert.Equal(0, await ConsultAccessRowsAsync(world.Factory, created.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task CreatingATaskIsAudited()
    {
        var world = await StartAsync();

        var created = await CreatedAsync(await PostTaskAsync(world.TaskOnly, world.Url, world.AdvisorMember));

        Assert.Equal(
            1, await CountAuditEntriesAsync(world.Factory, SupportAuditActions.TicketCreated, created.GetProperty("id").GetString()!));
    }

    [Fact]
    public async Task AssigningATaskToSomeoneElseWritesTheEvent()
    {
        var world = await StartAsync();

        var created = await CreatedAsync(await PostTaskAsync(world.TaskOnly, world.Url, world.AdvisorMember));

        var id = created.GetProperty("id").GetGuid();
        var row = Assert.Single(await OutboxPayloadsAsync(world.Factory, id));
        var (name, payload) = (row.Split('|', 2)[0], JsonDocument.Parse(row.Split('|', 2)[1]).RootElement);
        Assert.Equal("support.ticket.task-assigned.v1", name);
        Assert.Equal(id, payload.GetProperty("ticketId").GetGuid());
        Assert.Equal(world.TenantId, payload.GetProperty("tenantId").GetGuid());
        Assert.Equal(created.GetProperty("number").GetString(), payload.GetProperty("ticketNumber").GetString());
        Assert.Equal(world.AdvisorMember, payload.GetProperty("assigneeMemberId").GetGuid());
        Assert.Equal(world.TaskOnlyMember, payload.GetProperty("assignedByMemberId").GetGuid());
    }

    [Fact]
    public async Task CreatingATaskForYourselfWritesNoEvent()
    {
        var world = await StartAsync();

        var created = await CreatedAsync(await PostTaskAsync(world.TaskOnly, world.Url, world.TaskOnlyMember));

        Assert.Empty(await OutboxPayloadsAsync(world.Factory, created.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task AReaderCannotCreateATask()
    {
        var world = await StartAsync();

        var response = await PostTaskAsync(world.Reader, world.Url, world.AdvisorMember);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TheTaskAssigneesListTaskUsersAndManagers()
    {
        var world = await StartAsync();

        var response = await world.TaskOnly.GetAsync($"{world.Url}/tasks/assignees", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await JsonAsync(response)).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(items, item => item.GetProperty("id").GetGuid() == world.TaskOnlyMember && item.GetProperty("isMe").GetBoolean());
        Assert.Contains(items, item => item.GetProperty("id").GetGuid() == world.AdvisorMember && !item.GetProperty("isMe").GetBoolean());
        Assert.Contains(items, item => item.GetProperty("id").GetGuid() == world.Manager.CallerMemberId);
        Assert.Equal(
            items.Select(item => item.GetProperty("displayName").GetString()).OrderBy(name => name, StringComparer.InvariantCultureIgnoreCase),
            items.Select(item => item.GetProperty("displayName").GetString()));
    }

    [Fact]
    public async Task AReaderCannotListTaskAssignees()
    {
        var world = await StartAsync();

        var response = await world.Reader.GetAsync($"{world.Url}/tasks/assignees", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
