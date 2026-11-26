using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Support.Application;
using static Modules.Support.IntegrationTests.SupportApiHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Spec 2026-11-22 §5.4 (Task 13): cambiar una tarea avisa <c>supportTicketChanged { ticketId }</c> en el tópico del ticket, en la
/// bandeja y en <c>support:tasks:{memberId}</c> de su creador, su responsable y, si cambió de manos, el responsable anterior. Un externo
/// nunca toca un tópico de tareas, y el cuerpo del aviso es sólo el id. Host con el publicador que anota los avisos; cada prueba
/// siembra su tenant y sólo lee los avisos de ese tenant.
/// </summary>
public sealed class SupportTaskRealtimeNoticesApiTests(ClockedSupportHost host) : IClassFixture<ClockedSupportHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RecordingRealtimePublisher Publisher => host.Publisher;

    private sealed record World(SupportTicketScenario Manager, Guid CashierMember, Guid AdvisorMember)
    {
        public string Url => Manager.Url;

        public Guid TenantId => Manager.TenantId;

        public Guid ManagerMember => Manager.CallerMemberId;
    }

    private async Task<World> StartAsync()
    {
        var factory = await host.StartTestAsync();
        var manager = await SupportTicketScenario.StartAsync(factory);
        var cashier = await SeedCallerMembershipAsync(factory, "Cora Cajera", "cashier", manager.Tenant, Guid.CreateVersion7().ToString());
        var advisor = await SeedMemberAsync(
            factory, $"asesor-{Guid.CreateVersion7():N}@example.com", "Aldo Asesor", "advisor", tenantId: manager.Tenant);
        return new World(manager, cashier, advisor);
    }

    // Una tarea creada por el gestor y trabajada por el asesor, sembrada por el dominio (sin avisos).
    private static async Task<Guid> SeedTaskAsync(World world) =>
        (await SupportTicketSeed.SaveInternalTicketAsync(
            world.Manager.Factory, world.TenantId, world.ManagerMember, world.AdvisorMember, new DateOnly(2030, 1, 1), ClockedSupportHost.Reception)).Id.Value;

    private IReadOnlyList<RecordingRealtimePublisher.Notice> NoticesAfter(World world, int already) =>
        [.. Publisher.Of(world.TenantId).Skip(already)];

    private static List<string> TopicsOf(IEnumerable<RecordingRealtimePublisher.Notice> notices) =>
        [.. notices.Select(notice => notice.Topic).Order(StringComparer.Ordinal)];

    private static List<string> ExpectedTaskTopics(Guid ticketId, params Guid[] members) =>
        [.. new[] { SupportRealtimeTopics.Ticket(ticketId), SupportRealtimeTopics.Inbox }
            .Concat(members.Distinct().Select(SupportRealtimeTopics.Tasks))
            .Order(StringComparer.Ordinal)];

    [Fact]
    public async Task CreatingATaskAnnouncesItToBothParticipants()
    {
        var world = await StartAsync();

        var response = await world.Manager.Client.PostAsJsonAsync(
            $"{world.Url}/tasks",
            new { subject = "Cuadrar el inventario", description = "Revisar las diferencias.", assigneeMemberId = world.AdvisorMember },
            Ct);
        var body = await BodyAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);
        var ticketId = JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();

        var notices = Publisher.Of(world.TenantId);
        Assert.Equal(ExpectedTaskTopics(ticketId, world.ManagerMember, world.AdvisorMember), TopicsOf(notices));
        Assert.All(notices, notice => Assert.Equal(ticketId, notice.TicketId));
    }

    [Fact]
    public async Task ChangingATaskAnnouncesItOnTheTasksTopics()
    {
        var world = await StartAsync();
        var ticketId = await SeedTaskAsync(world);
        var expected = ExpectedTaskTopics(ticketId, world.ManagerMember, world.AdvisorMember);

        var changes = new Dictionary<string, Func<Task<HttpResponseMessage>>>
        {
            ["status"] = async () => await world.Manager.ActAsync(
                ticketId, "status", new { status = "Open" }, (await world.Manager.DetailAsync(ticketId)).GetProperty("version").GetInt64()),
            ["priority"] = async () => await world.Manager.ActAsync(
                ticketId, "priority", new { priority = "High" }, (await world.Manager.DetailAsync(ticketId)).GetProperty("version").GetInt64()),
            ["due date"] = async () => await world.Manager.ActAsync(
                ticketId, "due-date", new { dueOn = "2030-02-01" }, (await world.Manager.DetailAsync(ticketId)).GetProperty("version").GetInt64()),
            ["note"] = () => world.Manager.Client.PostAsJsonAsync(
                $"{world.Url}/tickets/{ticketId}/notes", new { clientId = Guid.CreateVersion7(), text = "Revisé el inventario." }, Ct),
        };

        foreach (var (name, change) in changes)
        {
            var already = Publisher.Of(world.TenantId).Count;

            var response = await change();

            Assert.True(response.IsSuccessStatusCode, $"{name}: {await BodyAsync(response)}");
            Assert.Equal(expected, TopicsOf(NoticesAfter(world, already)));
        }
    }

    [Fact]
    public async Task ReassigningATaskAlsoTellsTheFormerAssignee()
    {
        var world = await StartAsync();
        var ticketId = await SeedTaskAsync(world);
        var already = Publisher.Of(world.TenantId).Count;
        var version = (await world.Manager.DetailAsync(ticketId)).GetProperty("version").GetInt64();

        var response = await world.Manager.ActAsync(ticketId, "assignee", new { assigneeMemberId = world.CashierMember }, version);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        Assert.Equal(
            ExpectedTaskTopics(ticketId, world.ManagerMember, world.AdvisorMember, world.CashierMember),
            TopicsOf(NoticesAfter(world, already)));
    }

    [Fact]
    public async Task ChangingAnExternalTicketNeverTouchesATasksTopic()
    {
        var world = await StartAsync();
        var ticket = await world.Manager.CreateAsync();
        var ticketId = SupportTicketScenario.Id(ticket);
        await world.Manager.MoveAsync(ticket, "Open");

        var topics = TopicsOf(Publisher.Of(world.TenantId));

        Assert.NotEmpty(topics);
        Assert.All(topics, topic => Assert.False(topic.StartsWith("support:tasks:", StringComparison.Ordinal), topic));
        Assert.Contains(SupportRealtimeTopics.Ticket(ticketId), topics);
    }

    [Fact]
    public async Task ThePayloadIsTheIdOnly()
    {
        var world = await StartAsync();
        var ticketId = await SeedTaskAsync(world);
        var already = Publisher.Of(world.TenantId).Count;
        var version = (await world.Manager.DetailAsync(ticketId)).GetProperty("version").GetInt64();
        await world.Manager.ActAsync(ticketId, "priority", new { priority = "Urgent" }, version);

        var notices = NoticesAfter(world, already);

        Assert.NotEmpty(notices);
        Assert.All(notices, notice =>
        {
            Assert.Equal(SupportRealtimeEvents.TicketChanged, notice.Kind);
            Assert.Equal(["ticketId"], notice.Payload.EnumerateObject().Select(property => property.Name));
            Assert.Equal(ticketId, notice.Payload.GetProperty("ticketId").GetGuid());
        });
    }
}
