using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Scheduling.Application;
using static Modules.Scheduling.IntegrationTests.SchedulingApiHarness;

namespace Modules.Scheduling.IntegrationTests;

/// <summary>
/// Sesiones publicadas (plan 2026-11-21, Tarea 8): sólo una franja con cupo mayor que 1 y modo <c>PublishedSessions</c> admite que el tenant
/// publique sus sesiones (<c>scheduling.session.not_published_mode</c> si no). La sesión nace con el cupo de la franja y dentro de su ventana.
/// </summary>
public sealed class SchedulingSessionsApiTests(SchedulingApiWorld world) : IClassFixture<SchedulingApiWorld>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Publish(Guid blockId, Guid resourceId, Guid serviceId, DateTimeOffset startsAt) =>
        new { blockId, resourceId, serviceId, startsAt };

    /// <summary>Una franja puntual de 09:00 a 12:00 con cupo 5 y sesiones publicadas, para un recurso y un servicio de 60 minutos.</summary>
    private async Task<(SchedulingTenant Tenant, Guid Resource, Guid Service, Guid Block, DateOnly Day)> PublishedBlockAsync(
        string? sessionMode = "PublishedSessions", int capacity = 5)
    {
        var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Sala 1");
        var service = await CreateServiceAsync(tenant, "Yoga", 60);
        var day = FutureMonday();
        var block = await CreatedAsync(
            tenant.Admin, $"{tenant.Url}/blocks", OneOffBlockBody(day, [resource], [service], "Clases", capacity, sessionMode));
        return (tenant, resource, service, block.GetProperty("id").GetGuid(), day);
    }

    [Fact]
    public async Task ASessionIsPublishedWithTheCapacityOfItsBlock()
    {
        var (tenant, resource, service, block, day) = await PublishedBlockAsync();
        using var _ = tenant;
        var start = NineOClock(day);

        var response = await tenant.Admin.PostAsJsonAsync($"{tenant.Url}/sessions", Publish(block, resource, service, start), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await JsonOfAsync(response);
        Assert.Equal(resource, created.GetProperty("resourceId").GetGuid());
        Assert.Equal(service, created.GetProperty("serviceId").GetGuid());
        Assert.Equal(start, created.GetProperty("startsAt").GetDateTimeOffset());
        Assert.Equal(start.AddMinutes(60), created.GetProperty("endsAt").GetDateTimeOffset());
        Assert.Equal(5, created.GetProperty("capacity").GetInt32());
        Assert.Equal(0, created.GetProperty("bookedCount").GetInt32());
        Assert.True(created.GetProperty("isPublished").GetBoolean());
    }

    [Theory]
    [InlineData("FirstBookingOpens", 5)]
    [InlineData(null, 1)]
    public async Task ASessionCanOnlyBePublishedOnABlockInPublishedSessionsMode(string? sessionMode, int capacity)
    {
        var (tenant, resource, service, block, day) = await PublishedBlockAsync(sessionMode, capacity);
        using var _ = tenant;

        var response = await tenant.Admin.PostAsJsonAsync(
            $"{tenant.Url}/sessions", Publish(block, resource, service, NineOClock(day)), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("scheduling.session.not_published_mode", CodeOf(await JsonOfAsync(response)));
    }

    [Fact]
    public async Task ASessionOutsideTheWindowOfItsBlockIsRejected()
    {
        var (tenant, resource, service, block, day) = await PublishedBlockAsync();
        using var _ = tenant;

        // La franja abre de 09:00 a 12:00 locales: las 08:00 abren antes, las 11:30 terminan después (la sesión dura 60 minutos), y el
        // día siguiente no tiene franja.
        var early = await tenant.Admin.PostAsJsonAsync(
            $"{tenant.Url}/sessions", Publish(block, resource, service, NineOClock(day).AddHours(-1)), Ct);
        var overrunning = await tenant.Admin.PostAsJsonAsync(
            $"{tenant.Url}/sessions", Publish(block, resource, service, NineOClock(day).AddMinutes(150)), Ct);
        var otherDay = await tenant.Admin.PostAsJsonAsync(
            $"{tenant.Url}/sessions", Publish(block, resource, service, NineOClock(day.AddDays(1))), Ct);

        foreach (var response in new[] { early, overrunning, otherDay })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.True(HasError(await JsonOfAsync(response), "startsAt"));
        }
    }

    [Fact]
    public async Task ASessionNeedsAResourceAndServiceOfItsBlock()
    {
        var (tenant, resource, service, block, day) = await PublishedBlockAsync();
        using var _ = tenant;
        var outsider = await CreateResourceAsync(tenant, "Sala 2");
        var otherService = await CreateServiceAsync(tenant, "Pilates", 60);

        var wrongResource = await tenant.Admin.PostAsJsonAsync(
            $"{tenant.Url}/sessions", Publish(block, outsider, service, NineOClock(day)), Ct);
        var wrongService = await tenant.Admin.PostAsJsonAsync(
            $"{tenant.Url}/sessions", Publish(block, resource, otherService, NineOClock(day)), Ct);
        var unknownBlock = await tenant.Admin.PostAsJsonAsync(
            $"{tenant.Url}/sessions", Publish(Guid.CreateVersion7(), resource, service, NineOClock(day)), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongResource.StatusCode);
        Assert.True(HasError(await JsonOfAsync(wrongResource), "resourceId"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongService.StatusCode);
        Assert.True(HasError(await JsonOfAsync(wrongService), "serviceId"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownBlock.StatusCode);
        Assert.True(HasError(await JsonOfAsync(unknownBlock), "blockId"));
    }

    [Fact]
    public async Task TheSameSessionOrOneThatOverlapsItCannotBePublishedTwice()
    {
        var (tenant, resource, service, block, day) = await PublishedBlockAsync();
        using var _ = tenant;
        var start = NineOClock(day);
        (await tenant.Admin.PostAsJsonAsync($"{tenant.Url}/sessions", Publish(block, resource, service, start), Ct)).EnsureSuccessStatusCode();

        var same = await tenant.Admin.PostAsJsonAsync($"{tenant.Url}/sessions", Publish(block, resource, service, start), Ct);
        // 09:30 cae dentro de [09:00, 10:00): la restricción de exclusión del recurso lo rechaza aunque sea otro inicio.
        var overlapping = await tenant.Admin.PostAsJsonAsync(
            $"{tenant.Url}/sessions", Publish(block, resource, service, start.AddMinutes(30)), Ct);
        var adjacent = await tenant.Admin.PostAsJsonAsync(
            $"{tenant.Url}/sessions", Publish(block, resource, service, start.AddMinutes(60)), Ct);

        Assert.Equal(HttpStatusCode.Conflict, same.StatusCode);
        Assert.Equal("scheduling.appointment.slot_taken", CodeOf(await JsonOfAsync(same)));
        Assert.Equal(HttpStatusCode.Conflict, overlapping.StatusCode);
        Assert.Equal("scheduling.appointment.slot_taken", CodeOf(await JsonOfAsync(overlapping)));
        Assert.Equal(HttpStatusCode.Created, adjacent.StatusCode);
    }

    [Fact]
    public async Task AProviderPublishesOnlyOnItsOwnResource()
    {
        var tenant = await NewTenantAsync(world.Factory);
        using var _ = tenant;
        var (own, provider) = await CreateProviderAsync(tenant, "torres@example.com", "Torres");
        using var __ = provider;
        var another = await CreateResourceAsync(tenant, "Sala 2");
        var service = await CreateServiceAsync(tenant, "Yoga", 60);
        var day = FutureMonday();
        var block = (await CreatedAsync(
            tenant.Admin, $"{tenant.Url}/blocks", OneOffBlockBody(day, [own, another], [service], "Clases", 5, "PublishedSessions")))
            .GetProperty("id").GetGuid();

        var mine = await provider.PostAsJsonAsync($"{tenant.Url}/sessions", Publish(block, own, service, NineOClock(day)), Ct);
        var theirs = await provider.PostAsJsonAsync($"{tenant.Url}/sessions", Publish(block, another, service, NineOClock(day)), Ct);

        Assert.Equal(HttpStatusCode.Created, mine.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, theirs.StatusCode);
        Assert.Equal("authorization.denied", CodeOf(await JsonOfAsync(theirs)));
    }

    [Fact]
    public async Task APublishedSessionIsRemovedAndCanBePublishedAgain()
    {
        var (tenant, resource, service, block, day) = await PublishedBlockAsync();
        using var _ = tenant;
        var start = NineOClock(day);
        var created = await CreatedAsync(tenant.Admin, $"{tenant.Url}/sessions", Publish(block, resource, service, start));
        var url = $"{tenant.Url}/sessions/{created.GetProperty("id").GetGuid()}";

        var removed = await tenant.Admin.DeleteAsync(url, Ct);
        var again = await tenant.Admin.DeleteAsync(url, Ct);
        var republished = await tenant.Admin.PostAsJsonAsync($"{tenant.Url}/sessions", Publish(block, resource, service, start), Ct);

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal("scheduling.session.not_found", CodeOf(await JsonOfAsync(again)));
        Assert.Equal(HttpStatusCode.Created, republished.StatusCode);
    }

    [Fact]
    public async Task ASessionWithAppointmentsCannotBeRemoved()
    {
        var (tenant, resource, service, block, day) = await PublishedBlockAsync();
        using var _ = tenant;
        var created = await CreatedAsync(tenant.Admin, $"{tenant.Url}/sessions", Publish(block, resource, service, NineOClock(day)));
        var id = created.GetProperty("id").GetGuid();
        await SeedAppointmentOnSessionAsync(world.Factory, tenant.IdText, await MainLocationIdAsync(tenant), id);

        var response = await tenant.Admin.DeleteAsync($"{tenant.Url}/sessions/{id}", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("scheduling.session.has_appointments", CodeOf(await JsonOfAsync(response)));
    }

    [Fact]
    public async Task ASessionWithOnlyCancelledAppointmentsIsDeactivatedNotDeleted()
    {
        var (tenant, resource, service, block, day) = await PublishedBlockAsync();
        using var _ = tenant;
        var created = await CreatedAsync(tenant.Admin, $"{tenant.Url}/sessions", Publish(block, resource, service, NineOClock(day)));
        var id = created.GetProperty("id").GetGuid();
        await SeedCancelledAppointmentOnSessionAsync(world.Factory, tenant.IdText, await MainLocationIdAsync(tenant), id);

        var removed = await tenant.Admin.DeleteAsync($"{tenant.Url}/sessions/{id}", Ct);
        var again = await tenant.Admin.DeleteAsync($"{tenant.Url}/sessions/{id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        var row = await ReadSessionAsync(world.Factory, id);
        Assert.True(row.Exists);
        Assert.False(row.IsActive);
        Assert.True(row.IsPublished);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task ADeactivatedSessionDoesNotOccupyItsResourceAndPublishingAgainReactivatesTheSameRow()
    {
        var (tenant, resource, service, block, day) = await PublishedBlockAsync();
        using var _ = tenant;
        var start = NineOClock(day);
        var created = await CreatedAsync(tenant.Admin, $"{tenant.Url}/sessions", Publish(block, resource, service, start));
        var id = created.GetProperty("id").GetGuid();
        await SeedCancelledAppointmentOnSessionAsync(world.Factory, tenant.IdText, await MainLocationIdAsync(tenant), id);
        (await tenant.Admin.DeleteAsync($"{tenant.Url}/sessions/{id}", Ct)).EnsureSuccessStatusCode();

        // 09:30 falls inside [09:00, 10:00): it would be a conflict if the inactive row still occupied the resource.
        var overlapping = await tenant.Admin.PostAsJsonAsync(
            $"{tenant.Url}/sessions", Publish(block, resource, service, start.AddMinutes(30)), Ct);
        Assert.Equal(HttpStatusCode.Created, overlapping.StatusCode);
        var overlappingId = (await JsonOfAsync(overlapping)).GetProperty("id").GetGuid();
        (await tenant.Admin.DeleteAsync($"{tenant.Url}/sessions/{overlappingId}", Ct)).EnsureSuccessStatusCode();

        var republished = await tenant.Admin.PostAsJsonAsync($"{tenant.Url}/sessions", Publish(block, resource, service, start), Ct);
        var duplicate = await tenant.Admin.PostAsJsonAsync($"{tenant.Url}/sessions", Publish(block, resource, service, start), Ct);

        Assert.Equal(HttpStatusCode.Created, republished.StatusCode);
        var body = await JsonOfAsync(republished);
        Assert.Equal(id, body.GetProperty("id").GetGuid());
        Assert.Equal(5, body.GetProperty("capacity").GetInt32());
        Assert.True(body.GetProperty("isPublished").GetBoolean());
        var row = await ReadSessionAsync(world.Factory, id);
        Assert.True(row.IsActive);
        Assert.True(row.IsPublished);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("scheduling.appointment.slot_taken", CodeOf(await JsonOfAsync(duplicate)));
    }

    [Fact]
    public async Task ADeactivatedSessionIsReactivatedWithTheCapacityOfTheCurrentBlock()
    {
        var (tenant, resource, service, block, day) = await PublishedBlockAsync(capacity: 5);
        using var _ = tenant;
        var start = NineOClock(day);
        var created = await CreatedAsync(tenant.Admin, $"{tenant.Url}/sessions", Publish(block, resource, service, start));
        var id = created.GetProperty("id").GetGuid();
        await SeedCancelledAppointmentOnSessionAsync(world.Factory, tenant.IdText, await MainLocationIdAsync(tenant), id);
        (await tenant.Admin.DeleteAsync($"{tenant.Url}/sessions/{id}", Ct)).EnsureSuccessStatusCode();
        var current = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/blocks");
        var version = current.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == block).GetProperty("version").GetInt64();
        var update = await PutAsync(
            tenant.Admin, $"{tenant.Url}/blocks/{block}", OneOffBlockBody(day, [resource], [service], "Clases", 8, "PublishedSessions"), version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        update.EnsureSuccessStatusCode();

        var republished = await tenant.Admin.PostAsJsonAsync($"{tenant.Url}/sessions", Publish(block, resource, service, start), Ct);

        Assert.Equal(HttpStatusCode.Created, republished.StatusCode);
        Assert.Equal(8, (await JsonOfAsync(republished)).GetProperty("capacity").GetInt32());
    }

    private static string ListUrl(SchedulingTenant tenant, DateOnly from, DateOnly to, Guid? resourceId = null) =>
        $"{tenant.Url}/sessions?from={Iso(from)}&to={Iso(to)}" + (resourceId is { } id ? $"&resourceId={id}" : string.Empty);

    [Fact]
    public async Task PublishedSessionsAreListedByDayRangeWithTheirRealBookedCount()
    {
        var (tenant, resource, service, block, day) = await PublishedBlockAsync();
        using var _ = tenant;
        var first = NineOClock(day);
        var late = await CreatedAsync(tenant.Admin, $"{tenant.Url}/sessions", Publish(block, resource, service, first.AddMinutes(60)));
        var early = await CreatedAsync(tenant.Admin, $"{tenant.Url}/sessions", Publish(block, resource, service, first));
        await SeedAppointmentOnSessionAsync(world.Factory, tenant.IdText, await MainLocationIdAsync(tenant), early.GetProperty("id").GetGuid());

        var listed = await GetJsonAsync(tenant.Admin, ListUrl(tenant, day, day));
        var outside = await GetJsonAsync(tenant.Admin, ListUrl(tenant, day.AddDays(1), day.AddDays(2)));

        var rows = listed.EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(early.GetProperty("id").GetGuid(), rows[0].GetProperty("id").GetGuid());
        Assert.Equal(late.GetProperty("id").GetGuid(), rows[1].GetProperty("id").GetGuid());
        Assert.Equal(first, rows[0].GetProperty("startsAt").GetDateTimeOffset());
        Assert.Equal(5, rows[0].GetProperty("capacity").GetInt32());
        Assert.Equal(1, rows[0].GetProperty("bookedCount").GetInt32());
        Assert.Equal(0, rows[1].GetProperty("bookedCount").GetInt32());
        Assert.True(rows[0].GetProperty("isPublished").GetBoolean());
        Assert.Empty(outside.EnumerateArray());
    }

    [Fact]
    public async Task ARemovedSessionAndASessionOfAnArchivedResourceAreNotListed()
    {
        var (tenant, resource, service, block, day) = await PublishedBlockAsync();
        using var _ = tenant;
        var removed = await CreatedAsync(tenant.Admin, $"{tenant.Url}/sessions", Publish(block, resource, service, NineOClock(day)));
        var kept = await CreatedAsync(tenant.Admin, $"{tenant.Url}/sessions", Publish(block, resource, service, NineOClock(day).AddMinutes(60)));
        (await tenant.Admin.DeleteAsync($"{tenant.Url}/sessions/{removed.GetProperty("id").GetGuid()}", Ct)).EnsureSuccessStatusCode();

        var listed = await GetJsonAsync(tenant.Admin, ListUrl(tenant, day, day));

        Assert.Equal(kept.GetProperty("id").GetGuid(), Assert.Single(listed.EnumerateArray()).GetProperty("id").GetGuid());

        var archive = await tenant.Admin.PostAsync($"{tenant.Url}/resources/{resource}/archive", null, Ct);
        Assert.True(archive.IsSuccessStatusCode, await BodyAsync(archive));
        Assert.Empty((await GetJsonAsync(tenant.Admin, ListUrl(tenant, day, day))).EnumerateArray());
    }

    [Fact]
    public async Task TheListCanBeNarrowedToOneResource()
    {
        var tenant = await NewTenantAsync(world.Factory);
        using var _ = tenant;
        var one = await CreateResourceAsync(tenant, "Sala 1");
        var two = await CreateResourceAsync(tenant, "Sala 2");
        var service = await CreateServiceAsync(tenant, "Yoga", 60);
        var day = FutureMonday();
        var block = (await CreatedAsync(
            tenant.Admin, $"{tenant.Url}/blocks", OneOffBlockBody(day, [one, two], [service], "Clases", 5, "PublishedSessions")))
            .GetProperty("id").GetGuid();
        await CreatedAsync(tenant.Admin, $"{tenant.Url}/sessions", Publish(block, one, service, NineOClock(day)));
        await CreatedAsync(tenant.Admin, $"{tenant.Url}/sessions", Publish(block, two, service, NineOClock(day)));

        var all = await GetJsonAsync(tenant.Admin, ListUrl(tenant, day, day));
        var onlyTwo = await GetJsonAsync(tenant.Admin, ListUrl(tenant, day, day, two));

        Assert.Equal(2, all.GetArrayLength());
        Assert.Equal(two, Assert.Single(onlyTwo.EnumerateArray()).GetProperty("resourceId").GetGuid());
    }

    [Fact]
    public async Task AProviderListsOnlyTheSessionsOfItsOwnResource()
    {
        var tenant = await NewTenantAsync(world.Factory);
        using var _ = tenant;
        var (own, provider) = await CreateProviderAsync(tenant, "torres@example.com", "Torres");
        using var __ = provider;
        var another = await CreateResourceAsync(tenant, "Sala 2");
        var service = await CreateServiceAsync(tenant, "Yoga", 60);
        var day = FutureMonday();
        var block = (await CreatedAsync(
            tenant.Admin, $"{tenant.Url}/blocks", OneOffBlockBody(day, [own, another], [service], "Clases", 5, "PublishedSessions")))
            .GetProperty("id").GetGuid();
        await CreatedAsync(tenant.Admin, $"{tenant.Url}/sessions", Publish(block, own, service, NineOClock(day)));
        await CreatedAsync(tenant.Admin, $"{tenant.Url}/sessions", Publish(block, another, service, NineOClock(day)));

        var mine = await GetJsonAsync(provider, ListUrl(tenant, day, day));
        var theirs = await provider.GetAsync(ListUrl(tenant, day, day, another), Ct);

        Assert.Equal(own, Assert.Single(mine.EnumerateArray()).GetProperty("resourceId").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, theirs.StatusCode);
    }

    [Fact]
    public async Task ListingNeedsAValidRangeAndAvailabilityManage()
    {
        var (tenant, _, _, _, day) = await PublishedBlockAsync();
        using var __ = tenant;
        using var reader = tenant.Client(null, SchedulingPermissions.AppointmentRead, SchedulingPermissions.AgendaReadAll);

        var missing = await tenant.Admin.GetAsync($"{tenant.Url}/sessions", Ct);
        var backwards = await tenant.Admin.GetAsync(ListUrl(tenant, day, day.AddDays(-1)), Ct);
        var tooLong = await tenant.Admin.GetAsync(ListUrl(tenant, day, day.AddDays(63)), Ct);
        var forbidden = await reader.GetAsync(ListUrl(tenant, day, day), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        var problem = await JsonOfAsync(missing);
        Assert.Equal("validation.failed", CodeOf(problem));
        Assert.True(HasError(problem, "from"));
        Assert.True(HasError(problem, "to"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, backwards.StatusCode);
        Assert.True(HasError(await JsonOfAsync(backwards), "to"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task AProviderCannotRemoveTheSessionOfAnotherResource()
    {
        var tenant = await NewTenantAsync(world.Factory);
        using var _ = tenant;
        var (own, provider) = await CreateProviderAsync(tenant, "torres@example.com", "Torres");
        using var __ = provider;
        var another = await CreateResourceAsync(tenant, "Sala 2");
        var service = await CreateServiceAsync(tenant, "Yoga", 60);
        var day = FutureMonday();
        var block = (await CreatedAsync(
            tenant.Admin, $"{tenant.Url}/blocks", OneOffBlockBody(day, [own, another], [service], "Clases", 5, "PublishedSessions")))
            .GetProperty("id").GetGuid();
        var theirs = await CreatedAsync(tenant.Admin, $"{tenant.Url}/sessions", Publish(block, another, service, NineOClock(day)));

        var response = await provider.DeleteAsync($"{tenant.Url}/sessions/{theirs.GetProperty("id").GetGuid()}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WithoutAvailabilityManageNoSessionRouteIsAvailable()
    {
        var (tenant, resource, service, block, day) = await PublishedBlockAsync();
        using var _ = tenant;
        using var reader = tenant.Client(null, SchedulingPermissions.AppointmentRead, SchedulingPermissions.AgendaReadAll);

        var publish = await reader.PostAsJsonAsync($"{tenant.Url}/sessions", Publish(block, resource, service, NineOClock(day)), Ct);
        var remove = await reader.DeleteAsync($"{tenant.Url}/sessions/{Guid.CreateVersion7()}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, publish.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);
    }
}
