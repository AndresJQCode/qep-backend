using System.Net;
using System.Net.Http.Json;
using Modules.Scheduling.Application;
using static Modules.Scheduling.IntegrationTests.SchedulingApiHarness;

namespace Modules.Scheduling.IntegrationTests;

/// <summary>
/// Auditoría de la configuración (plan 2026-11-21, adelantada de la Tarea 9 a la 7 por decisión del owner): cada alta, cambio, archivo o
/// borrado deja una fila en <c>audit.entries</c> con la acción y la entidad correctas, en la misma transacción que el cambio.
/// </summary>
public sealed class SchedulingAuditApiTests(SchedulingApiWorld world) : IClassFixture<SchedulingApiWorld>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<int> CountAsync(SchedulingTenant tenant, string action, string resourceType, Guid id) =>
        CountAuditEntriesAsync(world.Factory, tenant.IdText, action, resourceType, id);

    [Fact]
    public async Task ALocationCreatedEditedAndArchivedLeavesItsTrail()
    {
        using var tenant = await NewTenantAsync(world.Factory);

        var created = await CreatedAsync(tenant.Admin, $"{tenant.Url}/locations", new { name = "Sede Norte" });
        var id = created.GetProperty("id").GetGuid();
        var url = $"{tenant.Url}/locations/{id}";
        var updated = await PutAsync(tenant.Admin, url, new { name = "Sede Norte 2" }, "1");
        var archived = await tenant.Admin.PostAsync($"{url}/archive", null, Ct);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        Assert.Equal(1, await CountAsync(tenant, SchedulingAuditActions.LocationCreated, SchedulingAuditActions.Resources.Location, id));
        Assert.Equal(1, await CountAsync(tenant, SchedulingAuditActions.LocationUpdated, SchedulingAuditActions.Resources.Location, id));
        Assert.Equal(1, await CountAsync(tenant, SchedulingAuditActions.LocationArchived, SchedulingAuditActions.Resources.Location, id));
    }

    [Fact]
    public async Task AResourceCreatedEditedAndArchivedLeavesItsTrail()
    {
        using var tenant = await NewTenantAsync(world.Factory);

        var id = await CreateResourceAsync(tenant, "Dra. Torres");
        var url = $"{tenant.Url}/resources/{id}";
        var updated = await PutAsync(tenant.Admin, url, new { displayName = "Dra. Laura Torres" }, "1");
        var archived = await tenant.Admin.PostAsync($"{url}/archive", null, Ct);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        Assert.Equal(1, await CountAsync(tenant, SchedulingAuditActions.ResourceCreated, SchedulingAuditActions.Resources.Resource, id));
        Assert.Equal(1, await CountAsync(tenant, SchedulingAuditActions.ResourceUpdated, SchedulingAuditActions.Resources.Resource, id));
        Assert.Equal(1, await CountAsync(tenant, SchedulingAuditActions.ResourceArchived, SchedulingAuditActions.Resources.Resource, id));
    }

    [Fact]
    public async Task AServiceCreatedEditedAndArchivedLeavesItsTrail()
    {
        using var tenant = await NewTenantAsync(world.Factory);

        var id = await CreateServiceAsync(tenant);
        var url = $"{tenant.Url}/services/{id}";
        var updated = await PutAsync(
            tenant.Admin, url, new { name = "Limpieza profunda", durationMinutes = 45, bufferMinutes = 5, bookableByChannel = false }, "1");
        var archived = await tenant.Admin.PostAsync($"{url}/archive", null, Ct);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        Assert.Equal(1, await CountAsync(tenant, SchedulingAuditActions.ServiceCreated, SchedulingAuditActions.Resources.Service, id));
        Assert.Equal(1, await CountAsync(tenant, SchedulingAuditActions.ServiceUpdated, SchedulingAuditActions.Resources.Service, id));
        Assert.Equal(1, await CountAsync(tenant, SchedulingAuditActions.ServiceArchived, SchedulingAuditActions.Resources.Service, id));
    }

    [Fact]
    public async Task EditingTheRulesLeavesItsTrailOnTheTenantRow()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var current = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");

        var response = await PutAsync(
            tenant.Admin,
            $"{tenant.Url}/settings",
            new
            {
                confirmationMode = "Manual",
                minNoticeMinutes = 60,
                horizonDays = 45,
                clientCancelCutoffHours = 12,
                reminderEnabled = false,
                reminderOffsetHours = 24,
            },
            current.GetProperty("version").GetRawText());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // La fila de reglas es única por tenant y no tiene id propio: su recurso auditado es el tenant.
        Assert.Equal(1, await CountAsync(tenant, SchedulingAuditActions.SettingsUpdated, SchedulingAuditActions.Resources.Settings, tenant.Id));
    }

    [Fact]
    public async Task ABlockCreatedEditedAndArchivedLeavesItsTrail()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var service = await CreateServiceAsync(tenant);

        var created = await CreatedAsync(tenant.Admin, $"{tenant.Url}/blocks", WeeklyBlockBody([resource], [service]));
        var id = created.GetProperty("id").GetGuid();
        var url = $"{tenant.Url}/blocks/{id}";
        var updated = await PutAsync(tenant.Admin, url, WeeklyBlockBody([resource], [service], "Mañanas 2"), "1");
        var archived = await tenant.Admin.PostAsync($"{url}/archive", null, Ct);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        Assert.Equal(1, await CountAsync(tenant, SchedulingAuditActions.BlockCreated, SchedulingAuditActions.Resources.Block, id));
        Assert.Equal(1, await CountAsync(tenant, SchedulingAuditActions.BlockUpdated, SchedulingAuditActions.Resources.Block, id));
        Assert.Equal(1, await CountAsync(tenant, SchedulingAuditActions.BlockArchived, SchedulingAuditActions.Resources.Block, id));
    }

    [Fact]
    public async Task AClosureCreatedAndDeletedLeavesItsTrail()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(30).AddHours(13), TimeSpan.Zero);

        var created = await CreatedAsync(
            tenant.Admin, $"{tenant.Url}/closures", new { resourceId = resource, startsAt = start, endsAt = start.AddHours(4), reason = "Vacaciones" });
        var id = created.GetProperty("id").GetGuid();
        var deleted = await tenant.Admin.DeleteAsync($"{tenant.Url}/closures/{id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(1, await CountAsync(tenant, SchedulingAuditActions.ClosureCreated, SchedulingAuditActions.Resources.Closure, id));
        Assert.Equal(1, await CountAsync(tenant, SchedulingAuditActions.ClosureDeleted, SchedulingAuditActions.Resources.Closure, id));
    }

    [Fact]
    public async Task APublishedSessionPublishedAndRemovedLeavesItsTrail()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Sala 1");
        var service = await CreateServiceAsync(tenant, "Yoga", 60);
        var day = FutureMonday();
        var block = await CreatedAsync(
            tenant.Admin, $"{tenant.Url}/blocks", OneOffBlockBody(day, [resource], [service], "Clases", 5, "PublishedSessions"));

        var created = await CreatedAsync(
            tenant.Admin,
            $"{tenant.Url}/sessions",
            new { blockId = block.GetProperty("id").GetGuid(), resourceId = resource, serviceId = service, startsAt = NineOClock(day) });
        var id = created.GetProperty("id").GetGuid();
        var removed = await tenant.Admin.DeleteAsync($"{tenant.Url}/sessions/{id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(1, await CountAsync(tenant, SchedulingAuditActions.SessionPublished, SchedulingAuditActions.Resources.Session, id));
        Assert.Equal(1, await CountAsync(tenant, SchedulingAuditActions.SessionRemoved, SchedulingAuditActions.Resources.Session, id));
    }

    [Fact]
    public async Task ADeactivatedAndReactivatedSessionLeavesItsTrail()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Sala 1");
        var service = await CreateServiceAsync(tenant, "Yoga", 60);
        var day = FutureMonday();
        var block = await CreatedAsync(
            tenant.Admin, $"{tenant.Url}/blocks", OneOffBlockBody(day, [resource], [service], "Clases", 5, "PublishedSessions"));
        var body = new { blockId = block.GetProperty("id").GetGuid(), resourceId = resource, serviceId = service, startsAt = NineOClock(day) };
        var id = (await CreatedAsync(tenant.Admin, $"{tenant.Url}/sessions", body)).GetProperty("id").GetGuid();
        await SeedCancelledAppointmentOnSessionAsync(world.Factory, tenant.IdText, await MainLocationIdAsync(tenant), id);

        (await tenant.Admin.DeleteAsync($"{tenant.Url}/sessions/{id}", Ct)).EnsureSuccessStatusCode();
        (await tenant.Admin.PostAsJsonAsync($"{tenant.Url}/sessions", body, Ct)).EnsureSuccessStatusCode();

        Assert.Equal(1, await CountAsync(tenant, SchedulingAuditActions.SessionDeactivated, SchedulingAuditActions.Resources.Session, id));
        Assert.Equal(1, await CountAsync(tenant, SchedulingAuditActions.SessionReactivated, SchedulingAuditActions.Resources.Session, id));
        Assert.Equal(0, await CountAsync(tenant, SchedulingAuditActions.SessionRemoved, SchedulingAuditActions.Resources.Session, id));
        // The block and the service did not change, yet the entry is not empty: the row came back to life.
        var fields = Assert.Single(await ReadAuditFieldsAsync(
            world.Factory, tenant.IdText, SchedulingAuditActions.SessionReactivated, SchedulingAuditActions.Resources.Session, id));
        Assert.Contains("isActive", fields, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArchivingAResourceOrServiceLeavesATrailOnEveryBlockItChanges()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var first = await CreateResourceAsync(tenant, "Dra. Torres");
        var second = await CreateResourceAsync(tenant, "Dr. Rojas");
        var cleaning = await CreateServiceAsync(tenant, "Limpieza");
        var checkup = await CreateServiceAsync(tenant, "Revisión");
        var shared = (await CreatedAsync(
            tenant.Admin, $"{tenant.Url}/blocks", WeeklyBlockBody([first, second], [cleaning, checkup]))).GetProperty("id").GetGuid();
        var onlyFirst = (await CreatedAsync(
            tenant.Admin, $"{tenant.Url}/blocks", WeeklyBlockBody([first], [cleaning]))).GetProperty("id").GetGuid();

        (await tenant.Admin.PostAsync($"{tenant.Url}/resources/{first}/archive", null, Ct)).EnsureSuccessStatusCode();
        (await tenant.Admin.PostAsync($"{tenant.Url}/services/{cleaning}/archive", null, Ct)).EnsureSuccessStatusCode();

        // shared: loses the resource, then the service. onlyFirst: emptied by the resource, so it is archived and the service no longer reaches it.
        Assert.Equal(2, await CountAsync(tenant, SchedulingAuditActions.BlockUpdated, SchedulingAuditActions.Resources.Block, shared));
        Assert.Equal(0, await CountAsync(tenant, SchedulingAuditActions.BlockArchived, SchedulingAuditActions.Resources.Block, shared));
        Assert.Equal(1, await CountAsync(tenant, SchedulingAuditActions.BlockUpdated, SchedulingAuditActions.Resources.Block, onlyFirst));
        Assert.Equal(1, await CountAsync(tenant, SchedulingAuditActions.BlockArchived, SchedulingAuditActions.Resources.Block, onlyFirst));
    }

    // Archiving something already archived answers the same entity (same version) and audits nothing: no change happened.
    private async Task AssertSecondArchiveIsSilentAsync(
        SchedulingTenant tenant, string url, string action, string resourceType, Guid id)
    {
        var first = await tenant.Admin.PostAsync($"{url}/archive", null, Ct);
        var second = await tenant.Admin.PostAsync($"{url}/archive", null, Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstBody = await JsonOfAsync(first);
        var secondBody = await JsonOfAsync(second);
        Assert.True(secondBody.GetProperty("archived").GetBoolean());
        Assert.Equal(firstBody.GetProperty("version").GetRawText(), secondBody.GetProperty("version").GetRawText());
        Assert.Equal(1, await CountAsync(tenant, action, resourceType, id));
    }

    [Fact]
    public async Task ArchivingALocationTwiceAuditsItOnce()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var id = (await CreatedAsync(tenant.Admin, $"{tenant.Url}/locations", new { name = "Sede Norte" })).GetProperty("id").GetGuid();

        await AssertSecondArchiveIsSilentAsync(
            tenant, $"{tenant.Url}/locations/{id}", SchedulingAuditActions.LocationArchived, SchedulingAuditActions.Resources.Location, id);
    }

    [Fact]
    public async Task ArchivingAResourceTwiceAuditsItOnce()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var id = await CreateResourceAsync(tenant, "Dra. Torres");

        await AssertSecondArchiveIsSilentAsync(
            tenant, $"{tenant.Url}/resources/{id}", SchedulingAuditActions.ResourceArchived, SchedulingAuditActions.Resources.Resource, id);
    }

    [Fact]
    public async Task ArchivingAServiceTwiceAuditsItOnce()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var id = await CreateServiceAsync(tenant);

        await AssertSecondArchiveIsSilentAsync(
            tenant, $"{tenant.Url}/services/{id}", SchedulingAuditActions.ServiceArchived, SchedulingAuditActions.Resources.Service, id);
    }

    [Fact]
    public async Task ArchivingABlockTwiceAuditsItOnce()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var service = await CreateServiceAsync(tenant);
        var id = (await CreatedAsync(tenant.Admin, $"{tenant.Url}/blocks", WeeklyBlockBody([resource], [service]))).GetProperty("id").GetGuid();

        await AssertSecondArchiveIsSilentAsync(
            tenant, $"{tenant.Url}/blocks/{id}", SchedulingAuditActions.BlockArchived, SchedulingAuditActions.Resources.Block, id);
    }

    [Fact]
    public async Task ARejectedChangeLeavesNoTrail()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var locations = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/locations");
        var mainId = locations.EnumerateArray().Single(location => location.GetProperty("isMain").GetBoolean()).GetProperty("id").GetGuid();

        var response = await tenant.Admin.PostAsync($"{tenant.Url}/locations/{mainId}/archive", null, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, await CountAsync(tenant, SchedulingAuditActions.LocationArchived, SchedulingAuditActions.Resources.Location, mainId));
    }
}
