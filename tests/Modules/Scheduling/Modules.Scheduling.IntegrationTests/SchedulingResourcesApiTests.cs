using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Scheduling.Application;
using Modules.Scheduling.Domain;
using static Modules.Scheduling.IntegrationTests.SchedulingApiHarness;

namespace Modules.Scheduling.IntegrationTests;

/// <summary>
/// Recursos (plan 2026-11-21, Tarea 7): el alta y la estructura son de <c>settings.manage</c>; los datos y el vínculo de <b>su</b> recurso,
/// de <c>availability.manage</c>. Un recurso fuera de alcance responde 403, nunca 404.
/// </summary>
public sealed class SchedulingResourcesApiTests(SchedulingApiWorld world) : IClassFixture<SchedulingApiWorld>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Guid> MainLocationIdAsync(SchedulingTenant tenant)
    {
        var locations = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/locations");
        return locations.EnumerateArray().Single(location => location.GetProperty("isMain").GetBoolean()).GetProperty("id").GetGuid();
    }

    private static Task<JsonElement> ResourceAsync(SchedulingTenant tenant, string name, Guid? memberId = null) =>
        CreatedAsync(tenant.Admin, $"{tenant.Url}/resources", new { displayName = name, memberId });

    [Fact]
    public async Task AResourceWithoutALocationBelongsToTheMainOne()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var mainId = await MainLocationIdAsync(tenant);

        var created = await ResourceAsync(tenant, "Dra. Torres");

        Assert.Equal(mainId, created.GetProperty("locationId").GetGuid());
        Assert.Equal("Dra. Torres", created.GetProperty("displayName").GetString());
        Assert.False(created.GetProperty("archived").GetBoolean());
        Assert.Equal(1, created.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task AResourceLinkedToAMemberOfTheTenantCarriesTheMemberName()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var (memberId, _) = await SeedMemberAsync(world.Factory, tenant.IdText, "torres@example.com", "Laura Torres");

        var created = await ResourceAsync(tenant, "Dra. Torres", memberId);

        Assert.Equal(memberId, created.GetProperty("memberId").GetGuid());
        Assert.Equal("Laura Torres", created.GetProperty("memberName").GetString());
    }

    [Fact]
    public async Task AMemberOfAnotherTenantCannotBeLinked()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        using var other = await NewTenantAsync(world.Factory);
        var (foreignMember, _) = await SeedMemberAsync(world.Factory, other.IdText, "ajena@example.com", "Persona Ajena");

        var response = await tenant.Admin.PostAsJsonAsync(
            $"{tenant.Url}/resources", new { displayName = "Dra. Torres", memberId = foreignMember }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await JsonOfAsync(response);
        Assert.Equal("validation.failed", CodeOf(problem));
        Assert.True(HasError(problem, "memberId"));
    }

    [Fact]
    public async Task TheSameMemberCannotAttendTwoResourcesOfOneLocation()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var (memberId, _) = await SeedMemberAsync(world.Factory, tenant.IdText, "torres@example.com", "Laura Torres");
        await ResourceAsync(tenant, "Consultorio 1", memberId);

        var response = await tenant.Admin.PostAsJsonAsync(
            $"{tenant.Url}/resources", new { displayName = "Consultorio 2", memberId }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True(HasError(await JsonOfAsync(response), "memberId"));
    }

    [Fact]
    public async Task AResourceIsEditedWithItsVersion()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var created = await ResourceAsync(tenant, "Dra. Torres");
        var url = $"{tenant.Url}/resources/{created.GetProperty("id").GetGuid()}";

        var updated = await PutAsync(tenant.Admin, url, new { displayName = "Dra. Laura Torres" }, "1");
        var stale = await PutAsync(tenant.Admin, url, new { displayName = "Otra" }, "1");
        var missing = await PutAsync(tenant.Admin, url, new { displayName = "Otra" }, null);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(2, (await JsonOfAsync(updated)).GetProperty("version").GetInt64());
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
    }

    [Fact]
    public async Task AResourceCannotBeArchivedWithFutureAppointments()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var mainId = await MainLocationIdAsync(tenant);
        var resource = await ResourceAsync(tenant, "Dra. Torres");
        var service = await CreatedAsync(
            tenant.Admin, $"{tenant.Url}/services", new { name = "Limpieza", durationMinutes = 30, bufferMinutes = 0, bookableByChannel = true });
        var resourceId = resource.GetProperty("id").GetGuid();
        await SeedFutureAppointmentAsync(world.Factory, tenant.IdText, mainId, resourceId, service.GetProperty("id").GetGuid());

        var response = await tenant.Admin.PostAsync($"{tenant.Url}/resources/{resourceId}/archive", null, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("scheduling.resource.has_future_appointments", CodeOf(await JsonOfAsync(response)));
        var resources = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/resources");
        Assert.False(Assert.Single(resources.EnumerateArray()).GetProperty("archived").GetBoolean());
    }

    [Fact]
    public async Task ACancelledFutureAppointmentDoesNotBlockArchivingTheResource()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var mainId = await MainLocationIdAsync(tenant);
        var resource = await ResourceAsync(tenant, "Dra. Torres");
        var service = await CreatedAsync(
            tenant.Admin, $"{tenant.Url}/services", new { name = "Limpieza", durationMinutes = 30, bufferMinutes = 0, bookableByChannel = true });
        var resourceId = resource.GetProperty("id").GetGuid();
        await SeedFutureAppointmentAsync(
            world.Factory, tenant.IdText, mainId, resourceId, service.GetProperty("id").GetGuid(), AppointmentStatus.Cancelled);

        var response = await tenant.Admin.PostAsync($"{tenant.Url}/resources/{resourceId}/archive", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await JsonOfAsync(response)).GetProperty("archived").GetBoolean());
    }

    [Fact]
    public async Task WithoutSettingsManageAResourceCannotBeCreatedOrArchived()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var resource = await ResourceAsync(tenant, "Dra. Torres");
        using var reader = tenant.Client(null, SchedulingPermissions.AppointmentRead, SchedulingPermissions.AvailabilityManage);

        var create = await reader.PostAsJsonAsync($"{tenant.Url}/resources", new { displayName = "Otro" }, Ct);
        var archive = await reader.PostAsync($"{tenant.Url}/resources/{resource.GetProperty("id").GetGuid()}/archive", null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, archive.StatusCode);
    }

    [Fact]
    public async Task AProviderEditsTheDataOfItsOwnResourceButNotAnotherOne()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var (memberId, userId) = await SeedMemberAsync(world.Factory, tenant.IdText, "torres@example.com", "Laura Torres");
        var own = await ResourceAsync(tenant, "Dra. Torres", memberId);
        var another = await ResourceAsync(tenant, "Dr. Rojas");
        using var provider = tenant.Client(
            userId.ToString(), SchedulingPermissions.AppointmentRead, SchedulingPermissions.AvailabilityManage);

        var mine = await PutAsync(
            provider, $"{tenant.Url}/resources/{own.GetProperty("id").GetGuid()}",
            new { displayName = "Dra. Laura", memberId }, "1");
        var theirs = await PutAsync(
            provider, $"{tenant.Url}/resources/{another.GetProperty("id").GetGuid()}", new { displayName = "Dr. Rojas B." }, "1");

        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal("Dra. Laura", (await JsonOfAsync(mine)).GetProperty("displayName").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, theirs.StatusCode);
        Assert.Equal("authorization.denied", CodeOf(await JsonOfAsync(theirs)));
    }

    [Fact]
    public async Task AProviderWithoutReadAllListsOnlyItsOwnResources()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var (memberId, userId) = await SeedMemberAsync(world.Factory, tenant.IdText, "torres@example.com", "Laura Torres");
        await ResourceAsync(tenant, "Dra. Torres", memberId);
        await ResourceAsync(tenant, "Dr. Rojas");
        using var provider = tenant.Client(
            userId.ToString(), SchedulingPermissions.AppointmentRead, SchedulingPermissions.AvailabilityManage);
        using var secretary = tenant.Client(
            null, SchedulingPermissions.AppointmentRead, SchedulingPermissions.AgendaReadAll);

        var visibleToTheProvider = await GetJsonAsync(provider, $"{tenant.Url}/resources");
        var visibleToTheSecretary = await GetJsonAsync(secretary, $"{tenant.Url}/resources");

        Assert.Equal("Dra. Torres", Assert.Single(visibleToTheProvider.EnumerateArray()).GetProperty("displayName").GetString());
        Assert.Equal(2, visibleToTheSecretary.GetArrayLength());
    }

    [Fact]
    public async Task AMemberWithNoLinkedResourceCannotEditAny()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var resource = await ResourceAsync(tenant, "Dra. Torres");
        // El sujeto del cliente no tiene membresía en el tenant: su alcance es vacío.
        using var stranger = tenant.Client(
            Guid.CreateVersion7().ToString(), SchedulingPermissions.AppointmentRead, SchedulingPermissions.AvailabilityManage);

        var response = await PutAsync(
            stranger, $"{tenant.Url}/resources/{resource.GetProperty("id").GetGuid()}", new { displayName = "Intruso" }, "1");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
