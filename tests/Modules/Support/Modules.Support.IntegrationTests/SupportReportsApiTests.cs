using System.Net;
using System.Text.Json;
using Modules.Support.Application;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportTicketScenario;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Fase 4b, Tarea 15 (H24, H25, owner 2026-11-16, Q-3, Q-16, Q-17): <c>GET /support/reports/summary</c>. Comparten el host con el reloj
/// del tenant movible (<see cref="ClockedSupportHost"/>): la población es por la fecha local de radicación, así que la prueba de los bordes
/// fija la hora de cada alta. Cada prueba siembra su tenant.
/// </summary>
public sealed class SupportReportsApiTests(ClockedSupportHost host) : IClassFixture<ClockedSupportHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // BFF: las colecciones de tamaño fijo viajan completas, también en cero: los seis estados, todos los tipos del tenant (activos o no,
    // en su orden) y las tres calificaciones.
    [Fact]
    public async Task EveryStatusTypeAndRatingTravelsEvenInZero()
    {
        var factory = await host.StartTestAsync();
        var scenario = await StartAsync(factory);
        // El reporte no cuenta el tipo interno: las tareas no son tickets de cliente.
        var types = (await JsonAsync(await scenario.Client.GetAsync($"{scenario.Url}/ticket-types", Ct))).EnumerateArray()
            .Where(type => !type.GetProperty("isInternal").GetBoolean()).ToList();

        var report = await ReportAsync(scenario.Client, scenario.Tenant, "2026-11-01", "2026-11-30");

        Assert.Equal(0, report.GetProperty("received").GetInt32());
        Assert.Equal(
            ["New", "Open", "WaitingOnCustomer", "WaitingInternal", "Resolved", "Closed"],
            report.GetProperty("byStatus").EnumerateArray().Select(item => item.GetProperty("status").GetString()));
        Assert.All(report.GetProperty("byStatus").EnumerateArray(), item => Assert.Equal(0, item.GetProperty("count").GetInt32()));
        Assert.Equal(
            types.Select(type => type.GetProperty("id").GetGuid()),
            report.GetProperty("byType").EnumerateArray().Select(item => item.GetProperty("typeId").GetGuid()));
        Assert.All(
            report.GetProperty("byType").EnumerateArray(),
            row => Assert.Equal(["Good", "Regular", "Bad"], row.GetProperty("ratings").EnumerateArray().Select(item => item.GetProperty("rating").GetString())));
        var satisfaction = report.GetProperty("satisfaction");
        Assert.Equal(["Good", "Regular", "Bad"], satisfaction.GetProperty("ratings").EnumerateArray().Select(item => item.GetProperty("rating").GetString()));
        Assert.Equal(JsonValueKind.Null, report.GetProperty("resolutionTime").GetProperty("averageBusinessDays").ValueKind);
    }

    // H25: la población es la de la fecha local de radicación (Bogotá): un ticket de las 23:59 del día anterior y uno de las 00:00 del
    // día siguiente quedan fuera.
    [Fact]
    public async Task TicketsOutsideTheRangeDoNotCount()
    {
        var factory = await host.StartTestAsync();
        var scenario = await StartAsync(factory);
        foreach (var local in new[] { "2026-11-12T23:59:00", "2026-11-13T00:00:00", "2026-11-13T23:59:00", "2026-11-14T00:00:00" })
        {
            host.Clock.UtcNow = new DateTimeOffset(DateTime.Parse(local, System.Globalization.CultureInfo.InvariantCulture), TimeSpan.FromHours(-5));
            await scenario.CreateAsync();
        }

        var report = await ReportAsync(scenario.Client, scenario.Tenant, "2026-11-13", "2026-11-13");

        Assert.Equal(2, report.GetProperty("received").GetInt32());
        Assert.Equal(("2026-11-13", "2026-11-13"), (report.GetProperty("from").GetString(), report.GetProperty("to").GetString()));
    }

    // Revisión del bloque D: un ticket resuelto, reabierto y cerrado desde «Esperando al cliente» no cuenta como resuelto, aunque
    // conserve el resolved_at del ciclo anterior; uno cerrado desde Resuelto sí.
    [Fact]
    public async Task AClosedTicketCountsAsResolvedOnlyWhenClosedFromResolved()
    {
        var factory = await host.StartTestAsync();
        var scenario = await StartAsync(factory);
        var fromResolved = await scenario.MoveAsync(await scenario.CreateAsync(), "Resolved");
        await scenario.MoveAsync(fromResolved, "Closed");
        var reopened = await scenario.MoveAsync(await scenario.CreateAsync(), "Resolved");
        reopened = await scenario.MoveAsync(reopened, "Open");
        reopened = await scenario.MoveAsync(reopened, "WaitingOnCustomer");
        await scenario.MoveAsync(reopened, "Closed");

        var report = await ReportAsync(scenario.Client, scenario.Tenant, "2026-11-13", "2026-11-13");

        Assert.Equal((2, 1), (report.GetProperty("received").GetInt32(), report.GetProperty("resolved").GetInt32()));
        Assert.Equal(1, report.GetProperty("resolutionTime").GetProperty("resolved").GetInt32());
    }

    // Q-16: lo ve todo el que tenga leer o gestionar.
    [Fact]
    public async Task AReaderCanSeeTheReport()
    {
        var factory = await host.StartTestAsync();
        var scenario = await StartAsync(factory);
        await scenario.CreateAsync();
        using var reader = CreateClient(factory, Guid.CreateVersion7().ToString(), scenario.Tenant, SupportPermissions.Read);

        var report = await ReportAsync(reader, scenario.Tenant, "2026-11-13", "2026-11-13");

        Assert.Equal(1, report.GetProperty("received").GetInt32());
    }

    [Fact]
    public async Task AnotherTenantIs403()
    {
        var factory = await host.StartTestAsync();
        var scenario = await StartAsync(factory);
        var other = await StartAsync(factory);

        var response = await scenario.Client.GetAsync(
            $"{other.Url}/reports/summary?from=2026-11-01&to=2026-11-30", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ARangeOver366DaysIs422()
    {
        var factory = await host.StartTestAsync();
        var scenario = await StartAsync(factory);

        var response = await scenario.Client.GetAsync($"{scenario.Url}/reports/summary?from=2025-11-13&to=2026-11-14", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("validation.failed", await CodeOfAsync(response));
        Assert.Equal(["To"], await ErrorKeysAsync(response));
    }

    private static async Task<JsonElement> ReportAsync(HttpClient client, string tenant, string from, string to)
    {
        var response = await client.GetAsync($"{SupportUrl(tenant)}/reports/summary?from={from}&to={to}", Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await BodyAsync(response));
        return await JsonAsync(response);
    }

    // Spec 2026-11-22 §5.3: una tarea resuelta en el rango no mueve ningún total ni aparece «Tarea interna» en el desglose por tipo.
    [Fact]
    public async Task TheReportIgnoresTasks()
    {
        var factory = await host.StartTestAsync();
        var scenario = await StartAsync(factory);
        var before = await ReportAsync(scenario.Client, scenario.Tenant, "2026-11-13", "2026-11-13");
        var task = await SupportTicketSeed.SaveInternalTicketAsync(
            factory, scenario.TenantId, Guid.CreateVersion7(), Guid.CreateVersion7(), null, ClockedSupportHost.Reception);
        await SupportTicketSeed.UpdateTicketAsync(
            factory, scenario.TenantId, task.Id,
            ticket => ticket.ChangeStatus(
                Modules.Support.Domain.SupportTicketStatus.Resolved, Guid.CreateVersion7(), ClockedSupportHost.Reception, SupportTicketSeed.Clock));

        var after = await ReportAsync(scenario.Client, scenario.Tenant, "2026-11-13", "2026-11-13");

        Assert.Equal(before.GetProperty("received").GetInt32(), after.GetProperty("received").GetInt32());
        Assert.Equal(
            before.GetProperty("byStatus").GetRawText(), after.GetProperty("byStatus").GetRawText());
        Assert.DoesNotContain(
            after.GetProperty("byType").EnumerateArray(), row => row.GetProperty("name").GetString() == "Tarea interna");
    }
}
