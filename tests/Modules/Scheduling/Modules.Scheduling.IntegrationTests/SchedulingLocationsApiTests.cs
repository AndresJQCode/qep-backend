using System.Net;
using System.Net.Http.Json;
using Modules.Scheduling.Application;
using static Modules.Scheduling.IntegrationTests.SchedulingApiHarness;

namespace Modules.Scheduling.IntegrationTests;

/// <summary>Sedes (plan 2026-11-21, Tarea 7): una sola principal, que no se archiva; las demás las crea quien configura la estructura.</summary>
public sealed class SchedulingLocationsApiTests(SchedulingApiWorld world) : IClassFixture<SchedulingApiWorld>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Guid> MainLocationIdAsync(SchedulingTenant tenant)
    {
        var locations = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/locations");
        return locations.EnumerateArray().Single(location => location.GetProperty("isMain").GetBoolean()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task ASecondMainLocationCannotBeCreated()
    {
        using var tenant = await NewTenantAsync(world.Factory);

        // El cuerpo ni siquiera tiene un campo para pedirla: una sede nueva nunca nace principal, mande lo que mande el cliente.
        var created = await CreatedAsync(tenant.Admin, $"{tenant.Url}/locations", new { name = "Sede Norte", isMain = true });

        Assert.False(created.GetProperty("isMain").GetBoolean());
        var locations = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/locations");
        Assert.Equal(2, locations.GetArrayLength());
        Assert.Single(locations.EnumerateArray(), location => location.GetProperty("isMain").GetBoolean());
    }

    [Fact]
    public async Task TheMainLocationCannotBeArchived()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var mainId = await MainLocationIdAsync(tenant);

        var response = await tenant.Admin.PostAsync($"{tenant.Url}/locations/{mainId}/archive", null, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("scheduling.location.main_cannot_be_archived", CodeOf(await JsonOfAsync(response)));
        var locations = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/locations");
        Assert.All(locations.EnumerateArray(), location => Assert.False(location.GetProperty("archived").GetBoolean()));
    }

    [Fact]
    public async Task ASecondaryLocationIsRenamedWithItsVersionAndArchived()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var created = await CreatedAsync(tenant.Admin, $"{tenant.Url}/locations", new { name = "Sede Norte" });
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(1, created.GetProperty("version").GetInt64());

        var renamed = await PutAsync(tenant.Admin, $"{tenant.Url}/locations/{id}", new { name = "Sede Centro" }, "1");
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var body = await JsonOfAsync(renamed);
        Assert.Equal("Sede Centro", body.GetProperty("name").GetString());
        Assert.Equal(2, body.GetProperty("version").GetInt64());

        var archived = await tenant.Admin.PostAsync($"{tenant.Url}/locations/{id}/archive", null, Ct);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        Assert.True((await JsonOfAsync(archived)).GetProperty("archived").GetBoolean());
    }

    [Fact]
    public async Task AStaleVersionOnALocationIs412AndAMissingOneIs428()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var created = await CreatedAsync(tenant.Admin, $"{tenant.Url}/locations", new { name = "Sede Norte" });
        var id = created.GetProperty("id").GetGuid();

        var stale = await PutAsync(tenant.Admin, $"{tenant.Url}/locations/{id}", new { name = "Otra" }, "7");
        var missing = await PutAsync(tenant.Admin, $"{tenant.Url}/locations/{id}", new { name = "Otra" }, null);

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
    }

    [Fact]
    public async Task ABlankLocationNameIs422WithTheFieldMarked()
    {
        using var tenant = await NewTenantAsync(world.Factory);

        var response = await tenant.Admin.PostAsJsonAsync($"{tenant.Url}/locations", new { name = "  " }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True(HasError(await JsonOfAsync(response), "name"));
    }

    [Fact]
    public async Task WithoutSettingsManageALocationCannotBeCreated()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        using var reader = tenant.Client(null, SchedulingPermissions.AppointmentRead, SchedulingPermissions.AvailabilityManage);

        var response = await reader.PostAsJsonAsync($"{tenant.Url}/locations", new { name = "Sede Norte" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ALocationOfAnotherTenantDoesNotExistInsideTheCallersTenant()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        using var other = await NewTenantAsync(world.Factory);
        var otherLocation = await CreatedAsync(other.Admin, $"{other.Url}/locations", new { name = "Ajena" });

        // El tenant de la ruta es el propio, pero la sede es de otro: dentro de su tenant no existe y responde 404, sin revelar nada.
        var response = await PutAsync(
            tenant.Admin, $"{tenant.Url}/locations/{otherLocation.GetProperty("id").GetGuid()}", new { name = "Mía" }, "1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
