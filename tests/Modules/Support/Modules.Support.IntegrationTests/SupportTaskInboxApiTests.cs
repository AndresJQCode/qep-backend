using System.Net;
using System.Text.Json;
using Modules.Support.Application;
using static Modules.Support.IntegrationTests.SupportApiHarness;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Spec 2026-11-22 §5.3 y §5.6 (Task 12): la bandeja separa externos de tareas con <c>kind</c> (por defecto <c>external</c>, así la
/// bandeja de siempre no cambia), <c>createdBy=me</c> y, para quien sólo usa tareas, el filtro por participante en la consulta. El
/// resumen sigue el mismo alcance que la lista: nadie cuenta tickets de clientes ni tareas ajenas. Host propio con el reloj movible; las
/// altas son el viernes 2026-11-13 y las tareas vencen el lunes 2026-11-16.
/// </summary>
public sealed class SupportTaskInboxApiTests(ClockedSupportHost host) : IClassFixture<ClockedSupportHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateOnly TaskDue = new(2026, 11, 16);

    // Un externo, una tarea de la cajera (la creó el gestor), una tarea entre otros dos (gestor y asesor); todas en New.
    private sealed record World(
        SupportTicketScenario Manager,
        HttpClient TaskOnly,
        Guid CashierMember,
        Guid AdvisorMember,
        Guid External,
        Guid CashierTask,
        Guid ForeignTask)
    {
        public string Url => Manager.Url;
    }

    private async Task<World> StartAsync()
    {
        var factory = await host.StartTestAsync();
        var manager = await SupportTicketScenario.StartAsync(factory);
        var subject = Guid.CreateVersion7().ToString();
        var cashier = await SeedCallerMembershipAsync(factory, "Cora Cajera", "cashier", manager.Tenant, subject);
        var advisor = await SeedMemberAsync(
            factory, $"asesor-{Guid.CreateVersion7():N}@example.com", "Aldo Asesor", "advisor", tenantId: manager.Tenant);
        var client = CreateClient(factory, subject, manager.Tenant, SupportPermissions.TaskUse);
        var external = SupportTicketScenario.Id(await manager.CreateAsync());
        var cashierTask = (await SupportTicketSeed.SaveInternalTicketAsync(
            factory, manager.TenantId, manager.CallerMemberId, cashier, TaskDue, ClockedSupportHost.Reception)).Id.Value;
        var foreignTask = (await SupportTicketSeed.SaveInternalTicketAsync(
            factory, manager.TenantId, manager.CallerMemberId, advisor, TaskDue, ClockedSupportHost.Reception)).Id.Value;
        return new World(manager, client, cashier, advisor, external, cashierTask, foreignTask);
    }

    private static async Task<JsonElement> ListAsync(HttpClient client, string url, string query = "")
    {
        var response = await client.GetAsync($"{url}/tickets{query}", Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonAsync(response);
    }

    private static List<Guid> Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).Order().ToList();

    private static int CountIn(JsonElement page, string status) =>
        page.GetProperty("summary").GetProperty("byStatus").EnumerateArray()
            .Single(item => item.GetProperty("status").GetString() == status).GetProperty("count").GetInt32();

    [Fact]
    public async Task TheInboxDefaultsToExternalTickets()
    {
        var world = await StartAsync();

        var page = await ListAsync(world.Manager.Client, world.Url);

        Assert.Equal([world.External], Ids(page));
        Assert.Equal(1, CountIn(page, "New"));
    }

    [Fact]
    public async Task KindInternalListsTasksAndAllListsBoth()
    {
        var world = await StartAsync();

        var tasks = await ListAsync(world.Manager.Client, world.Url, "?kind=internal");
        var all = await ListAsync(world.Manager.Client, world.Url, "?kind=all");

        Assert.Equal(new[] { world.CashierTask, world.ForeignTask }.Order().ToList(), Ids(tasks));
        Assert.Equal(2, CountIn(tasks, "New"));
        Assert.Equal(new[] { world.External, world.CashierTask, world.ForeignTask }.Order().ToList(), Ids(all));
        Assert.Equal(3, CountIn(all, "New"));
    }

    [Fact]
    public async Task ATaskOnlyMemberListsOnlyHerTasksAndTheSummaryCountsOnlyThose()
    {
        var world = await StartAsync();

        var page = await ListAsync(world.TaskOnly, world.Url);

        Assert.Equal([world.CashierTask], Ids(page));
        Assert.Equal(1, page.GetProperty("total").GetInt32());
        Assert.Equal(1, CountIn(page, "New"));
        Assert.Equal(0, page.GetProperty("summary").GetProperty("overdue").GetInt32());
        // Viernes 13: la tarea vence el lunes 16, dentro de los tres días hábiles. La ajena no suma.
        Assert.Equal(1, page.GetProperty("summary").GetProperty("dueSoon").GetInt32());

        host.Clock.UtcNow = new DateTimeOffset(2026, 11, 18, 15, 0, 0, TimeSpan.Zero);
        var later = await ListAsync(world.TaskOnly, world.Url);

        Assert.Equal(1, later.GetProperty("summary").GetProperty("overdue").GetInt32());
        Assert.Equal(0, later.GetProperty("summary").GetProperty("dueSoon").GetInt32());
    }

    [Fact]
    public async Task ATaskOnlyMemberAskingForExternalOrAllGets403()
    {
        var world = await StartAsync();

        foreach (var kind in new[] { "external", "all" })
        {
            var response = await world.TaskOnly.GetAsync($"{world.Url}/tickets?kind={kind}", Ct);
            var body = await BodyAsync(response);

            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, body);
            Assert.Equal("authorization.denied", JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
        }

        var own = await world.TaskOnly.GetAsync($"{world.Url}/tickets?kind=internal", Ct);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    [Fact]
    public async Task CreatedByMeListsTheTasksICreated()
    {
        var world = await StartAsync();
        var mine = (await SupportTicketSeed.SaveInternalTicketAsync(
            world.Manager.Factory, world.Manager.TenantId, world.CashierMember, world.AdvisorMember, TaskDue, ClockedSupportHost.Reception)).Id.Value;

        var cashier = await ListAsync(world.TaskOnly, world.Url, "?createdBy=me");
        var manager = await ListAsync(world.Manager.Client, world.Url, "?kind=internal&createdBy=me");

        Assert.Equal([mine], Ids(cashier));
        Assert.Equal(new[] { world.CashierTask, world.ForeignTask }.Order().ToList(), Ids(manager));
    }

    [Fact]
    public async Task AssigneeMeStillWorks()
    {
        var world = await StartAsync();

        var cashier = await ListAsync(world.TaskOnly, world.Url, "?assignee=me");

        Assert.Equal([world.CashierTask], Ids(cashier));
    }
}
