using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Scheduling.Application;
using Modules.Scheduling.Domain;
using Modules.Scheduling.Infrastructure.Persistence;
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

    // Plan, correction 50: archiving a secondary location cascades (resources, blocks, future live appointments) and cannot be undone.
    private static async Task<(Guid LocationId, Guid ResourceId, Guid ServiceId, Guid BlockId, Guid Customer)> SetUpLocationAsync(SchedulingTenant tenant)
    {
        var location = (await CreatedAsync(tenant.Admin, $"{tenant.Url}/locations", new { name = "Sede Norte" })).GetProperty("id").GetGuid();
        var resource = await CreateResourceAsync(tenant, "Dra. Norte", locationId: location);
        var service = await CreateServiceAsync(tenant);
        var block = (await CreatedAsync(tenant.Admin, $"{tenant.Url}/blocks", WeeklyBlockBody([resource], [service]))).GetProperty("id").GetGuid();
        return (location, resource, service, block, await CreateCustomerAsync(tenant));
    }

    private async Task<(AppointmentStatus Status, string? Reason, int Events, int Booked)> ReadAppointmentAsync(Guid appointmentId)
    {
        await using var scope = world.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
        var id = new AppointmentId(appointmentId);
        var appointment = await db.Appointments.AsNoTracking().Include(value => value.Events).SingleAsync(value => value.Id == id, Ct);
        var session = await db.Sessions.AsNoTracking().SingleAsync(value => value.Id == appointment.SessionId, Ct);
        return (appointment.Status, appointment.CancelReason, appointment.Events.Count, session.BookedCount);
    }

    [Fact]
    public async Task ArchivingASecondaryLocationCancelsItsFutureAppointmentsAndArchivesItsResourcesAndBlocks()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var (location, resource, service, block, customer) = await SetUpLocationAsync(tenant);
        var future = await SeedAppointmentAsync(
            world.Factory, tenant.IdText, location, resource, service, customer, DateTimeOffset.UtcNow.AddDays(3), AppointmentStatus.Confirmed);
        var pending = await SeedAppointmentAsync(
            world.Factory, tenant.IdText, location, resource, service, customer, DateTimeOffset.UtcNow.AddDays(4), AppointmentStatus.Pending);
        var past = await SeedAppointmentAsync(
            world.Factory, tenant.IdText, location, resource, service, customer, DateTimeOffset.UtcNow.AddDays(-3), AppointmentStatus.Confirmed);
        var attended = await SeedAppointmentAsync(
            world.Factory, tenant.IdText, location, resource, service, customer, DateTimeOffset.UtcNow.AddDays(-2), AppointmentStatus.Completed);
        var mainResource = await CreateResourceAsync(tenant, "Dr. Principal");
        var mainBlock = (await CreatedAsync(tenant.Admin, $"{tenant.Url}/blocks", WeeklyBlockBody([mainResource], [service], "Otra"))).GetProperty("id").GetGuid();

        var response = await tenant.Admin.PostAsync($"{tenant.Url}/locations/{location}/archive", null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await JsonOfAsync(response)).GetProperty("archived").GetBoolean());
        foreach (var cancelled in new[] { future, pending })
        {
            var (status, reason, events, booked) = await ReadAppointmentAsync(cancelled);
            Assert.Equal((AppointmentStatus.Cancelled, ArchiveLocationHandler.CancelReason, 2, 0), (status, reason, events, booked));
        }

        // The past appointment and the closed one are history: untouched, with their seat and without a new line.
        Assert.Equal((AppointmentStatus.Confirmed, null, 1, 1), await ReadAppointmentAsync(past));
        Assert.Equal(AppointmentStatus.Completed, (await ReadAppointmentAsync(attended)).Status);

        var resources = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/resources");
        Assert.True(resources.EnumerateArray().Single(value => value.GetProperty("id").GetGuid() == resource).GetProperty("archived").GetBoolean());
        Assert.False(resources.EnumerateArray().Single(value => value.GetProperty("id").GetGuid() == mainResource).GetProperty("archived").GetBoolean());
        var blocks = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/blocks");
        Assert.True(blocks.EnumerateArray().Single(value => value.GetProperty("id").GetGuid() == block).GetProperty("archived").GetBoolean());
        Assert.False(blocks.EnumerateArray().Single(value => value.GetProperty("id").GetGuid() == mainBlock).GetProperty("archived").GetBoolean());

        // One audit entry per entity that really changed.
        Assert.Equal(1, await CountAuditEntriesAsync(world.Factory, tenant.IdText, SchedulingAuditActions.LocationArchived, SchedulingAuditActions.Resources.Location, location));
        Assert.Equal(1, await CountAuditEntriesAsync(world.Factory, tenant.IdText, SchedulingAuditActions.ResourceArchived, SchedulingAuditActions.Resources.Resource, resource));
        Assert.Equal(1, await CountAuditEntriesAsync(world.Factory, tenant.IdText, SchedulingAuditActions.BlockArchived, SchedulingAuditActions.Resources.Block, block));
        Assert.Equal(0, await CountAuditEntriesAsync(world.Factory, tenant.IdText, SchedulingAuditActions.BlockArchived, SchedulingAuditActions.Resources.Block, mainBlock));
        Assert.Equal(1, await CountAuditEntriesAsync(world.Factory, tenant.IdText, SchedulingAuditActions.AppointmentCancelled, SchedulingAuditActions.Resources.Appointment, future));
        Assert.Equal(0, await CountAuditEntriesAsync(world.Factory, tenant.IdText, SchedulingAuditActions.AppointmentCancelled, SchedulingAuditActions.Resources.Appointment, past));
    }

    [Fact]
    public async Task ArchivingALocationTwiceChangesAndAuditsNothingTheSecondTime()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var (location, resource, service, _, customer) = await SetUpLocationAsync(tenant);
        await SeedAppointmentAsync(
            world.Factory, tenant.IdText, location, resource, service, customer, DateTimeOffset.UtcNow.AddDays(3), AppointmentStatus.Confirmed);

        var first = await JsonOfAsync(await tenant.Admin.PostAsync($"{tenant.Url}/locations/{location}/archive", null, Ct));
        var second = await tenant.Admin.PostAsync($"{tenant.Url}/locations/{location}/archive", null, Ct);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(first.GetProperty("version").GetInt64(), (await JsonOfAsync(second)).GetProperty("version").GetInt64());
        Assert.Equal(1, await CountAuditEntriesAsync(world.Factory, tenant.IdText, SchedulingAuditActions.LocationArchived, SchedulingAuditActions.Resources.Location, location));
        Assert.Equal(1, await CountAuditEntriesAsync(world.Factory, tenant.IdText, SchedulingAuditActions.ResourceArchived, SchedulingAuditActions.Resources.Resource, resource));
    }

    [Fact]
    public async Task AnArchivedLocationCannotGetNewResourcesNorBookingsOnItsArchivedResources()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var (location, resource, service, _, customer) = await SetUpLocationAsync(tenant);
        await tenant.Admin.PostAsync($"{tenant.Url}/locations/{location}/archive", null, Ct);

        var book = await tenant.Admin.PostAsJsonAsync(
            $"{tenant.Url}/appointments", BookBody(service, DateTimeOffset.UtcNow.AddDays(7), customer, resource), Ct);
        var create = await tenant.Admin.PostAsJsonAsync($"{tenant.Url}/resources", new { displayName = "Nueva", locationId = location }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, book.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, create.StatusCode);
    }

    [Fact]
    public async Task ArchivingALocationOfAnotherTenantIs403AndChangesNothing()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        using var other = await NewTenantAsync(world.Factory);
        var (location, _, _, _, _) = await SetUpLocationAsync(other);

        var response = await tenant.Admin.PostAsync($"{other.Url}/locations/{location}/archive", null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var locations = await GetJsonAsync(other.Admin, $"{other.Url}/locations");
        Assert.All(locations.EnumerateArray(), value => Assert.False(value.GetProperty("archived").GetBoolean()));
    }

    [Fact]
    public async Task TheMainLocationIsRejectedWithoutCancellingItsAppointments()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var mainId = await MainLocationIdAsync(tenant);
        var resource = await CreateResourceAsync(tenant, "Dr. Principal");
        var service = await CreateServiceAsync(tenant);
        var customer = await CreateCustomerAsync(tenant);
        var appointment = await SeedAppointmentAsync(
            world.Factory, tenant.IdText, mainId, resource, service, customer, DateTimeOffset.UtcNow.AddDays(3), AppointmentStatus.Confirmed);

        var response = await tenant.Admin.PostAsync($"{tenant.Url}/locations/{mainId}/archive", null, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal((AppointmentStatus.Confirmed, null, 1, 1), await ReadAppointmentAsync(appointment));
    }
}
