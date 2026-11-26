using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Scheduling.Application;
using static Modules.Scheduling.IntegrationTests.SchedulingApiHarness;

namespace Modules.Scheduling.IntegrationTests;

/// <summary>
/// Franjas (plan 2026-11-21, Tarea 8): <c>availability.manage</c> dentro del alcance. Quien no tiene «ver todas las agendas» sólo abre
/// franjas sobre sus recursos; uno ajeno responde 403, nunca 404. Los ids de recursos y servicios no tienen llave foránea: los revisa el
/// caso de uso al guardar (plan, corrección 13).
/// </summary>
public sealed class SchedulingBlocksApiTests(SchedulingApiWorld world) : IClassFixture<SchedulingApiWorld>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Guid[] ResourceIdsOf(JsonElement block) =>
        [.. block.GetProperty("resourceIds").EnumerateArray().Select(id => id.GetGuid())];

    [Fact]
    public async Task AWeeklyBlockWithSeveralResourcesAndServicesComesBackWholeAndInOrder()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var mainId = await MainLocationIdAsync(tenant);
        var first = await CreateResourceAsync(tenant, "Dra. Torres");
        var second = await CreateResourceAsync(tenant, "Dr. Rojas");
        var cleaning = await CreateServiceAsync(tenant, "Limpieza");
        var checkup = await CreateServiceAsync(tenant, "Revisión");

        // El orden de los recursos es el de la asignación «primero libre»: se pide al revés del orden alfabético a propósito.
        var created = await CreatedAsync(
            tenant.Admin, $"{tenant.Url}/blocks", WeeklyBlockBody([second, first], [cleaning, checkup], "Mañanas", capacity: 1));

        Assert.Equal("Mañanas", created.GetProperty("name").GetString());
        Assert.Equal("Weekly", created.GetProperty("kind").GetString());
        Assert.Equal(mainId, created.GetProperty("locationId").GetGuid());
        Assert.Equal([second, first], ResourceIdsOf(created));
        Assert.Equal(
            [cleaning, checkup], created.GetProperty("serviceIds").EnumerateArray().Select(id => id.GetGuid()).ToArray());
        var monday = Assert.Single(created.GetProperty("weekly").EnumerateArray());
        Assert.Equal(1, monday.GetProperty("day").GetInt32());
        var range = Assert.Single(monday.GetProperty("ranges").EnumerateArray());
        Assert.Equal(480, range.GetProperty("from").GetInt32());
        Assert.Equal(720, range.GetProperty("to").GetInt32());
        Assert.Equal(1, created.GetProperty("capacityPerResource").GetInt32());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("sessionMode").ValueKind);
        Assert.Equal("FirstFree", created.GetProperty("assignmentRule").GetString());
        Assert.False(created.GetProperty("archived").GetBoolean());
        Assert.Equal(1, created.GetProperty("version").GetInt64());

        var listed = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/blocks");
        Assert.Equal([second, first], ResourceIdsOf(Assert.Single(listed.EnumerateArray())));
    }

    [Fact]
    public async Task ABlockWithMoreThanOneSeatPerResourceDefaultsToFirstBookingOpens()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Sala 1");
        var service = await CreateServiceAsync(tenant, "Yoga", 60);

        var created = await CreatedAsync(
            tenant.Admin, $"{tenant.Url}/blocks", WeeklyBlockBody([resource], [service], "Clases", capacity: 12));

        Assert.Equal(12, created.GetProperty("capacityPerResource").GetInt32());
        Assert.Equal("FirstBookingOpens", created.GetProperty("sessionMode").GetString());
    }

    [Fact]
    public async Task AOneOffBlockIsCreatedWithItsDate()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var service = await CreateServiceAsync(tenant);
        var day = FutureMonday();

        var created = await CreatedAsync(tenant.Admin, $"{tenant.Url}/blocks", OneOffBlockBody(day, [resource], [service]));

        Assert.Equal("OneOff", created.GetProperty("kind").GetString());
        Assert.Equal(day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), created.GetProperty("oneOff").GetProperty("date").GetString());
        Assert.Equal(0, created.GetProperty("weekly").GetArrayLength());
    }

    [Fact]
    public async Task ABlockWithAnEmptyResourceListIsRejected()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var service = await CreateServiceAsync(tenant);

        var response = await tenant.Admin.PostAsJsonAsync($"{tenant.Url}/blocks", WeeklyBlockBody([], [service]), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await JsonOfAsync(response);
        Assert.Equal("validation.failed", CodeOf(problem));
        Assert.True(HasError(problem, "resourceIds"));
    }

    [Fact]
    public async Task ABlockWithAnEmptyServiceListIsRejected()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");

        var response = await tenant.Admin.PostAsJsonAsync($"{tenant.Url}/blocks", WeeklyBlockBody([resource], []), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True(HasError(await JsonOfAsync(response), "serviceIds"));
    }

    private static JsonElement BlockById(JsonElement blocks, Guid id) =>
        blocks.EnumerateArray().Single(block => block.GetProperty("id").GetGuid() == id);

    [Fact]
    public async Task ArchivingAResourceTakesItOutOfEveryBlockOfTheTenantInTheSameRequest()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var torres = await CreateResourceAsync(tenant, "Dra. Torres");
        var rojas = await CreateResourceAsync(tenant, "Dr. Rojas");
        var service = await CreateServiceAsync(tenant);
        var shared = (await CreatedAsync(tenant.Admin, $"{tenant.Url}/blocks", WeeklyBlockBody([torres, rojas], [service])))
            .GetProperty("id").GetGuid();
        var alone = (await CreatedAsync(tenant.Admin, $"{tenant.Url}/blocks", WeeklyBlockBody([torres], [service])))
            .GetProperty("id").GetGuid();
        var unrelated = (await CreatedAsync(tenant.Admin, $"{tenant.Url}/blocks", WeeklyBlockBody([rojas], [service])))
            .GetProperty("id").GetGuid();

        var archived = await tenant.Admin.PostAsync($"{tenant.Url}/resources/{torres}/archive", null, Ct);

        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        var blocks = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/blocks");
        Assert.Equal([rojas], ResourceIdsOf(BlockById(blocks, shared)));
        Assert.False(BlockById(blocks, shared).GetProperty("archived").GetBoolean());
        Assert.Empty(ResourceIdsOf(BlockById(blocks, alone)));
        Assert.True(BlockById(blocks, alone).GetProperty("archived").GetBoolean());
        Assert.Equal([rojas], ResourceIdsOf(BlockById(blocks, unrelated)));
        Assert.False(BlockById(blocks, unrelated).GetProperty("archived").GetBoolean());
    }

    [Fact]
    public async Task ArchivingAServiceTakesItOutOfEveryBlockAndArchivesTheOnesLeftWithoutServices()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var cleaning = await CreateServiceAsync(tenant, "Limpieza");
        var checkup = await CreateServiceAsync(tenant, "Revisión");
        var both = (await CreatedAsync(tenant.Admin, $"{tenant.Url}/blocks", WeeklyBlockBody([resource], [cleaning, checkup])))
            .GetProperty("id").GetGuid();
        var alone = (await CreatedAsync(tenant.Admin, $"{tenant.Url}/blocks", WeeklyBlockBody([resource], [cleaning])))
            .GetProperty("id").GetGuid();

        var archived = await tenant.Admin.PostAsync($"{tenant.Url}/services/{cleaning}/archive", null, Ct);

        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        var blocks = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/blocks");
        Assert.Equal(
            [checkup],
            BlockById(blocks, both).GetProperty("serviceIds").EnumerateArray().Select(id => id.GetGuid()));
        Assert.False(BlockById(blocks, both).GetProperty("archived").GetBoolean());
        Assert.Empty(BlockById(blocks, alone).GetProperty("serviceIds").EnumerateArray());
        Assert.True(BlockById(blocks, alone).GetProperty("archived").GetBoolean());
    }

    [Fact]
    public async Task AResourceWithFutureAppointmentsStaysInItsBlocksBecauseArchivingIsRejected()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var service = await CreateServiceAsync(tenant);
        var block = (await CreatedAsync(tenant.Admin, $"{tenant.Url}/blocks", WeeklyBlockBody([resource], [service])))
            .GetProperty("id").GetGuid();
        await SeedFutureAppointmentAsync(world.Factory, tenant.IdText, await MainLocationIdAsync(tenant), resource, service);

        var rejectedResource = await tenant.Admin.PostAsync($"{tenant.Url}/resources/{resource}/archive", null, Ct);
        var rejectedService = await tenant.Admin.PostAsync($"{tenant.Url}/services/{service}/archive", null, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejectedResource.StatusCode);
        Assert.Equal("scheduling.resource.has_future_appointments", CodeOf(await JsonOfAsync(rejectedResource)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejectedService.StatusCode);
        Assert.Equal("scheduling.service.in_use", CodeOf(await JsonOfAsync(rejectedService)));
        var blocks = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/blocks");
        Assert.Equal([resource], ResourceIdsOf(BlockById(blocks, block)));
        Assert.False(BlockById(blocks, block).GetProperty("archived").GetBoolean());
    }

    [Fact]
    public async Task ABlockCannotUseAResourceOrServiceThatIsNotOfTheTenantOrIsArchived()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        using var other = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var service = await CreateServiceAsync(tenant);
        var foreignResource = await CreateResourceAsync(other, "Ajena");
        var foreignService = await CreateServiceAsync(other, "Ajeno");
        var archived = await CreateResourceAsync(tenant, "Archivada");
        (await tenant.Admin.PostAsync($"{tenant.Url}/resources/{archived}/archive", null, Ct)).EnsureSuccessStatusCode();

        var withForeignResource = await tenant.Admin.PostAsJsonAsync(
            $"{tenant.Url}/blocks", WeeklyBlockBody([resource, foreignResource], [service]), Ct);
        var withForeignService = await tenant.Admin.PostAsJsonAsync(
            $"{tenant.Url}/blocks", WeeklyBlockBody([resource], [service, foreignService]), Ct);
        var withArchivedResource = await tenant.Admin.PostAsJsonAsync(
            $"{tenant.Url}/blocks", WeeklyBlockBody([archived], [service]), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, withForeignResource.StatusCode);
        Assert.True(HasError(await JsonOfAsync(withForeignResource), "resourceIds"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, withForeignService.StatusCode);
        Assert.True(HasError(await JsonOfAsync(withForeignService), "serviceIds"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, withArchivedResource.StatusCode);
        Assert.True(HasError(await JsonOfAsync(withArchivedResource), "resourceIds"));
    }

    [Fact]
    public async Task TheResourcesOfOneBlockMustShareALocation()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var branch = await CreatedAsync(tenant.Admin, $"{tenant.Url}/locations", new { name = "Sede norte" });
        var inMain = await CreateResourceAsync(tenant, "Dra. Torres");
        var inBranch = await CreateResourceAsync(tenant, "Dr. Rojas", locationId: branch.GetProperty("id").GetGuid());
        var service = await CreateServiceAsync(tenant);

        var response = await tenant.Admin.PostAsJsonAsync(
            $"{tenant.Url}/blocks", WeeklyBlockBody([inMain, inBranch], [service]), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True(HasError(await JsonOfAsync(response), "resourceIds"));
    }

    [Fact]
    public async Task AnImpossibleScheduleIsRejectedWithTheFieldMarked()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var service = await CreateServiceAsync(tenant);
        var backwards = new
        {
            name = "Rota",
            kind = "Weekly",
            weekly = new[] { new { day = 1, ranges = new[] { new { @from = 720, to = 480 } } } },
            resourceIds = new[] { resource },
            serviceIds = new[] { service },
        };

        var response = await tenant.Admin.PostAsJsonAsync($"{tenant.Url}/blocks", backwards, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.True(HasError(await JsonOfAsync(response), "weekly"));
    }

    [Fact]
    public async Task ABlockIsEditedWithItsVersionAndTheNewResourceOrderComesBack()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var first = await CreateResourceAsync(tenant, "Dra. Torres");
        var second = await CreateResourceAsync(tenant, "Dr. Rojas");
        var service = await CreateServiceAsync(tenant);
        var created = await CreatedAsync(tenant.Admin, $"{tenant.Url}/blocks", WeeklyBlockBody([first, second], [service]));
        var url = $"{tenant.Url}/blocks/{created.GetProperty("id").GetGuid()}";

        var updated = await PutAsync(tenant.Admin, url, WeeklyBlockBody([second, first], [service], "Tardes"), "1");
        var stale = await PutAsync(tenant.Admin, url, WeeklyBlockBody([first], [service], "Otra"), "1");
        var missing = await PutAsync(tenant.Admin, url, WeeklyBlockBody([first], [service], "Otra"), null);

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var body = await JsonOfAsync(updated);
        Assert.Equal("Tardes", body.GetProperty("name").GetString());
        Assert.Equal(2, body.GetProperty("version").GetInt64());
        Assert.Equal([second, first], ResourceIdsOf(body));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
    }

    [Fact]
    public async Task AnArchivedBlockStopsCountingForTheSetup()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var service = await CreateServiceAsync(tenant);
        var created = await CreatedAsync(tenant.Admin, $"{tenant.Url}/blocks", WeeklyBlockBody([resource], [service]));
        Assert.True((await GetJsonAsync(tenant.Admin, $"{tenant.Url}/setup")).GetProperty("hasBlock").GetBoolean());

        var archived = await tenant.Admin.PostAsync($"{tenant.Url}/blocks/{created.GetProperty("id").GetGuid()}/archive", null, Ct);

        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        Assert.True((await JsonOfAsync(archived)).GetProperty("archived").GetBoolean());
        Assert.False((await GetJsonAsync(tenant.Admin, $"{tenant.Url}/setup")).GetProperty("hasBlock").GetBoolean());
    }

    [Fact]
    public async Task AProviderOnlyCreatesBlocksOverItsOwnResource()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var (own, provider) = await CreateProviderAsync(tenant, "torres@example.com", "Torres");
        using var _ = provider;
        var another = await CreateResourceAsync(tenant, "Dr. Rojas");
        var service = await CreateServiceAsync(tenant);

        var mine = await provider.PostAsJsonAsync($"{tenant.Url}/blocks", WeeklyBlockBody([own], [service]), Ct);
        var theirs = await provider.PostAsJsonAsync($"{tenant.Url}/blocks", WeeklyBlockBody([another], [service]), Ct);
        var mixed = await provider.PostAsJsonAsync($"{tenant.Url}/blocks", WeeklyBlockBody([own, another], [service]), Ct);

        Assert.Equal(HttpStatusCode.Created, mine.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, theirs.StatusCode);
        Assert.Equal("authorization.denied", CodeOf(await JsonOfAsync(theirs)));
        Assert.Equal(HttpStatusCode.Forbidden, mixed.StatusCode);
    }

    [Fact]
    public async Task AProviderListsBlocksThatOpenItsAgendaButEditsOnlyThoseEntirelyItsOwn()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var (own, provider) = await CreateProviderAsync(tenant, "torres@example.com", "Torres");
        using var _ = provider;
        var another = await CreateResourceAsync(tenant, "Dr. Rojas");
        var service = await CreateServiceAsync(tenant);
        var mine = await CreatedAsync(tenant.Admin, $"{tenant.Url}/blocks", WeeklyBlockBody([own], [service], "Mía"));
        var shared = await CreatedAsync(tenant.Admin, $"{tenant.Url}/blocks", WeeklyBlockBody([own, another], [service], "Compartida"));
        var foreign = await CreatedAsync(tenant.Admin, $"{tenant.Url}/blocks", WeeklyBlockBody([another], [service], "Ajena"));

        var visible = await GetJsonAsync(provider, $"{tenant.Url}/blocks");
        var editMine = await PutAsync(
            provider, $"{tenant.Url}/blocks/{mine.GetProperty("id").GetGuid()}", WeeklyBlockBody([own], [service], "Mía 2"), "1");
        var editShared = await PutAsync(
            provider, $"{tenant.Url}/blocks/{shared.GetProperty("id").GetGuid()}", WeeklyBlockBody([own], [service], "Recortada"), "1");
        var editForeign = await PutAsync(
            provider, $"{tenant.Url}/blocks/{foreign.GetProperty("id").GetGuid()}", WeeklyBlockBody([own], [service], "Robada"), "1");
        var archiveForeign = await provider.PostAsync($"{tenant.Url}/blocks/{foreign.GetProperty("id").GetGuid()}/archive", null, Ct);

        Assert.Equal(
            ["Compartida", "Mía"],
            visible.EnumerateArray().Select(block => block.GetProperty("name").GetString()!).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(HttpStatusCode.OK, editMine.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, editShared.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, editForeign.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, archiveForeign.StatusCode);
    }

    [Fact]
    public async Task WithoutAvailabilityManageNoBlockRouteIsAvailable()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var service = await CreateServiceAsync(tenant);
        var created = await CreatedAsync(tenant.Admin, $"{tenant.Url}/blocks", WeeklyBlockBody([resource], [service]));
        var id = created.GetProperty("id").GetGuid();
        using var reader = tenant.Client(null, SchedulingPermissions.AppointmentRead, SchedulingPermissions.AgendaReadAll);

        var list = await reader.GetAsync($"{tenant.Url}/blocks", Ct);
        var create = await reader.PostAsJsonAsync($"{tenant.Url}/blocks", WeeklyBlockBody([resource], [service]), Ct);
        var update = await PutAsync(reader, $"{tenant.Url}/blocks/{id}", WeeklyBlockBody([resource], [service]), "1");
        var archive = await reader.PostAsync($"{tenant.Url}/blocks/{id}/archive", null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, archive.StatusCode);
    }

    [Fact]
    public async Task ABlockRouteOfAnotherTenantIsForbiddenAndItsIdsAreNotFoundHere()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        using var other = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(other, "Ajena");
        var service = await CreateServiceAsync(other, "Ajeno");
        var foreign = await CreatedAsync(other.Admin, $"{other.Url}/blocks", WeeklyBlockBody([resource], [service]));

        var crossTenant = await other.Admin.GetAsync($"{tenant.Url}/blocks", Ct);
        var unknownHere = await PutAsync(
            tenant.Admin, $"{tenant.Url}/blocks/{foreign.GetProperty("id").GetGuid()}", WeeklyBlockBody([resource], [service]), "1");

        Assert.Equal(HttpStatusCode.Forbidden, crossTenant.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownHere.StatusCode);
        Assert.Equal("scheduling.block.not_found", CodeOf(await JsonOfAsync(unknownHere)));
    }
}
