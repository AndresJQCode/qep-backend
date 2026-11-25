using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Support.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Domain;
using static Modules.Support.IntegrationTests.SupportApiHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Spec 2026-11-22 §5.6 (Block 2, Task 6): quien sólo tiene <c>support.task.use</c> no llega nunca a un ticket de cliente, por ninguna
/// ruta, y trabaja sus tareas (las que creó o le asignaron). Un externo, una tarea ajena y un id que no existe responden el mismo 404, y
/// una versión vieja sobre algo ajeno es 404 y no 412. Una sola puerta, <see cref="SupportTicketScope"/>. Un host por clase; cada prueba
/// siembra su tenant.
/// </summary>
public sealed class SupportTaskAccessApiTests(SharedSupportHost sharedHost) : IClassFixture<SharedSupportHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // El tenant de la prueba: quien gestiona (rol admin, con su cliente de lectura y gestión), una membresía sólo de tareas (rol cashier
    // y el permiso support.task.use por X-Permissions) y un asesor al que se le pueden pasar tareas.
    private sealed record World(
        SupportTicketScenario Manager,
        HttpClient TaskOnly,
        Guid TaskOnlyMember,
        Guid AdvisorMember,
        string TaskOnlySubject)
    {
        public QepApiFactory Factory => Manager.Factory;

        public string Url => Manager.Url;

        public Guid TenantId => Manager.TenantId;
    }

    private async Task<World> StartAsync()
    {
        var factory = await sharedHost.StartTestAsync();
        var manager = await SupportTicketScenario.StartAsync(factory);
        var subject = Guid.CreateVersion7().ToString();
        var taskOnlyMember = await SeedCallerMembershipAsync(factory, "Cora Cajera", "cashier", manager.Tenant, subject);
        var advisor = await SeedMemberAsync(factory, $"asesor-{Guid.CreateVersion7():N}@example.com", "Aldo Asesor", "advisor", tenantId: manager.Tenant);
        var client = CreateClient(factory, subject, manager.Tenant, SupportPermissions.TaskUse);
        return new World(manager, client, taskOnlyMember, advisor, subject);
    }

    // La tarea la crea quien se pasa y la trabaja quien se pasa: «participantes» son creador y responsable.
    private static async Task<Guid> SeedTaskAsync(World world, Guid creator, Guid assignee) =>
        (await SupportTicketSeed.SaveInternalTicketAsync(world.Factory, world.TenantId, creator, assignee)).Id.Value;

    private static async Task<long> VersionAsync(World world, Guid ticketId)
    {
        var response = await world.Manager.Client.GetAsync($"{world.Url}/tickets/{ticketId}", Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return (await JsonAsync(response)).GetProperty("version").GetInt64();
    }

    private static Task<HttpResponseMessage> PostNoteAsync(HttpClient client, string url, Guid ticketId) =>
        client.PostAsJsonAsync($"{url}/tickets/{ticketId}/notes", new { clientId = Guid.CreateVersion7(), text = "Revisé el inventario." }, Ct);

    private static void AssertTicketNotFound(HttpResponseMessage response, string body, string? route = null)
    {
        Assert.True(response.StatusCode == HttpStatusCode.NotFound, route is null ? body : $"{route}: {body}");
        Assert.Equal("support.ticket.not_found", System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ATaskOnlyMemberNeverSeesAnExternalTicket()
    {
        var world = await StartAsync();
        var external = SupportTicketScenario.Id(await world.Manager.CreateAsync());
        var noteId = Guid.CreateVersion7();
        var fileId = Guid.CreateVersion7();
        var version = await VersionAsync(world, external);

        var responses = new Dictionary<string, HttpResponseMessage>
        {
            ["detail"] = await world.TaskOnly.GetAsync($"{world.Url}/tickets/{external}", Ct),
            ["notes"] = await world.TaskOnly.GetAsync($"{world.Url}/tickets/{external}/notes", Ct),
            ["note file"] = await world.TaskOnly.GetAsync($"{world.Url}/tickets/{external}/notes/{noteId}/files/{fileId}/download", Ct),
            ["post note"] = await PostNoteAsync(world.TaskOnly, world.Url, external),
            ["status"] = await PutAsync(world.TaskOnly, $"{world.Url}/tickets/{external}/status", new { status = "Open" }, version),
            ["priority"] = await PutAsync(world.TaskOnly, $"{world.Url}/tickets/{external}/priority", new { priority = "High" }, version),
        };

        foreach (var (route, response) in responses)
        {
            var body = await BodyAsync(response);
            AssertTicketNotFound(response, body, route);
        }
    }

    [Fact]
    public async Task TheSame404ForUnknownExternalAndForeignTask()
    {
        var world = await StartAsync();
        var external = SupportTicketScenario.Id(await world.Manager.CreateAsync());
        var foreign = await SeedTaskAsync(world, world.Manager.CallerMemberId, world.AdvisorMember);
        var unknown = Guid.CreateVersion7();

        var bodies = new List<string>();
        foreach (var id in new[] { unknown, external, foreign })
        {
            var response = await world.TaskOnly.GetAsync($"{world.Url}/tickets/{id}", Ct);
            var body = await BodyAsync(response);
            AssertTicketNotFound(response, body);
            // Sólo el id del mensaje cambia; el traceId (distinto en cada request) y el instance (la ruta que mandó quien llama) no cuentan.
            using var document = JsonDocument.Parse(body);
            bodies.Add(string.Join(
                "|",
                document.RootElement.EnumerateObject()
                    .Where(property => property.Name is not ("traceId" or "instance"))
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .Select(property => $"{property.Name}={property.Value.ToString().Replace(id.ToString(), "<id>", StringComparison.OrdinalIgnoreCase)}")));
        }

        Assert.Single(bodies.Distinct());
    }

    [Fact]
    public async Task AStaleVersionOnSomeoneElsesTaskIs404Not412()
    {
        var world = await StartAsync();
        var foreign = await SeedTaskAsync(world, world.Manager.CallerMemberId, world.AdvisorMember);
        var own = await SeedTaskAsync(world, world.TaskOnlyMember, world.AdvisorMember);

        var stale = await PutAsync(world.TaskOnly, $"{world.Url}/tickets/{foreign}/priority", new { priority = "High" }, 999);
        AssertTicketNotFound(stale, await BodyAsync(stale));

        // Sobre la propia, la misma versión vieja sí es un 412.
        var mine = await PutAsync(world.TaskOnly, $"{world.Url}/tickets/{own}/priority", new { priority = "High" }, 999);
        Assert.Equal(HttpStatusCode.PreconditionFailed, mine.StatusCode);
    }

    [Fact]
    public async Task ATaskOnlyMemberWorksHerTasks()
    {
        var world = await StartAsync();
        var asCreator = await SeedTaskAsync(world, world.TaskOnlyMember, world.AdvisorMember);
        var asAssignee = await SeedTaskAsync(world, world.Manager.CallerMemberId, world.TaskOnlyMember);

        foreach (var task in new[] { asCreator, asAssignee })
        {
            var detail = await world.TaskOnly.GetAsync($"{world.Url}/tickets/{task}", Ct);
            Assert.True(detail.StatusCode == HttpStatusCode.OK, await BodyAsync(detail));

            var priority = await PutAsync(
                world.TaskOnly, $"{world.Url}/tickets/{task}/priority", new { priority = "High" },
                (await JsonAsync(detail)).GetProperty("version").GetInt64());
            Assert.True(priority.StatusCode == HttpStatusCode.OK, await BodyAsync(priority));

            var note = await PostNoteAsync(world.TaskOnly, world.Url, task);
            Assert.True(note.StatusCode == HttpStatusCode.Created, await BodyAsync(note));
        }
    }

    [Fact]
    public async Task AReaderSeesEveryTaskButCannotAct()
    {
        var world = await StartAsync();
        var task = await SeedTaskAsync(world, world.Manager.CallerMemberId, world.AdvisorMember);
        using var reader = CreateReader(world.Factory, world.Manager.Tenant);

        var detail = await reader.GetAsync($"{world.Url}/tickets/{task}", Ct);
        var priority = await PutAsync(reader, $"{world.Url}/tickets/{task}/priority", new { priority = "High" }, 1);

        Assert.True(detail.StatusCode == HttpStatusCode.OK, await BodyAsync(detail));
        // La política (manage o task.use) corta antes del handler: 403 pelado, sin código.
        Assert.Equal(HttpStatusCode.Forbidden, priority.StatusCode);
        Assert.True(string.IsNullOrWhiteSpace(await BodyAsync(priority)) || !(await JsonAsync(priority)).TryGetProperty("code", out _));
    }

    [Fact]
    public async Task AReaderWithTaskUseGets403OnAnExternalAction()
    {
        var world = await StartAsync();
        var external = await world.Manager.CreateAsync();
        var subject = Guid.CreateVersion7().ToString();
        await SeedCallerMembershipAsync(world.Factory, "Rita Lectora", "cashier", world.Manager.Tenant, subject);
        using var client = CreateClient(world.Factory, subject, world.Manager.Tenant, SupportPermissions.Read, SupportPermissions.TaskUse);

        // Lo ve (no hay nada que ocultar), pero no actúa: 403 con código, no 404.
        var detail = await client.GetAsync($"{world.Url}/tickets/{SupportTicketScenario.Id(external)}", Ct);
        var priority = await PutAsync(
            client, $"{world.Url}/tickets/{SupportTicketScenario.Id(external)}/priority", new { priority = "High" },
            SupportTicketScenario.Version(external));

        Assert.True(detail.StatusCode == HttpStatusCode.OK, await BodyAsync(detail));
        Assert.Equal(HttpStatusCode.Forbidden, priority.StatusCode);
        Assert.Equal("authorization.denied", await CodeOfAsync(priority));
    }

    // Review Focus 4: quien pierde task.use conserva la tarea sólo en lectura. Por HTTP el acceso lo deciden los claims del cliente, no la
    // base: por eso el cliente trae únicamente support.ticket.read.
    [Fact]
    public async Task ACreatorWhoLostTaskUseCanOnlyReadHerTask()
    {
        var world = await StartAsync();
        var task = await SeedTaskAsync(world, world.TaskOnlyMember, world.TaskOnlyMember);

        using var client = CreateClient(world.Factory, world.TaskOnlySubject, world.Manager.Tenant, SupportPermissions.Read);
        var detail = await client.GetAsync($"{world.Url}/tickets/{task}", Ct);
        // Con sólo el claim de lectura la ve (nivel View) y no actúa: el 403 lo da la política de ruta TicketAct, antes del handler.
        Assert.True(detail.StatusCode == HttpStatusCode.OK, await BodyAsync(detail));
        var note = await PostNoteAsync(client, world.Url, task);
        Assert.Equal(HttpStatusCode.Forbidden, note.StatusCode);

        // Sin ningún permiso de Soporte en el claim, la política corta antes del handler.
        using var none = CreateClient(world.Factory, world.TaskOnlySubject, world.Manager.Tenant, "tenancy.settings.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await none.GetAsync($"{world.Url}/tickets/{task}", Ct)).StatusCode);
    }

    [Fact]
    public async Task ReplyOptionsOnATaskIs422InternalNoReply()
    {
        var world = await StartAsync();
        var task = await SeedTaskAsync(world, world.Manager.CallerMemberId, world.AdvisorMember);

        var response = await world.Manager.Client.GetAsync($"{world.Url}/tickets/{task}/reply-options", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("support.ticket.internal_no_reply", await CodeOfAsync(response));
    }

    [Fact]
    public async Task AManagerSeesAndActsOnEveryTask()
    {
        var world = await StartAsync();
        var task = await SeedTaskAsync(world, world.TaskOnlyMember, world.AdvisorMember);

        var detail = await world.Manager.Client.GetAsync($"{world.Url}/tickets/{task}", Ct);
        Assert.True(detail.StatusCode == HttpStatusCode.OK, await BodyAsync(detail));
        var priority = await PutAsync(
            world.Manager.Client, $"{world.Url}/tickets/{task}/priority", new { priority = "Urgent" },
            (await JsonAsync(detail)).GetProperty("version").GetInt64());

        Assert.True(priority.StatusCode == HttpStatusCode.OK, await BodyAsync(priority));
    }

    [Fact]
    public async Task HandingTheTaskOverRemovesTheFormerAssigneesAccess()
    {
        var world = await StartAsync();
        // Responsable pero no creadora: al pasársela a otro pierde el acceso al instante.
        var task = await SeedTaskAsync(world, world.Manager.CallerMemberId, world.TaskOnlyMember);

        var handOver = await PutAsync(
            world.TaskOnly, $"{world.Url}/tickets/{task}/assignee", new { assigneeMemberId = world.AdvisorMember },
            await VersionAsync(world, task));
        Assert.True(handOver.StatusCode == HttpStatusCode.OK, await BodyAsync(handOver));

        var after = await world.TaskOnly.GetAsync($"{world.Url}/tickets/{task}", Ct);
        AssertTicketNotFound(after, await BodyAsync(after));
    }

    [Fact]
    public async Task ExternalOnlyRoutesStay403ForATaskOnlyMember()
    {
        var world = await StartAsync();
        var external = SupportTicketScenario.Id(await world.Manager.CreateAsync());
        var task = await SeedTaskAsync(world, world.TaskOnlyMember, world.AdvisorMember);
        var typeId = await world.Manager.TypeIdAsync("Petición general");

        foreach (var ticket in new[] { external, task })
        {
            var reply = await world.TaskOnly.PostAsJsonAsync(
                $"{world.Url}/tickets/{ticket}/replies", new { clientId = Guid.CreateVersion7(), channel = "Email", text = "Hola" }, Ct);
            var type = await PutAsync(world.TaskOnly, $"{world.Url}/tickets/{ticket}/type", new { ticketTypeId = typeId }, 1);
            var messages = await world.TaskOnly.GetAsync($"{world.Url}/tickets/{ticket}/messages", Ct);

            Assert.Equal(HttpStatusCode.Forbidden, reply.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, type.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, messages.StatusCode);
        }

        var report = await world.TaskOnly.GetAsync($"{world.Url}/reports/summary", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, report.StatusCode);
    }

    [Fact]
    public async Task WithoutTheModuleATaskOnlyMemberGets403()
    {
        var factory = await sharedHost.StartTestAsync();
        var scenario = await SupportTicketScenario.StartAsync(factory, TenantModuleKeys.All.Where(key => key != TenantModuleKeys.Support));
        var subject = Guid.CreateVersion7().ToString();
        await SeedCallerMembershipAsync(factory, "Cora Cajera", "cashier", scenario.Tenant, subject);
        using var client = CreateClient(factory, subject, scenario.Tenant, SupportPermissions.TaskUse);

        var response = await client.GetAsync($"{scenario.Url}/tickets/{Guid.CreateVersion7()}", Ct);

        // ModuleEntitlementMask quita el permiso antes del handler: 403 pelado.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
