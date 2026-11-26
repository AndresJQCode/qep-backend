using System.Net;
using System.Net.Http.Json;
using Modules.Scheduling.Application;
using static Modules.Scheduling.IntegrationTests.SchedulingApiHarness;

namespace Modules.Scheduling.IntegrationTests;

/// <summary>Servicios (plan 2026-11-21, Tarea 7): lo que se agenda y cuánto dura. Todo es de <c>settings.manage</c>.</summary>
public sealed class SchedulingServicesApiTests(SchedulingApiWorld world) : IClassFixture<SchedulingApiWorld>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Cleaning(string name = "Limpieza dental", int duration = 30, int buffer = 10) =>
        new { name, durationMinutes = duration, bufferMinutes = buffer, bookableByChannel = true };

    [Fact]
    public async Task AServiceIsCreatedAndListed()
    {
        using var tenant = await NewTenantAsync(world.Factory);

        var created = await CreatedAsync(tenant.Admin, $"{tenant.Url}/services", Cleaning());

        Assert.Equal("Limpieza dental", created.GetProperty("name").GetString());
        Assert.Equal(30, created.GetProperty("durationMinutes").GetInt32());
        Assert.Equal(10, created.GetProperty("bufferMinutes").GetInt32());
        Assert.True(created.GetProperty("bookableByChannel").GetBoolean());
        Assert.False(created.GetProperty("archived").GetBoolean());
        Assert.Equal(1, created.GetProperty("version").GetInt64());
        var services = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/services");
        Assert.Equal(created.GetProperty("id").GetGuid(), Assert.Single(services.EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task AServiceIsEditedWithItsVersion()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var created = await CreatedAsync(tenant.Admin, $"{tenant.Url}/services", Cleaning());
        var url = $"{tenant.Url}/services/{created.GetProperty("id").GetGuid()}";

        var updated = await PutAsync(tenant.Admin, url, Cleaning("Limpieza profunda", 45, 15), "1");
        var stale = await PutAsync(tenant.Admin, url, Cleaning(), "1");
        var missing = await PutAsync(tenant.Admin, url, Cleaning(), null);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var body = await JsonOfAsync(updated);
        Assert.Equal(45, body.GetProperty("durationMinutes").GetInt32());
        Assert.Equal(2, body.GetProperty("version").GetInt64());
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
    }

    [Fact]
    public async Task ADurationThatIsNotAMultipleOfFiveIs422WithTheFieldMarked()
    {
        using var tenant = await NewTenantAsync(world.Factory);

        var response = await tenant.Admin.PostAsJsonAsync($"{tenant.Url}/services", Cleaning(duration: 32, buffer: -5), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await JsonOfAsync(response);
        Assert.Equal("validation.failed", CodeOf(problem));
        Assert.True(HasError(problem, "durationMinutes"));
        Assert.True(HasError(problem, "bufferMinutes"));
    }

    [Fact]
    public async Task AServiceWithoutANameIs422()
    {
        using var tenant = await NewTenantAsync(world.Factory);

        var response = await tenant.Admin.PostAsJsonAsync($"{tenant.Url}/services", Cleaning(name: " "), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True(HasError(await JsonOfAsync(response), "name"));
    }

    [Fact]
    public async Task AServiceWithFutureAppointmentsCannotBeArchived()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var locations = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/locations");
        var mainId = locations.EnumerateArray().Single().GetProperty("id").GetGuid();
        var resource = await CreatedAsync(tenant.Admin, $"{tenant.Url}/resources", new { displayName = "Dra. Torres" });
        var service = await CreatedAsync(tenant.Admin, $"{tenant.Url}/services", Cleaning());
        var serviceId = service.GetProperty("id").GetGuid();
        await SeedFutureAppointmentAsync(world.Factory, tenant.IdText, mainId, resource.GetProperty("id").GetGuid(), serviceId);

        var response = await tenant.Admin.PostAsync($"{tenant.Url}/services/{serviceId}/archive", null, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("scheduling.service.in_use", CodeOf(await JsonOfAsync(response)));
    }

    [Fact]
    public async Task AServiceWithoutAppointmentsIsArchived()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var service = await CreatedAsync(tenant.Admin, $"{tenant.Url}/services", Cleaning());

        var response = await tenant.Admin.PostAsync($"{tenant.Url}/services/{service.GetProperty("id").GetGuid()}/archive", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await JsonOfAsync(response)).GetProperty("archived").GetBoolean());
    }

    [Fact]
    public async Task WithoutSettingsManageAServiceCannotBeCreated()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        using var manager = tenant.Client(
            null, SchedulingPermissions.AppointmentRead, SchedulingPermissions.AppointmentManage, SchedulingPermissions.AvailabilityManage);

        var response = await manager.PostAsJsonAsync($"{tenant.Url}/services", Cleaning(), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AReaderListsTheServicesButCannotCreateThem()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        await CreatedAsync(tenant.Admin, $"{tenant.Url}/services", Cleaning());
        using var reader = tenant.Client(null, SchedulingPermissions.AppointmentRead);

        var list = await reader.GetAsync($"{tenant.Url}/services", Ct);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }
}
