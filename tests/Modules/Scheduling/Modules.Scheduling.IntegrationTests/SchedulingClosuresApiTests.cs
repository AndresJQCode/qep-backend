using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Scheduling.Application;
using Modules.Scheduling.Domain;
using static Modules.Scheduling.IntegrationTests.SchedulingApiHarness;

namespace Modules.Scheduling.IntegrationTests;

/// <summary>
/// Bloqueos (plan 2026-11-21, Tarea 8; spec D-12): cierran un recurso, o toda la sede con el recurso nulo. Uno que cruza citas vigentes se
/// rechaza hasta reprogramarlas; nada se mueve en silencio. Quien no ve todas las agendas sólo bloquea su recurso, nunca la sede entera.
/// </summary>
public sealed class SchedulingClosuresApiTests(SchedulingApiWorld world) : IClassFixture<SchedulingApiWorld>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // La cita sembrada por el harness cae tres días después de hoy a las 14:00Z y dura 30 minutos.
    private static DateTimeOffset SeededStart => new(DateTime.UtcNow.Date.AddDays(3).AddHours(14), TimeSpan.Zero);

    private static object Closure(Guid? resourceId, DateTimeOffset startsAt, DateTimeOffset endsAt, string? reason = "Vacaciones") =>
        new { resourceId, startsAt, endsAt, reason };

    private static Task<JsonElement> CreateClosureAsync(HttpClient client, SchedulingTenant tenant, Guid? resourceId, int daysFromNow = 30) =>
        CreatedAsync(
            client,
            $"{tenant.Url}/closures",
            Closure(resourceId, new DateTimeOffset(DateTime.UtcNow.Date.AddDays(daysFromNow).AddHours(13), TimeSpan.Zero),
                new DateTimeOffset(DateTime.UtcNow.Date.AddDays(daysFromNow).AddHours(17), TimeSpan.Zero)));

    [Fact]
    public async Task AClosureOfOneResourceIsCreatedAndListed()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var mainId = await MainLocationIdAsync(tenant);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(30).AddHours(13), TimeSpan.Zero);

        var created = await CreatedAsync(
            tenant.Admin, $"{tenant.Url}/closures", Closure(resource, start.ToOffset(TimeSpan.FromHours(-5)), start.AddHours(4)));

        Assert.Equal(resource, created.GetProperty("resourceId").GetGuid());
        Assert.Equal(mainId, created.GetProperty("locationId").GetGuid());
        Assert.Equal("Vacaciones", created.GetProperty("reason").GetString());
        Assert.Equal(start, created.GetProperty("startsAt").GetDateTimeOffset());
        Assert.Equal(1, created.GetProperty("version").GetInt64());
        var listed = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/closures");
        Assert.Equal(created.GetProperty("id").GetGuid(), Assert.Single(listed.EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task AClosureWithoutAResourceClosesTheMainLocation()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var mainId = await MainLocationIdAsync(tenant);

        var created = await CreateClosureAsync(tenant.Admin, tenant, null);

        Assert.Equal(JsonValueKind.Null, created.GetProperty("resourceId").ValueKind);
        Assert.Equal(mainId, created.GetProperty("locationId").GetGuid());
    }

    [Fact]
    public async Task AClosureOverlappingAnActiveAppointmentIsRejected()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var mainId = await MainLocationIdAsync(tenant);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var other = await CreateResourceAsync(tenant, "Dr. Rojas");
        var service = await CreateServiceAsync(tenant);
        await SeedFutureAppointmentAsync(world.Factory, tenant.IdText, mainId, resource, service);

        var sameResource = await tenant.Admin.PostAsJsonAsync(
            $"{tenant.Url}/closures", Closure(resource, SeededStart.AddHours(-1), SeededStart.AddHours(1)), Ct);
        var wholeLocation = await tenant.Admin.PostAsJsonAsync(
            $"{tenant.Url}/closures", Closure(null, SeededStart.AddHours(-1), SeededStart.AddHours(1)), Ct);
        var anotherResource = await tenant.Admin.PostAsJsonAsync(
            $"{tenant.Url}/closures", Closure(other, SeededStart.AddHours(-1), SeededStart.AddHours(1)), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, sameResource.StatusCode);
        Assert.Equal("scheduling.closure.overlaps_appointments", CodeOf(await JsonOfAsync(sameResource)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wholeLocation.StatusCode);
        Assert.Equal("scheduling.closure.overlaps_appointments", CodeOf(await JsonOfAsync(wholeLocation)));
        // Otro recurso de la misma sede no tiene la cita: su bloqueo es legítimo.
        Assert.Equal(HttpStatusCode.Created, anotherResource.StatusCode);
        Assert.Single((await GetJsonAsync(tenant.Admin, $"{tenant.Url}/closures")).EnumerateArray());
    }

    [Fact]
    public async Task ACancelledAppointmentOrOneThatOnlyTouchesTheEdgeDoesNotBlockTheClosure()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var mainId = await MainLocationIdAsync(tenant);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var service = await CreateServiceAsync(tenant);
        await SeedFutureAppointmentAsync(world.Factory, tenant.IdText, mainId, resource, service);

        // La cita dura [14:00Z, 14:30Z): un bloqueo que empieza justo cuando termina no la cruza.
        var touching = await tenant.Admin.PostAsJsonAsync(
            $"{tenant.Url}/closures", Closure(resource, SeededStart.AddMinutes(30), SeededStart.AddHours(2)), Ct);

        Assert.Equal(HttpStatusCode.Created, touching.StatusCode);

        var cancelled = await CreateResourceAsync(tenant, "Dr. Rojas");
        await SeedFutureAppointmentAsync(world.Factory, tenant.IdText, mainId, cancelled, service, AppointmentStatus.Cancelled);
        var overCancelled = await tenant.Admin.PostAsJsonAsync(
            $"{tenant.Url}/closures", Closure(cancelled, SeededStart.AddHours(-1), SeededStart.AddHours(1)), Ct);

        Assert.Equal(HttpStatusCode.Created, overCancelled.StatusCode);
    }

    [Fact]
    public async Task AClosureMustEndAfterItStartsAndItsReasonIsShort()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(30).AddHours(13), TimeSpan.Zero);

        var backwards = await tenant.Admin.PostAsJsonAsync($"{tenant.Url}/closures", Closure(resource, start, start.AddHours(-1)), Ct);
        var longReason = await tenant.Admin.PostAsJsonAsync(
            $"{tenant.Url}/closures", Closure(resource, start, start.AddHours(1), new string('x', 201)), Ct);
        var unknownResource = await tenant.Admin.PostAsJsonAsync(
            $"{tenant.Url}/closures", Closure(Guid.CreateVersion7(), start, start.AddHours(1)), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, backwards.StatusCode);
        Assert.True(HasError(await JsonOfAsync(backwards), "endsAt"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, longReason.StatusCode);
        Assert.True(HasError(await JsonOfAsync(longReason), "reason"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownResource.StatusCode);
        Assert.True(HasError(await JsonOfAsync(unknownResource), "resourceId"));
    }

    [Fact]
    public async Task AProviderClosesOnlyItsOwnResourceNeverTheWholeLocationNorAnotherResource()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var (own, provider) = await CreateProviderAsync(tenant, "torres@example.com", "Torres");
        using var _ = provider;
        var another = await CreateResourceAsync(tenant, "Dr. Rojas");

        var mine = await CreateClosureAsync(provider, tenant, own);
        var theirs = await provider.PostAsJsonAsync(
            $"{tenant.Url}/closures", Closure(another, DateTimeOffset.UtcNow.AddDays(40), DateTimeOffset.UtcNow.AddDays(41)), Ct);
        var wholeLocation = await provider.PostAsJsonAsync(
            $"{tenant.Url}/closures", Closure(null, DateTimeOffset.UtcNow.AddDays(40), DateTimeOffset.UtcNow.AddDays(41)), Ct);

        Assert.Equal(own, mine.GetProperty("resourceId").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, theirs.StatusCode);
        Assert.Equal("authorization.denied", CodeOf(await JsonOfAsync(theirs)));
        Assert.Equal(HttpStatusCode.Forbidden, wholeLocation.StatusCode);
    }

    [Fact]
    public async Task AProviderListsItsClosuresAndTheLocationWideOnesButNotAnotherResources()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var (own, provider) = await CreateProviderAsync(tenant, "torres@example.com", "Torres");
        using var _ = provider;
        var another = await CreateResourceAsync(tenant, "Dr. Rojas");
        var mine = await CreateClosureAsync(tenant.Admin, tenant, own, 30);
        var locationWide = await CreateClosureAsync(tenant.Admin, tenant, null, 31);
        await CreateClosureAsync(tenant.Admin, tenant, another, 32);

        var visible = await GetJsonAsync(provider, $"{tenant.Url}/closures");
        var all = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/closures");

        Assert.Equal(
            new[] { mine.GetProperty("id").GetGuid(), locationWide.GetProperty("id").GetGuid() }.Order().ToArray(),
            visible.EnumerateArray().Select(closure => closure.GetProperty("id").GetGuid()).Order().ToArray());
        Assert.Equal(3, all.GetArrayLength());
    }

    [Fact]
    public async Task AClosureIsDeletedAndDeletingItAgainIsNotFound()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var created = await CreateClosureAsync(tenant.Admin, tenant, resource);
        var url = $"{tenant.Url}/closures/{created.GetProperty("id").GetGuid()}";

        var deleted = await tenant.Admin.DeleteAsync(url, Ct);
        var again = await tenant.Admin.DeleteAsync(url, Ct);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal("scheduling.closure.not_found", CodeOf(await JsonOfAsync(again)));
        Assert.Equal(0, (await GetJsonAsync(tenant.Admin, $"{tenant.Url}/closures")).GetArrayLength());
    }

    [Fact]
    public async Task AProviderCannotDeleteAnotherResourcesClosureNorALocationWideOne()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var (own, provider) = await CreateProviderAsync(tenant, "torres@example.com", "Torres");
        using var _ = provider;
        var another = await CreateResourceAsync(tenant, "Dr. Rojas");
        var foreign = await CreateClosureAsync(tenant.Admin, tenant, another, 30);
        var locationWide = await CreateClosureAsync(tenant.Admin, tenant, null, 31);
        var mine = await CreateClosureAsync(tenant.Admin, tenant, own, 32);

        var deleteForeign = await provider.DeleteAsync($"{tenant.Url}/closures/{foreign.GetProperty("id").GetGuid()}", Ct);
        var deleteWide = await provider.DeleteAsync($"{tenant.Url}/closures/{locationWide.GetProperty("id").GetGuid()}", Ct);
        var deleteMine = await provider.DeleteAsync($"{tenant.Url}/closures/{mine.GetProperty("id").GetGuid()}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, deleteForeign.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deleteWide.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleteMine.StatusCode);
    }

    [Fact]
    public async Task WithoutAvailabilityManageNoClosureRouteIsAvailable()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var created = await CreateClosureAsync(tenant.Admin, tenant, resource);
        using var reader = tenant.Client(null, SchedulingPermissions.AppointmentRead, SchedulingPermissions.AgendaReadAll);

        var list = await reader.GetAsync($"{tenant.Url}/closures", Ct);
        var create = await reader.PostAsJsonAsync(
            $"{tenant.Url}/closures", Closure(resource, DateTimeOffset.UtcNow.AddDays(40), DateTimeOffset.UtcNow.AddDays(41)), Ct);
        var delete = await reader.DeleteAsync($"{tenant.Url}/closures/{created.GetProperty("id").GetGuid()}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    [Fact]
    public async Task AClosureRouteOfAnotherTenantIsForbidden()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        using var other = await NewTenantAsync(world.Factory);

        var response = await other.Admin.GetAsync($"{tenant.Url}/closures", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
