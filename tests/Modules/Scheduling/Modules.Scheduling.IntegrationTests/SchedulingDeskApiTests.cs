using System.Net;
using System.Text.Json;
using Modules.Scheduling.Application;
using Modules.Scheduling.Domain;
using static Modules.Scheduling.IntegrationTests.SchedulingApiHarness;

namespace Modules.Scheduling.IntegrationTests;

/// <summary>
/// <c>GET desk</c> (plan 2026-11-21, Tarea 10): what the secretary opens the day with. The counters of every status travel complete, even at
/// zero, and with a scope they only count what the member sees. «Sin marcar» is a confirmed appointment that already ended.
/// </summary>
public sealed class SchedulingDeskApiTests(SchedulingApiWorld world) : IClassFixture<SchedulingApiWorld>
{
    private static readonly TimeZoneInfo Bogota = TimeZoneInfo.FindSystemTimeZoneById("America/Bogota");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url(SchedulingTenant tenant, DateOnly? day = null) =>
        day is { } value ? $"{tenant.Url}/desk?date={value:yyyy-MM-dd}" : $"{tenant.Url}/desk";

    // Un día ya pasado del tenant: sus citas están vencidas sin importar la hora a la que corra la prueba.
    private static DateOnly PastDay() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Bogota).DateTime).AddDays(-3);

    private static int Counter(JsonElement desk, string name) => desk.GetProperty("counters").GetProperty(name).GetInt32();

    private async Task<(SchedulingTenant Tenant, Guid Resource, Guid Service, Guid Location, Guid Customer)> SceneAsync()
    {
        var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var service = await CreateServiceAsync(tenant, "Limpieza", 30);
        var customer = await CreateCustomerAsync(tenant, "Ana Ruiz");
        return (tenant, resource, service, await MainLocationIdAsync(tenant), customer);
    }

    [Fact]
    public async Task TheCountersOfEveryStatusTravelEvenAtZero()
    {
        var tenant = await NewTenantAsync(world.Factory);
        using var owner = tenant;

        var desk = await GetJsonAsync(tenant.Admin, Url(tenant, FutureMonday()));

        foreach (var name in new[] { "appointments", "pending", "unmarked", "attended", "noShow", "free" })
        {
            Assert.Equal(0, Counter(desk, name));
        }

        Assert.Equal(Iso(FutureMonday()), desk.GetProperty("date").GetString());
        Assert.Empty(desk.GetProperty("pending").EnumerateArray());
        Assert.Empty(desk.GetProperty("unmarked").EnumerateArray());
        Assert.Empty(desk.GetProperty("upcoming").EnumerateArray());
    }

    [Fact]
    public async Task WithoutADateTheDeskIsTodayInTheTenantTimeZone()
    {
        var tenant = await NewTenantAsync(world.Factory);
        using var owner = tenant;

        var desk = await GetJsonAsync(tenant.Admin, Url(tenant));

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Bogota).DateTime);
        Assert.Equal(Iso(today), desk.GetProperty("date").GetString());
    }

    [Fact]
    public async Task DeskCountersMatchWhatTheScopeSees()
    {
        var tenant = await NewTenantAsync(world.Factory);
        using var owner = tenant;
        var (own, provider) = await CreateProviderAsync(tenant, "torres@example.com", "Torres");
        using var providerClient = provider;
        var other = await CreateResourceAsync(tenant, "Dr. Mora");
        var service = await CreateServiceAsync(tenant, "Limpieza", 30);
        var location = await MainLocationIdAsync(tenant);
        var customer = await CreateCustomerAsync(tenant);
        var past = PastDay();
        var nine = NineOClock(past);
        var future = NineOClock(FutureMonday());

        // Lo suyo: una atendida y una que no llegó. Lo ajeno: dos atendidas, una vencida sin marcar y una por confirmar.
        await SeedAppointmentAsync(world.Factory, tenant.IdText, location, own, service, customer, nine, AppointmentStatus.Completed);
        await SeedAppointmentAsync(world.Factory, tenant.IdText, location, own, service, customer, nine.AddMinutes(30), AppointmentStatus.NoShow);
        await SeedAppointmentAsync(world.Factory, tenant.IdText, location, other, service, customer, nine, AppointmentStatus.Completed);
        await SeedAppointmentAsync(world.Factory, tenant.IdText, location, other, service, customer, nine.AddMinutes(30), AppointmentStatus.Completed);
        await SeedAppointmentAsync(world.Factory, tenant.IdText, location, other, service, customer, nine.AddMinutes(60), AppointmentStatus.Confirmed);
        await SeedAppointmentAsync(world.Factory, tenant.IdText, location, other, service, customer, future, AppointmentStatus.Pending);
        await SeedAppointmentAsync(world.Factory, tenant.IdText, location, other, service, customer, nine.AddMinutes(90), AppointmentStatus.Cancelled);

        var everything = await GetJsonAsync(tenant.Admin, Url(tenant, past));
        var mine = await GetJsonAsync(provider, Url(tenant, past));

        // Las canceladas no cuentan como citas del día; «pendientes» y «sin marcar» son colas, no del día.
        Assert.Equal(5, Counter(everything, "appointments"));
        Assert.Equal(3, Counter(everything, "attended"));
        Assert.Equal(1, Counter(everything, "noShow"));
        Assert.Equal(1, Counter(everything, "unmarked"));
        Assert.Equal(1, Counter(everything, "pending"));
        Assert.Equal(2, Counter(mine, "appointments"));
        Assert.Equal(1, Counter(mine, "attended"));
        Assert.Equal(1, Counter(mine, "noShow"));
        Assert.Equal(0, Counter(mine, "unmarked"));
        Assert.Equal(0, Counter(mine, "pending"));
        Assert.Empty(mine.GetProperty("pending").EnumerateArray());
        Assert.Empty(mine.GetProperty("unmarked").EnumerateArray());
    }

    [Fact]
    public async Task TheFreeCounterIsTheBookableStartsOfTheDayInTheScope()
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

        var everything = await GetJsonAsync(tenant.Admin, Url(tenant, day));
        var mine = await GetJsonAsync(provider, Url(tenant, day));

        // Seis inicios por recurso entre 09:00 y 12:00; el suyo tiene uno ocupado.
        Assert.Equal(11, Counter(everything, "free"));
        Assert.Equal(5, Counter(mine, "free"));
        Assert.Equal(1, Counter(mine, "appointments"));
    }

    [Fact]
    public async Task UnmarkedAreConfirmedAppointmentsAlreadyOver()
    {
        var (tenant, resource, service, location, customer) = await SceneAsync();
        using var owner = tenant;
        var nine = NineOClock(PastDay());
        var future = NineOClock(FutureMonday());
        var unmarked = await SeedAppointmentAsync(world.Factory, tenant.IdText, location, resource, service, customer, nine, AppointmentStatus.Confirmed);
        await SeedAppointmentAsync(world.Factory, tenant.IdText, location, resource, service, customer, nine.AddMinutes(30), AppointmentStatus.Completed);
        await SeedAppointmentAsync(world.Factory, tenant.IdText, location, resource, service, customer, nine.AddMinutes(60), AppointmentStatus.Pending);
        await SeedAppointmentAsync(world.Factory, tenant.IdText, location, resource, service, customer, future, AppointmentStatus.Confirmed);

        var desk = await GetJsonAsync(tenant.Admin, Url(tenant, FutureMonday()));

        Assert.Equal(1, Counter(desk, "unmarked"));
        var row = Assert.Single(desk.GetProperty("unmarked").EnumerateArray());
        Assert.Equal(unmarked, row.GetProperty("appointmentId").GetGuid());
        Assert.Equal("Ana Ruiz", row.GetProperty("customerName").GetString());
        Assert.Equal("Limpieza", row.GetProperty("serviceName").GetString());
        Assert.Equal("Dra. Torres", row.GetProperty("resourceName").GetString());
        Assert.Equal(nine, row.GetProperty("start").GetDateTimeOffset());
        Assert.Equal("BackOffice", row.GetProperty("source").GetString());
        // La confirmada de la fecha pedida todavía no pasa: es la que viene.
        var upcoming = Assert.Single(desk.GetProperty("upcoming").EnumerateArray());
        Assert.Equal(future, upcoming.GetProperty("start").GetDateTimeOffset());
    }

    [Fact]
    public async Task ThePendingQueueIsOrderedByRequestTime()
    {
        var (tenant, resource, service, location, customer) = await SceneAsync();
        using var owner = tenant;
        var day = FutureMonday();
        var nine = NineOClock(day);
        var now = DateTimeOffset.UtcNow;

        // Se siembran fuera de orden: la cola va de la solicitud más vieja a la más nueva, no por la hora de la cita.
        var recent = await SeedAppointmentAsync(
            world.Factory, tenant.IdText, location, resource, service, customer, nine, AppointmentStatus.Pending,
            requestedAt: now.AddHours(-1), source: AppointmentSource.Channel);
        var oldest = await SeedAppointmentAsync(
            world.Factory, tenant.IdText, location, resource, service, customer, nine.AddMinutes(60), AppointmentStatus.Pending,
            requestedAt: now.AddHours(-3), source: AppointmentSource.Channel);
        var middle = await SeedAppointmentAsync(
            world.Factory, tenant.IdText, location, resource, service, customer, nine.AddMinutes(30), AppointmentStatus.Pending,
            requestedAt: now.AddHours(-2));

        var desk = await GetJsonAsync(tenant.Admin, Url(tenant, day));

        var pending = desk.GetProperty("pending").EnumerateArray().ToArray();
        Assert.Equal([oldest, middle, recent], pending.Select(row => row.GetProperty("appointmentId").GetGuid()));
        Assert.Equal(3, Counter(desk, "pending"));
        Assert.Equal("Channel", pending[0].GetProperty("source").GetString());
        // Minutos desde que se pidió, con el reloj del servidor: la pantalla sólo lo formatea.
        Assert.InRange(pending[0].GetProperty("requestedAgo").GetInt32(), 180, 185);
        Assert.InRange(pending[1].GetProperty("requestedAgo").GetInt32(), 120, 125);
        Assert.InRange(pending[2].GetProperty("requestedAgo").GetInt32(), 60, 65);
    }

    [Fact]
    public async Task TheUpcomingListIsTheDayStillToCome()
    {
        var (tenant, resource, service, location, customer) = await SceneAsync();
        using var owner = tenant;
        var day = FutureMonday();
        var nine = NineOClock(day);
        var second = await SeedAppointmentAsync(world.Factory, tenant.IdText, location, resource, service, customer, nine.AddMinutes(30), AppointmentStatus.Confirmed);
        var first = await SeedAppointmentAsync(world.Factory, tenant.IdText, location, resource, service, customer, nine, AppointmentStatus.Pending);
        await SeedAppointmentAsync(world.Factory, tenant.IdText, location, resource, service, customer, nine.AddMinutes(60), AppointmentStatus.Cancelled);

        var desk = await GetJsonAsync(tenant.Admin, Url(tenant, day));

        Assert.Equal(
            [first, second],
            desk.GetProperty("upcoming").EnumerateArray().Select(row => row.GetProperty("appointmentId").GetGuid()));
        Assert.Equal(2, Counter(desk, "appointments"));
    }

    [Fact]
    public async Task WithoutAppointmentReadOrInAnotherTenantItIs403()
    {
        var tenant = await NewTenantAsync(world.Factory);
        using var owner = tenant;
        var other = await NewTenantAsync(world.Factory, "Otra Clínica");
        using var otherOwner = other;
        using var noRead = tenant.Client(null, SchedulingPermissions.AppointmentManage);

        var withoutPermission = await noRead.GetAsync(Url(tenant), Ct);
        var crossTenant = await other.Admin.GetAsync(Url(tenant), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, withoutPermission.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crossTenant.StatusCode);
    }
}
