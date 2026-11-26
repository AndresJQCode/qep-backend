using System.Net;
using Modules.Scheduling.Application;
using static Modules.Scheduling.IntegrationTests.SchedulingApiHarness;

namespace Modules.Scheduling.IntegrationTests;

/// <summary>
/// <c>GET availability</c> (plan 2026-11-21, Tarea 9): the engine's free starts with the seats left and the resources that can take
/// each one, already named, inside the scope of whoever asks and the rules of the tenant.
/// </summary>
public sealed class SchedulingAvailabilityApiTests(SchedulingApiWorld world) : IClassFixture<SchedulingApiWorld>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Url(SchedulingTenant tenant, Guid service, DateOnly day, Guid? resource = null, DateOnly? to = null) =>
        $"{tenant.Url}/availability?serviceId={service}&from={day:yyyy-MM-dd}&to={(to ?? day):yyyy-MM-dd}"
        + (resource is { } id ? $"&resourceId={id}" : string.Empty);

    private async Task<(SchedulingTenant Tenant, Guid Resource, Guid Service, DateOnly Day)> SceneAsync(int capacity = 1, string? mode = null)
    {
        var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var service = await CreateServiceAsync(tenant, "Limpieza", 30);
        var day = FutureMonday();
        await CreatedAsync(tenant.Admin, $"{tenant.Url}/blocks", OneOffBlockBody(day, [resource], [service], "Jornada", capacity, mode));
        return (tenant, resource, service, day);
    }

    [Fact]
    public async Task TheFreeStartsComeWithTheirSeatsAndTheNamedResources()
    {
        var (tenant, resource, service, day) = await SceneAsync();
        using var owner = tenant;

        var availability = await GetJsonAsync(tenant.Admin, Url(tenant, service, day));

        Assert.Equal("America/Bogota", availability.GetProperty("timeZone").GetString());
        var slots = availability.GetProperty("slots").EnumerateArray().ToArray();
        // The block opens 09:00 to 12:00 local and the service lasts 30 minutes: six starts.
        Assert.Equal(6, slots.Length);
        Assert.Equal(NineOClock(day), slots[0].GetProperty("startsAt").GetDateTimeOffset());
        Assert.Equal(NineOClock(day).AddMinutes(30), slots[0].GetProperty("endsAt").GetDateTimeOffset());
        Assert.Equal(1, slots[0].GetProperty("seatsLeft").GetInt32());
        var option = Assert.Single(slots[0].GetProperty("resources").EnumerateArray());
        Assert.Equal(resource, option.GetProperty("resourceId").GetGuid());
        Assert.Equal("Dra. Torres", option.GetProperty("displayName").GetString());
        Assert.Equal(1, option.GetProperty("seatsLeft").GetInt32());
    }

    [Fact]
    public async Task TheHorizonOfTheRulesCutsTheFarthestDays()
    {
        var (tenant, _, service, day) = await SceneAsync();
        using var owner = tenant;
        var settings = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");
        var rules = new
        {
            confirmationMode = "Automatic",
            minNoticeMinutes = 0,
            horizonDays = 7,
            clientCancelCutoffHours = 24,
            reminderEnabled = false,
            reminderOffsetHours = 24,
        };
        (await PutAsync(tenant.Admin, $"{tenant.Url}/settings", rules, VersionOf(settings))).EnsureSuccessStatusCode();

        var availability = await GetJsonAsync(tenant.Admin, Url(tenant, service, day));

        Assert.Empty(availability.GetProperty("slots").EnumerateArray());
    }

    [Fact]
    public async Task AResourceFilterNarrowsTheOptionsToThatResource()
    {
        var (tenant, resource, service, day) = await SceneAsync();
        using var owner = tenant;
        var other = await CreateResourceAsync(tenant, "Dr. Mora");
        await CreatedAsync(tenant.Admin, $"{tenant.Url}/blocks", OneOffBlockBody(day, [other], [service], "Otra jornada"));

        var all = await GetJsonAsync(tenant.Admin, Url(tenant, service, day));
        var onlyMora = await GetJsonAsync(tenant.Admin, Url(tenant, service, day, other));

        Assert.Equal(2, all.GetProperty("slots")[0].GetProperty("resources").GetArrayLength());
        Assert.Equal(2, all.GetProperty("slots")[0].GetProperty("seatsLeft").GetInt32());
        var option = Assert.Single(onlyMora.GetProperty("slots")[0].GetProperty("resources").EnumerateArray());
        Assert.Equal(other, option.GetProperty("resourceId").GetGuid());
        Assert.NotEqual(resource, other);
    }

    [Fact]
    public async Task AMemberWithoutReadAllOnlySeesItsOwnResourceAndGets403ForAnother()
    {
        var tenant = await NewTenantAsync(world.Factory);
        using var owner = tenant;
        var (own, provider) = await CreateProviderAsync(tenant, "torres@example.com", "Torres");
        using var providerClient = provider;
        var other = await CreateResourceAsync(tenant, "Dr. Mora");
        var service = await CreateServiceAsync(tenant, "Limpieza", 30);
        var day = FutureMonday();
        await CreatedAsync(tenant.Admin, $"{tenant.Url}/blocks", OneOffBlockBody(day, [own, other], [service], "Jornada"));

        var mine = await GetJsonAsync(provider, Url(tenant, service, day));
        var foreign = await provider.GetAsync(Url(tenant, service, day, other), Ct);

        var option = Assert.Single(mine.GetProperty("slots")[0].GetProperty("resources").EnumerateArray());
        Assert.Equal(own, option.GetProperty("resourceId").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal("authorization.denied", CodeOf(await JsonOfAsync(foreign)));
    }

    [Fact]
    public async Task AnUnknownServiceIs404AndAnArchivedOneToo()
    {
        var (tenant, _, service, day) = await SceneAsync();
        using var owner = tenant;
        var archived = await CreateServiceAsync(tenant, "Vieja", 30);
        (await tenant.Admin.PostAsync($"{tenant.Url}/services/{archived}/archive", null, Ct)).EnsureSuccessStatusCode();

        var unknown = await tenant.Admin.GetAsync(Url(tenant, Guid.CreateVersion7(), day), Ct);
        var gone = await tenant.Admin.GetAsync(Url(tenant, archived, day), Ct);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("scheduling.service.not_found", CodeOf(await JsonOfAsync(unknown)));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.NotEqual(service, archived);
    }

    [Fact]
    public async Task ABackwardsRangeIs422WithTheFieldMarked()
    {
        var (tenant, _, service, day) = await SceneAsync();
        using var owner = tenant;

        var response = await tenant.Admin.GetAsync(Url(tenant, service, day, to: day.AddDays(-1)), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True(HasError(await JsonOfAsync(response), "to"));
    }

    [Fact]
    public async Task WithoutAppointmentReadOrInAnotherTenantItIs403()
    {
        var (tenant, _, service, day) = await SceneAsync();
        using var owner = tenant;
        using var noRead = tenant.Client(null, SchedulingPermissions.AppointmentManage);
        var stranger = await NewTenantAsync(world.Factory);
        using var strangerTenant = stranger;

        var withoutPermission = await noRead.GetAsync(Url(tenant, service, day), Ct);
        var crossTenant = await stranger.Admin.GetAsync(Url(tenant, service, day), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, withoutPermission.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crossTenant.StatusCode);
        Assert.Equal("authorization.denied", CodeOf(await JsonOfAsync(crossTenant)));
    }

    [Fact]
    public async Task APublishedSessionsBlockOffersOnlyThePublishedOnes()
    {
        var (tenant, resource, service, day) = await SceneAsync(capacity: 5, mode: "PublishedSessions");
        using var owner = tenant;
        var blocks = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/blocks");
        var block = blocks.EnumerateArray().Single().GetProperty("id").GetGuid();
        Assert.Empty((await GetJsonAsync(tenant.Admin, Url(tenant, service, day))).GetProperty("slots").EnumerateArray());

        var published = await CreatedAsync(
            tenant.Admin, $"{tenant.Url}/sessions", new { blockId = block, resourceId = resource, serviceId = service, startsAt = NineOClock(day).AddHours(1) });

        var availability = await GetJsonAsync(tenant.Admin, Url(tenant, service, day));
        var slot = Assert.Single(availability.GetProperty("slots").EnumerateArray());
        Assert.Equal(NineOClock(day).AddHours(1), slot.GetProperty("startsAt").GetDateTimeOffset());
        Assert.Equal(5, slot.GetProperty("seatsLeft").GetInt32());
        Assert.Equal(published.GetProperty("id").GetGuid(), slot.GetProperty("resources")[0].GetProperty("sessionId").GetGuid());
    }
}
