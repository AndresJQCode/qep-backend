using System.Net;
using System.Text.Json;
using Modules.Scheduling.Application;
using Modules.Scheduling.Domain;
using static Modules.Scheduling.IntegrationTests.SchedulingApiHarness;

namespace Modules.Scheduling.IntegrationTests;

/// <summary>
/// <c>GET calendar</c> (plan 2026-11-21, Tarea 10): one response, already filtered by the caller's scope, with the layout and the counts the
/// screen paints computed by the server. The scope is the point (Review Focus 2): a member without «ver todas las agendas» never sees
/// another resource, and asking for one by id is a 403, not a 404.
/// </summary>
public sealed class SchedulingCalendarApiTests(SchedulingApiWorld world) : IClassFixture<SchedulingApiWorld>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url(SchedulingTenant tenant, DateOnly day, params Guid[] resourceIds) =>
        $"{tenant.Url}/calendar?date={day:yyyy-MM-dd}" + string.Concat(resourceIds.Select(id => $"&resourceIds={id}"));

    private static JsonElement ResourceOf(JsonElement calendar, Guid resourceId) =>
        calendar.GetProperty("resources").EnumerateArray().Single(resource => resource.GetProperty("id").GetGuid() == resourceId);

    [Fact]
    public async Task AMemberWithoutReadAllNeverSeesAnotherResource()
    {
        var tenant = await NewTenantAsync(world.Factory);
        using var owner = tenant;
        var (own, provider) = await CreateProviderAsync(tenant, "torres@example.com", "Torres");
        using var providerClient = provider;
        var other = await CreateResourceAsync(tenant, "Dr. Mora");
        var service = await CreateServiceAsync(tenant, "Limpieza", 30);
        var location = await MainLocationIdAsync(tenant);
        var customer = await CreateCustomerAsync(tenant);
        var day = FutureMonday();
        await CreatedAsync(tenant.Admin, $"{tenant.Url}/blocks", OneOffBlockBody(day, [own, other], [service], "Jornada"));
        await SeedAppointmentAsync(world.Factory, tenant.IdText, location, own, service, customer, NineOClock(day), AppointmentStatus.Confirmed);
        await SeedAppointmentAsync(world.Factory, tenant.IdText, location, other, service, customer, NineOClock(day), AppointmentStatus.Confirmed);

        var mine = await GetJsonAsync(provider, Url(tenant, day));
        var foreign = await provider.GetAsync(Url(tenant, day, other), Ct);
        var both = await provider.GetAsync(Url(tenant, day, own, other), Ct);

        var resource = Assert.Single(mine.GetProperty("resources").EnumerateArray());
        Assert.Equal(own, resource.GetProperty("id").GetGuid());
        Assert.Single(resource.GetProperty("appointments").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal("authorization.denied", CodeOf(await JsonOfAsync(foreign)));
        Assert.Equal(HttpStatusCode.Forbidden, both.StatusCode);
    }

    [Fact]
    public async Task AMemberWithNoLinkedResourceSeesAnEmptyCalendar()
    {
        var tenant = await NewTenantAsync(world.Factory);
        using var owner = tenant;
        await CreateResourceAsync(tenant, "Dr. Mora");
        var (_, userId) = await SeedMemberAsync(world.Factory, tenant.IdText, "sin-agenda@example.com", "Sin Agenda");
        using var unlinked = tenant.Client(userId.ToString(), SchedulingPermissions.AppointmentRead);

        var calendar = await GetJsonAsync(unlinked, Url(tenant, FutureMonday()));

        Assert.Empty(calendar.GetProperty("resources").EnumerateArray());
    }

    [Fact]
    public async Task ReadAllSeesEveryResource()
    {
        var tenant = await NewTenantAsync(world.Factory);
        using var owner = tenant;
        var second = await CreateResourceAsync(tenant, "Dra. Mora");
        var first = await CreateResourceAsync(tenant, "Dra. Alvarez");
        var day = FutureMonday();

        var all = await GetJsonAsync(tenant.Admin, Url(tenant, day));
        var onlyMora = await GetJsonAsync(tenant.Admin, Url(tenant, day, second));

        Assert.Equal(
            [first, second],
            all.GetProperty("resources").EnumerateArray().Select(resource => resource.GetProperty("id").GetGuid()));
        Assert.Equal(second, Assert.Single(onlyMora.GetProperty("resources").EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task CalendarReturnsOneResponseWithLayoutAndCounts()
    {
        var tenant = await NewTenantAsync(world.Factory);
        using var owner = tenant;
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var service = await CreateServiceAsync(tenant, "Limpieza", 30);
        var location = await MainLocationIdAsync(tenant);
        var customer = await CreateCustomerAsync(tenant, "Ana Ruiz");
        var day = FutureMonday();
        await CreatedAsync(tenant.Admin, $"{tenant.Url}/blocks", OneOffBlockBody(day, [resource], [service], "Jornada"));
        var nine = NineOClock(day);
        var booked = await SeedAppointmentAsync(world.Factory, tenant.IdText, location, resource, service, customer, nine, AppointmentStatus.Confirmed);
        await SeedAppointmentAsync(world.Factory, tenant.IdText, location, resource, service, customer, nine.AddMinutes(30), AppointmentStatus.Pending);
        // Una cancelada libera su lugar: no se dibuja ni se cuenta.
        await SeedAppointmentAsync(world.Factory, tenant.IdText, location, resource, service, customer, nine.AddMinutes(60), AppointmentStatus.Cancelled);
        var closure = await CreatedAsync(
            tenant.Admin,
            $"{tenant.Url}/closures",
            new { resourceId = resource, startsAt = nine.AddMinutes(150), endsAt = nine.AddMinutes(180), reason = "Capacitación" });

        var calendar = await GetJsonAsync(tenant.Admin, Url(tenant, day));

        Assert.Equal("Personal", calendar.GetProperty("layout").GetString());
        Assert.Equal("America/Bogota", calendar.GetProperty("timeZone").GetString());
        Assert.Equal(Iso(day), calendar.GetProperty("from").GetString());
        Assert.Equal(Iso(day), calendar.GetProperty("to").GetString());
        var row = Assert.Single(calendar.GetProperty("resources").EnumerateArray());
        Assert.Equal(location, row.GetProperty("locationId").GetGuid());
        Assert.Equal("Dra. Torres", row.GetProperty("displayName").GetString());
        Assert.Equal(2, row.GetProperty("appointmentCount").GetInt32());
        // Seis inicios de 30 minutos entre 09:00 y 12:00: dos reservados y uno bloqueado; el de la cancelada vuelve a estar libre.
        Assert.Equal(3, row.GetProperty("freeCount").GetInt32());
        var window = Assert.Single(row.GetProperty("openWindows").EnumerateArray());
        Assert.Equal(nine, window.GetProperty("start").GetDateTimeOffset());
        Assert.Equal(nine.AddHours(3), window.GetProperty("end").GetDateTimeOffset());
        var closed = Assert.Single(row.GetProperty("closures").EnumerateArray());
        Assert.Equal(closure.GetProperty("id").GetGuid(), closed.GetProperty("id").GetGuid());
        Assert.Equal("Capacitación", closed.GetProperty("reason").GetString());
        var appointments = row.GetProperty("appointments").EnumerateArray().ToArray();
        Assert.Equal(2, appointments.Length);
        Assert.Equal(booked, appointments[0].GetProperty("id").GetGuid());
        Assert.Equal(nine, appointments[0].GetProperty("start").GetDateTimeOffset());
        Assert.Equal(nine.AddMinutes(30), appointments[0].GetProperty("end").GetDateTimeOffset());
        Assert.Equal("Confirmed", appointments[0].GetProperty("status").GetString());
        Assert.Equal("Pending", appointments[1].GetProperty("status").GetString());
        Assert.Equal("Ana Ruiz", appointments[0].GetProperty("customerName").GetString());
        Assert.Equal("Limpieza", appointments[0].GetProperty("serviceName").GetString());
        Assert.Equal("BackOffice", appointments[0].GetProperty("source").GetString());
        Assert.NotEqual(Guid.Empty, appointments[0].GetProperty("sessionId").GetGuid());
        Assert.Equal(1, appointments[0].GetProperty("booked").GetInt32());
        Assert.Equal(1, appointments[0].GetProperty("capacity").GetInt32());

        // Con una segunda sede o un segundo recurso, el servidor dice «Consolidated»: la pantalla no cuenta recursos.
        await CreateResourceAsync(tenant, "Dr. Mora");
        var consolidated = await GetJsonAsync(tenant.Admin, Url(tenant, day));
        Assert.Equal("Consolidated", consolidated.GetProperty("layout").GetString());
    }

    [Fact]
    public async Task ARangeReturnsEveryDayAndAResourceWithoutBlocksHasNoWindows()
    {
        var tenant = await NewTenantAsync(world.Factory);
        using var owner = tenant;
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var day = FutureMonday();

        var calendar = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/calendar?from={day:yyyy-MM-dd}&to={day.AddDays(6):yyyy-MM-dd}");

        Assert.Equal(Iso(day.AddDays(6)), calendar.GetProperty("to").GetString());
        var row = ResourceOf(calendar, resource);
        Assert.Empty(row.GetProperty("openWindows").EnumerateArray());
        Assert.Empty(row.GetProperty("closures").EnumerateArray());
        Assert.Empty(row.GetProperty("appointments").EnumerateArray());
        Assert.Equal(0, row.GetProperty("appointmentCount").GetInt32());
        Assert.Equal(0, row.GetProperty("freeCount").GetInt32());
    }

    [Fact]
    public async Task ABackwardsRangeIs422WithTheFieldMarked()
    {
        var tenant = await NewTenantAsync(world.Factory);
        using var owner = tenant;
        var day = FutureMonday();

        var response = await tenant.Admin.GetAsync($"{tenant.Url}/calendar?from={day:yyyy-MM-dd}&to={day.AddDays(-1):yyyy-MM-dd}", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True(HasError(await JsonOfAsync(response), "to"));
    }

    [Fact]
    public async Task WithoutADayOrARangeIs422()
    {
        var tenant = await NewTenantAsync(world.Factory);
        using var owner = tenant;

        var response = await tenant.Admin.GetAsync($"{tenant.Url}/calendar", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True(HasError(await JsonOfAsync(response), "date"));
    }

    [Fact]
    public async Task AnUnknownResourceIs404AndAnUnknownLocationToo()
    {
        var tenant = await NewTenantAsync(world.Factory);
        using var owner = tenant;
        var day = FutureMonday();

        var resource = await tenant.Admin.GetAsync(Url(tenant, day, Guid.CreateVersion7()), Ct);
        var location = await tenant.Admin.GetAsync($"{Url(tenant, day)}&locationId={Guid.CreateVersion7()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, resource.StatusCode);
        Assert.Equal("scheduling.resource.not_found", CodeOf(await JsonOfAsync(resource)));
        Assert.Equal(HttpStatusCode.NotFound, location.StatusCode);
        Assert.Equal("scheduling.location.not_found", CodeOf(await JsonOfAsync(location)));
    }

    [Fact]
    public async Task TheLocationFilterKeepsOnlyItsResources()
    {
        var tenant = await NewTenantAsync(world.Factory);
        using var owner = tenant;
        var main = await MainLocationIdAsync(tenant);
        var branch = (await CreatedAsync(tenant.Admin, $"{tenant.Url}/locations", new { name = "Sede norte" })).GetProperty("id").GetGuid();
        var inMain = await CreateResourceAsync(tenant, "Dra. Torres");
        var inBranch = await CreateResourceAsync(tenant, "Dr. Mora", locationId: branch);
        var day = FutureMonday();

        var onlyBranch = await GetJsonAsync(tenant.Admin, $"{Url(tenant, day)}&locationId={branch}");

        Assert.Equal(inBranch, Assert.Single(onlyBranch.GetProperty("resources").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.NotEqual(main, branch);
        Assert.NotEqual(inMain, inBranch);
    }

    [Fact]
    public async Task WithoutAppointmentReadOrInAnotherTenantItIs403()
    {
        var tenant = await NewTenantAsync(world.Factory);
        using var owner = tenant;
        var other = await NewTenantAsync(world.Factory, "Otra Clínica");
        using var otherOwner = other;
        using var noRead = tenant.Client(null, SchedulingPermissions.AppointmentManage);

        var withoutPermission = await noRead.GetAsync(Url(tenant, FutureMonday()), Ct);
        var crossTenant = await other.Admin.GetAsync(Url(tenant, FutureMonday()), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, withoutPermission.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crossTenant.StatusCode);
    }
}
