using System.Net;
using Modules.Scheduling.Application;
using static Modules.Scheduling.IntegrationTests.SchedulingApiHarness;

namespace Modules.Scheduling.IntegrationTests;

/// <summary>
/// <c>GET setup</c> y el aprovisionamiento del primer acceso (plan 2026-11-21, Tarea 7): la sede principal y las reglas nacen con la
/// primera petición, y el <c>layout</c> lo decide el backend contando sedes y recursos activos.
/// </summary>
public sealed class SchedulingSetupApiTests(SchedulingApiWorld world) : IClassFixture<SchedulingApiWorld>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheFirstSetupReadProvisionsTheMainLocationAndTheDefaultRules()
    {
        using var tenant = await NewTenantAsync(world.Factory, "Odontología Andina");

        var setup = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/setup");

        Assert.False(setup.GetProperty("hasService").GetBoolean());
        Assert.False(setup.GetProperty("hasResource").GetBoolean());
        Assert.False(setup.GetProperty("hasBlock").GetBoolean());

        var locations = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/locations");
        var main = Assert.Single(locations.EnumerateArray());
        Assert.True(main.GetProperty("isMain").GetBoolean());
        Assert.Equal("Odontología Andina", main.GetProperty("name").GetString());

        var settings = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");
        Assert.Equal("Automatic", settings.GetProperty("confirmationMode").GetString());
        Assert.Equal(120, settings.GetProperty("minNoticeMinutes").GetInt32());
        Assert.Equal(30, settings.GetProperty("horizonDays").GetInt32());
        Assert.Equal(24, settings.GetProperty("clientCancelCutoffHours").GetInt32());
        Assert.False(settings.GetProperty("reminderEnabled").GetBoolean());
        Assert.Equal(1, settings.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task ReadingTheSetupTwiceKeepsOneMainLocation()
    {
        using var tenant = await NewTenantAsync(world.Factory);

        await GetJsonAsync(tenant.Admin, $"{tenant.Url}/setup");
        await GetJsonAsync(tenant.Admin, $"{tenant.Url}/setup");

        var locations = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/locations");
        Assert.Single(locations.EnumerateArray());
    }

    [Fact]
    public async Task TheSetupFlagsFollowWhatTheTenantHasCreated()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        await CreatedAsync(tenant.Admin, $"{tenant.Url}/services", new { name = "Limpieza", durationMinutes = 30, bufferMinutes = 0, bookableByChannel = true });

        var withService = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/setup");
        Assert.True(withService.GetProperty("hasService").GetBoolean());
        Assert.False(withService.GetProperty("hasResource").GetBoolean());

        await CreatedAsync(tenant.Admin, $"{tenant.Url}/resources", new { displayName = "Dra. Torres" });

        var withBoth = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/setup");
        Assert.True(withBoth.GetProperty("hasService").GetBoolean());
        Assert.True(withBoth.GetProperty("hasResource").GetBoolean());
        Assert.False(withBoth.GetProperty("hasBlock").GetBoolean());
    }

    [Fact]
    public async Task LayoutIsPersonalWithOneLocationAndOneResource()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        await CreatedAsync(tenant.Admin, $"{tenant.Url}/resources", new { displayName = "Dra. Torres" });

        var setup = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/setup");

        Assert.Equal("Personal", setup.GetProperty("layout").GetString());
    }

    [Fact]
    public async Task LayoutBecomesConsolidatedWithTheSecondResource()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        await CreatedAsync(tenant.Admin, $"{tenant.Url}/resources", new { displayName = "Dra. Torres" });
        await CreatedAsync(tenant.Admin, $"{tenant.Url}/resources", new { displayName = "Dr. Rojas" });

        var setup = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/setup");

        Assert.Equal("Consolidated", setup.GetProperty("layout").GetString());
    }

    [Fact]
    public async Task LayoutGoesBackToPersonalWhenTheSecondResourceIsArchived()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        await CreatedAsync(tenant.Admin, $"{tenant.Url}/resources", new { displayName = "Dra. Torres" });
        var second = await CreatedAsync(tenant.Admin, $"{tenant.Url}/resources", new { displayName = "Dr. Rojas" });

        var archived = await tenant.Admin.PostAsync($"{tenant.Url}/resources/{second.GetProperty("id").GetGuid()}/archive", null, Ct);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);

        var setup = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/setup");
        Assert.Equal("Personal", setup.GetProperty("layout").GetString());
    }

    [Fact]
    public async Task SetupWithoutTheReadPermissionIsDenied()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        using var client = tenant.Client(null, SchedulingPermissions.AvailabilityManage);

        var response = await client.GetAsync($"{tenant.Url}/setup", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ARequestForAnotherTenantIsDeniedNotNotFound()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        using var other = await NewTenantAsync(world.Factory);

        // Quien opera en `tenant` apunta a la ruta de `other`: la política deja pasar el permiso y el handler responde 403.
        var response = await tenant.Admin.GetAsync($"{other.Url}/setup", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("authorization.denied", CodeOf(await JsonOfAsync(response)));
    }
}
