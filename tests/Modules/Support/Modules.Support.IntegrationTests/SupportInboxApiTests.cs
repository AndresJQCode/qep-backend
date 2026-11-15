using System.Net;
using System.Text.Json;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportTicketScenario;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// La bandeja: el resumen de los seis estados, «vencidos» y «por vencer» con el umbral del tenant, la búsqueda por radicado y por
/// cliente, los filtros de responsable y el nombre del solicitante. Host propio de la clase con el reloj movible
/// (<see cref="ClockedSupportHost"/>); las altas son el viernes 2026-11-13 con «Petición general» (vence el 2026-12-07).
/// </summary>
public sealed class SupportInboxApiTests(ClockedSupportHost host) : IClassFixture<ClockedSupportHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSummaryCountsTheSixStatusesEvenAtZero()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        await scenario.CreateAsync();
        await scenario.CreateAsync();
        await scenario.MoveAsync(await scenario.CreateAsync(), "Open");

        var page = await ListAsync(scenario, "status=Open");

        Assert.Equal(
            [("New", 2), ("Open", 1), ("WaitingOnCustomer", 0), ("WaitingInternal", 0), ("Resolved", 0), ("Closed", 0)],
            page.GetProperty("summary").GetProperty("byStatus").EnumerateArray()
                .Select(item => (item.GetProperty("status").GetString()!, item.GetProperty("count").GetInt32())));
        // El resumen es del tenant entero; la página, del filtro.
        Assert.Equal(1, page.GetProperty("total").GetInt32());
        Assert.Equal(["Open"], page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("status").GetString()));
        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.Equal(25, page.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task OverdueShowsOnlyRunningPastDueTickets()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var open = await scenario.MoveAsync(await scenario.CreateAsync(), "Open");
        await scenario.MoveAsync(await scenario.CreateAsync(), "WaitingOnCustomer");
        await scenario.CreateAsync("Soporte técnico");
        host.Clock.UtcNow = At(12, 8);

        var page = await ListAsync(scenario, "due=overdue");

        Assert.Equal([Id(open)], Ids(page));
        Assert.Equal(1, page.GetProperty("total").GetInt32());
        Assert.Equal(1, page.GetProperty("summary").GetProperty("overdue").GetInt32());
        var item = page.GetProperty("items")[0];
        Assert.Equal("Overdue", item.GetProperty("dueState").GetString());
        Assert.Equal("2026-12-07", item.GetProperty("dueOn").GetString());
        Assert.Equal(0, item.GetProperty("businessDaysLeft").GetInt32());
    }

    [Fact]
    public async Task DueSoonShowsTicketsWithinThreeBusinessDays()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var ticket = await scenario.CreateAsync();

        host.Clock.UtcNow = At(12, 1);
        var before = await ListAsync(scenario, "due=dueSoon");
        host.Clock.UtcNow = At(12, 2);
        var inside = await ListAsync(scenario, "due=dueSoon");

        Assert.Empty(Ids(before));
        Assert.Equal(0, before.GetProperty("summary").GetProperty("dueSoon").GetInt32());
        Assert.Equal([Id(ticket)], Ids(inside));
        Assert.Equal(1, inside.GetProperty("summary").GetProperty("dueSoon").GetInt32());
        Assert.Equal("DueSoon", inside.GetProperty("items")[0].GetProperty("dueState").GetString());
        Assert.Equal(3, inside.GetProperty("items")[0].GetProperty("businessDaysLeft").GetInt32());
    }

    [Fact]
    public async Task TheTenantThresholdOf5MakesFourDaysLeftDueSoon()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var ticket = await scenario.CreateAsync();
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(scenario.Client, $"{scenario.Url}/settings", new { dueSoonBusinessDays = 5 }, 1)).StatusCode);
        host.Clock.UtcNow = At(12, 1);

        var page = await ListAsync(scenario, "due=dueSoon");
        var detail = await scenario.DetailAsync(Id(ticket));

        Assert.Equal([Id(ticket)], Ids(page));
        Assert.Equal(1, page.GetProperty("summary").GetProperty("dueSoon").GetInt32());
        Assert.Equal("DueSoon", detail.GetProperty("clock").GetProperty("dueState").GetString());
        Assert.Equal(4, detail.GetProperty("clock").GetProperty("businessDaysLeft").GetInt32());
    }

    [Fact]
    public async Task AThresholdOf0TurnsDueSoonOff()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var ticket = await scenario.CreateAsync();
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(scenario.Client, $"{scenario.Url}/settings", new { dueSoonBusinessDays = 0 }, 1)).StatusCode);
        host.Clock.UtcNow = At(12, 7);

        var page = await ListAsync(scenario, "due=dueSoon");
        var detail = await scenario.DetailAsync(Id(ticket));

        Assert.Empty(Ids(page));
        Assert.Equal(0, page.GetProperty("total").GetInt32());
        Assert.Equal(0, page.GetProperty("summary").GetProperty("dueSoon").GetInt32());
        Assert.Equal("OnTrack", detail.GetProperty("clock").GetProperty("dueState").GetString());
        Assert.Equal(0, detail.GetProperty("clock").GetProperty("businessDaysLeft").GetInt32());
    }

    [Fact]
    public async Task SearchingByNumberFindsTheTicket()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        await scenario.CreateAsync();
        var second = await scenario.CreateAsync();
        await scenario.CreateAsync();

        var byNumber = await ListAsync(scenario, "search=SOP-000002");
        var byDigit = await ListAsync(scenario, "search=2");

        Assert.Equal([Id(second)], Ids(byNumber));
        Assert.Equal([Id(second)], Ids(byDigit));
        Assert.Equal("SOP-000002", byNumber.GetProperty("items")[0].GetProperty("number").GetString());
    }

    [Fact]
    public async Task SearchingByCustomerNameFindsTheTicket()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var customer = await SeedCustomerAsync(scenario.Factory, scenario.TenantId, "Novasteel SAS");
        var linked = await scenario.CreateAsync(customerId: customer);
        await scenario.CreateAsync();

        var page = await ListAsync(scenario, "search=novasteel");

        Assert.Equal([Id(linked)], Ids(page));
        Assert.Equal("Novasteel SAS", page.GetProperty("items")[0].GetProperty("requesterDisplayName").GetString());
    }

    [Fact]
    public async Task AssigneeMeAndUnassignedFilter()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var advisor = await SeedMemberAsync(
            scenario.Factory, $"advisor-{scenario.Tenant}@example.com", "Beto Asesor", "advisor", tenantId: scenario.Tenant);
        var typeId = await scenario.TypeIdAsync("Petición general");
        var mine = await scenario.CreateAsync(Body(typeId, assignee: scenario.CallerMemberId));
        var theirs = await scenario.CreateAsync(Body(typeId, assignee: advisor));
        var nobody = await scenario.CreateAsync(Body(typeId));

        Assert.Equal([Id(mine)], Ids(await ListAsync(scenario, "assignee=me")));
        Assert.Equal([Id(nobody)], Ids(await ListAsync(scenario, "assignee=unassigned")));
        var byId = await ListAsync(scenario, $"assignee={advisor}");
        Assert.Equal([Id(theirs)], Ids(byId));
        Assert.Equal("Beto Asesor", byId.GetProperty("items")[0].GetProperty("assignee").GetProperty("displayName").GetString());
        Assert.Equal([Id(nobody), Id(theirs), Id(mine)], Ids(await ListAsync(scenario, string.Empty)));
    }

    [Fact]
    public async Task TheRequesterDisplayNameFallsBackToNit()
    {
        var scenario = await StartAsync(await host.StartTestAsync());
        var customer = await SeedCustomerAsync(scenario.Factory, scenario.TenantId, "Novasteel SAS");
        var ticket = await scenario.CreateAsync(Body(
            await scenario.TypeIdAsync("Queja"),
            requester: new { documentType = "NIT", documentNumber = "900123456", email = "compras@novasteel.test" },
            customerId: customer));
        // Sólo con NIT no hay nombre (plan D6), así que el alta necesitó el cliente; sin él resoluble, la fila cae a la foto.
        await DeleteCustomerAsync(scenario.Factory, customer);

        var page = await ListAsync(scenario, string.Empty);

        Assert.Equal([Id(ticket)], Ids(page));
        Assert.Equal("NIT 900123456", page.GetProperty("items")[0].GetProperty("requesterDisplayName").GetString());
    }

    [Fact]
    public async Task AnUnknownFilterIs422OnTheField()
    {
        var scenario = await StartAsync(await host.StartTestAsync());

        var response = await scenario.Client.GetAsync($"{scenario.Url}/tickets?due=late&pageSize=101", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
        Assert.Equal(["Due", "PageSize"], await ErrorKeysAsync(response));
    }

    private static async Task<JsonElement> ListAsync(SupportTicketScenario scenario, string query)
    {
        var response = await scenario.Client.GetAsync($"{scenario.Url}/tickets?{query}", Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonAsync(response);
    }

    private static List<Guid> Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();

    private static DateTimeOffset At(int month, int day) => new(2026, month, day, 15, 0, 0, TimeSpan.Zero);
}
