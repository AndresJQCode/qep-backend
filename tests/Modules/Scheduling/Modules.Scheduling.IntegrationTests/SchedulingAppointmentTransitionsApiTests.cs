using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Scheduling.Application;
using Modules.Scheduling.Domain;
using static Modules.Scheduling.IntegrationTests.SchedulingApiHarness;

namespace Modules.Scheduling.IntegrationTests;

/// <summary>
/// The transitions of an appointment (plan 2026-11-21, Task 9): confirm, cancel, attend, no-show and reschedule. Each one needs
/// <c>If-Match</c> with the version the screen loaded, leaves a line in the history and an audit entry, and answers the whole appointment
/// with its new version. Cancelling and rescheduling move seats, so they run under the same resource lock as booking.
/// </summary>
public sealed class SchedulingAppointmentTransitionsApiTests(SchedulingApiWorld world) : IClassFixture<SchedulingApiWorld>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Scene(SchedulingTenant Tenant, Guid Resource, Guid Service, Guid Customer, DateOnly Day, Guid Block);

    private async Task<Scene> SceneAsync(int capacity = 1, string? mode = null, int duration = 30, params string[] extraResources)
    {
        var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var resourceIds = new List<Guid> { resource };
        foreach (var name in extraResources)
        {
            resourceIds.Add(await CreateResourceAsync(tenant, name));
        }

        var service = await CreateServiceAsync(tenant, "Limpieza", duration);
        var day = FutureMonday();
        var block = (await CreatedAsync(
                tenant.Admin, $"{tenant.Url}/blocks", OneOffBlockBody(day, resourceIds, [service], "Jornada", capacity, mode)))
            .GetProperty("id").GetGuid();
        return new Scene(tenant, resource, service, await CreateCustomerAsync(tenant), day, block);
    }

    private static async Task<JsonElement> BookedAsync(Scene scene, DateTimeOffset start, Guid? resource = null, string? source = null)
    {
        var response = await PostAsync(
            scene.Tenant.Admin, $"{scene.Tenant.Url}/appointments", BookBody(scene.Service, start, scene.Customer, resource, source), null);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    private static string UrlOf(Scene scene, JsonElement appointment, string action) =>
        $"{scene.Tenant.Url}/appointments/{appointment.GetProperty("id").GetGuid()}/{action}";

    private static async Task<JsonElement> ReadAsync(Scene scene, JsonElement appointment) =>
        await GetJsonAsync(scene.Tenant.Admin, $"{scene.Tenant.Url}/appointments/{appointment.GetProperty("id").GetGuid()}");

    private Task<int> AuditAsync(Scene scene, string action, JsonElement appointment) =>
        CountAuditEntriesAsync(
            world.Factory, scene.Tenant.IdText, action, SchedulingAuditActions.Resources.Appointment, appointment.GetProperty("id").GetGuid());

    private static string[] HistoryOf(JsonElement appointment) =>
        [.. appointment.GetProperty("history").EnumerateArray().Select(entry => entry.GetProperty("action").GetString()!)];

    private static async Task SetRulesAsync(SchedulingTenant tenant, string mode = "Automatic", int cutoffHours = 24)
    {
        var current = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");
        var rules = new
        {
            confirmationMode = mode,
            minNoticeMinutes = 120,
            horizonDays = 30,
            clientCancelCutoffHours = cutoffHours,
            reminderEnabled = false,
            reminderOffsetHours = 24,
        };
        (await PutAsync(tenant.Admin, $"{tenant.Url}/settings", rules, VersionOf(current))).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task APendingAppointmentIsConfirmedOnceAndLeavesItsTrail()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;
        await SetRulesAsync(scene.Tenant, "Manual");
        var pending = await BookedAsync(scene, NineOClock(scene.Day), source: "Channel");

        var confirmed = await PostAsync(scene.Tenant.Admin, UrlOf(scene, pending, "confirm"), null, VersionOf(pending));
        var again = await PostAsync(scene.Tenant.Admin, UrlOf(scene, pending, "confirm"), null, "2");

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var body = await JsonOfAsync(confirmed);
        Assert.Equal("Confirmed", body.GetProperty("status").GetString());
        Assert.Equal(2, body.GetProperty("version").GetInt64());
        Assert.Equal(["Created", "Confirmed"], HistoryOf(body));
        Assert.Equal(1, await AuditAsync(scene, SchedulingAuditActions.AppointmentConfirmed, pending));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal("scheduling.appointment.invalid_transition", CodeOf(await JsonOfAsync(again)));
        Assert.Equal(1, await AuditAsync(scene, SchedulingAuditActions.AppointmentConfirmed, pending));
    }

    [Fact]
    public async Task CancellingTheLastBookingFreesTheRange()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;
        var start = NineOClock(scene.Day);
        var booked = await BookedAsync(scene, start, scene.Resource);

        var cancelled = await PostAsync(
            scene.Tenant.Admin, UrlOf(scene, booked, "cancel"), new { reason = "El paciente no puede asistir" }, VersionOf(booked));

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var body = await JsonOfAsync(cancelled);
        Assert.Equal("Cancelled", body.GetProperty("status").GetString());
        Assert.Equal("El paciente no puede asistir", body.GetProperty("cancelReason").GetString());
        Assert.Equal(["Created", "Cancelled"], HistoryOf(body));
        Assert.Equal(1, await AuditAsync(scene, SchedulingAuditActions.AppointmentCancelled, booked));
        // The ad-hoc session is emptied and stops occupying the resource: the same start can be booked again.
        var session = Assert.Single(await ReadSessionsAsync(world.Factory, scene.Resource));
        Assert.Equal((0, false), (session.Booked, session.IsActive));
        var availability = await GetJsonAsync(
            scene.Tenant.Admin,
            $"{scene.Tenant.Url}/availability?serviceId={scene.Service}&from={scene.Day:yyyy-MM-dd}&to={scene.Day:yyyy-MM-dd}");
        Assert.Contains(availability.GetProperty("slots").EnumerateArray(), slot => slot.GetProperty("startsAt").GetDateTimeOffset() == start);
        var again = await BookedAsync(scene, start, scene.Resource);
        Assert.Equal("Confirmed", again.GetProperty("status").GetString());
        Assert.Equal(1, Assert.Single(await ReadSessionsAsync(world.Factory, scene.Resource)).Booked);
    }

    [Fact]
    public async Task CancellingOneOfSeveralBookingsKeepsTheSharedSessionOpen()
    {
        var scene = await SceneAsync(capacity: 5, mode: "FirstBookingOpens");
        using var owner = scene.Tenant;
        var first = await BookedAsync(scene, NineOClock(scene.Day), scene.Resource);
        await BookedAsync(scene, NineOClock(scene.Day), scene.Resource);

        (await PostAsync(scene.Tenant.Admin, UrlOf(scene, first, "cancel"), new { }, VersionOf(first))).EnsureSuccessStatusCode();

        var session = Assert.Single(await ReadSessionsAsync(world.Factory, scene.Resource));
        Assert.Equal((5, 1, true), (session.Capacity, session.Booked, session.IsActive));
    }

    [Fact]
    public async Task AClientCannotCancelInsideTheCutoffButTheBackOfficeCan()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;
        // The appointment is two or three weeks away; a cutoff of 720 hours (30 days) puts it inside the window.
        await SetRulesAsync(scene.Tenant, cutoffHours: 720);
        var inside = await BookedAsync(scene, NineOClock(scene.Day), scene.Resource);
        var other = await BookedAsync(scene, NineOClock(scene.Day).AddMinutes(30), scene.Resource);

        var byClient = await PostAsync(scene.Tenant.Admin, UrlOf(scene, inside, "cancel"), new { source = "Channel" }, VersionOf(inside));
        var byBackOffice = await PostAsync(scene.Tenant.Admin, UrlOf(scene, other, "cancel"), new { source = "BackOffice" }, VersionOf(other));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, byClient.StatusCode);
        Assert.Equal("scheduling.appointment.invalid_transition", CodeOf(await JsonOfAsync(byClient)));
        Assert.Equal("Confirmed", (await ReadAsync(scene, inside)).GetProperty("status").GetString());
        Assert.Equal(0, await AuditAsync(scene, SchedulingAuditActions.AppointmentCancelled, inside));
        Assert.Equal(HttpStatusCode.OK, byBackOffice.StatusCode);
        Assert.Equal(1, await AuditAsync(scene, SchedulingAuditActions.AppointmentCancelled, other));
    }

    [Fact]
    public async Task AClientCancelsFreelyOutsideTheCutoff()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;
        var booked = await BookedAsync(scene, NineOClock(scene.Day), scene.Resource);

        var response = await PostAsync(scene.Tenant.Admin, UrlOf(scene, booked, "cancel"), new { source = "Channel" }, VersionOf(booked));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RescheduleKeepsIdAndStatusAndLeavesAnEvent()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;
        await SetRulesAsync(scene.Tenant, "Manual");
        var start = NineOClock(scene.Day);
        var pending = await BookedAsync(scene, start, scene.Resource, "Channel");

        var response = await PutAsync(
            scene.Tenant.Admin, UrlOf(scene, pending, "reschedule"), new { startsAt = start.AddHours(1) }, VersionOf(pending));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOfAsync(response);
        Assert.Equal(pending.GetProperty("id").GetGuid(), body.GetProperty("id").GetGuid());
        Assert.Equal("Pending", body.GetProperty("status").GetString());
        Assert.Equal(start.AddHours(1), body.GetProperty("startsAt").GetDateTimeOffset());
        Assert.Equal(start.AddHours(1).AddMinutes(30), body.GetProperty("endsAt").GetDateTimeOffset());
        Assert.Equal(["Created", "Rescheduled"], HistoryOf(body));
        Assert.Equal(2, body.GetProperty("version").GetInt64());
        Assert.Equal(1, await AuditAsync(scene, SchedulingAuditActions.AppointmentRescheduled, pending));
        // The old range is free again and the new one holds the seat.
        var sessions = await ReadSessionsAsync(world.Factory, scene.Resource);
        Assert.Equal(2, sessions.Count);
        Assert.Equal((0, false), (sessions[0].Booked, sessions[0].IsActive));
        Assert.Equal((1, true), (sessions[1].Booked, sessions[1].IsActive));
        Assert.Equal(sessions[1].StartsAt, start.AddHours(1));
        await BookedAsync(scene, start, scene.Resource);
    }

    [Fact]
    public async Task RescheduleToTheNextStartOfAServiceWithMarginWorksBecauseTheOldSeatIsReleasedFirst()
    {
        var tenant = await NewTenantAsync(world.Factory);
        using var owner = tenant;
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var service = (await CreatedAsync(
                tenant.Admin, $"{tenant.Url}/services", new { name = "Limpieza", durationMinutes = 30, bufferMinutes = 10, bookableByChannel = true }))
            .GetProperty("id").GetGuid();
        var day = FutureMonday();
        await CreatedAsync(tenant.Admin, $"{tenant.Url}/blocks", OneOffBlockBody(day, [resource], [service]));
        var scene = new Scene(tenant, resource, service, await CreateCustomerAsync(tenant), day, Guid.Empty);
        var start = NineOClock(day);
        var booked = await BookedAsync(scene, start, resource);

        // 09:30 overlaps the 10 minutes of margin of the appointment itself, which is about to leave.
        var response = await PutAsync(
            tenant.Admin, UrlOf(scene, booked, "reschedule"), new { startsAt = start.AddMinutes(30) }, VersionOf(booked));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RescheduleToAnotherResourceMovesTheSeat()
    {
        var scene = await SceneAsync(extraResources: "Dr. Mora");
        using var owner = scene.Tenant;
        var start = NineOClock(scene.Day);
        var booked = await BookedAsync(scene, start, scene.Resource);
        var resources = await GetJsonAsync(scene.Tenant.Admin, $"{scene.Tenant.Url}/resources");
        var mora = resources.EnumerateArray().Single(resource => resource.GetProperty("displayName").GetString() == "Dr. Mora").GetProperty("id").GetGuid();

        var response = await PutAsync(
            scene.Tenant.Admin, UrlOf(scene, booked, "reschedule"), new { resourceId = mora, startsAt = start }, VersionOf(booked));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOfAsync(response);
        Assert.Equal(mora, body.GetProperty("resourceId").GetGuid());
        Assert.Equal("Dr. Mora", body.GetProperty("resourceName").GetString());
        Assert.Equal(0, Assert.Single(await ReadSessionsAsync(world.Factory, scene.Resource)).Booked);
        Assert.Equal(1, Assert.Single(await ReadSessionsAsync(world.Factory, mora)).Booked);
    }

    [Fact]
    public async Task RescheduleToATakenStartIs409AndKeepsTheOldSeat()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;
        var start = NineOClock(scene.Day);
        var mine = await BookedAsync(scene, start, scene.Resource);
        await BookedAsync(scene, start.AddMinutes(30), scene.Resource);

        var response = await PutAsync(
            scene.Tenant.Admin, UrlOf(scene, mine, "reschedule"), new { startsAt = start.AddMinutes(30) }, VersionOf(mine));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("scheduling.appointment.slot_taken", CodeOf(await JsonOfAsync(response)));
        var reread = await ReadAsync(scene, mine);
        Assert.Equal(start, reread.GetProperty("startsAt").GetDateTimeOffset());
        Assert.Equal(1, reread.GetProperty("version").GetInt64());
        Assert.All(await ReadSessionsAsync(world.Factory, scene.Resource), session => Assert.Equal((1, true), (session.Booked, session.IsActive)));
    }

    [Fact]
    public async Task RescheduleToAStartOutsideTheRulesOrToTheSameOneIs422()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;
        var start = NineOClock(scene.Day);
        var booked = await BookedAsync(scene, start, scene.Resource);

        var offGrid = await PutAsync(
            scene.Tenant.Admin, UrlOf(scene, booked, "reschedule"), new { startsAt = start.AddMinutes(10) }, VersionOf(booked));
        var same = await PutAsync(scene.Tenant.Admin, UrlOf(scene, booked, "reschedule"), new { startsAt = start }, VersionOf(booked));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, offGrid.StatusCode);
        Assert.Equal("scheduling.appointment.outside_rules", CodeOf(await JsonOfAsync(offGrid)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, same.StatusCode);
        Assert.True(HasError(await JsonOfAsync(same), "startsAt"));
        Assert.Equal(1, (await ReadAsync(scene, booked)).GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task ACancelledAppointmentCannotBeConfirmedRescheduledOrCancelledAgain()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;
        var start = NineOClock(scene.Day);
        var booked = await BookedAsync(scene, start, scene.Resource);
        var cancelled = await JsonOfAsync(await PostAsync(scene.Tenant.Admin, UrlOf(scene, booked, "cancel"), new { }, VersionOf(booked)));
        var version = VersionOf(cancelled);

        var confirm = await PostAsync(scene.Tenant.Admin, UrlOf(scene, booked, "confirm"), null, version);
        var cancel = await PostAsync(scene.Tenant.Admin, UrlOf(scene, booked, "cancel"), new { }, version);
        var reschedule = await PutAsync(
            scene.Tenant.Admin, UrlOf(scene, booked, "reschedule"), new { startsAt = start.AddHours(1) }, version);

        foreach (var response in new[] { confirm, cancel, reschedule })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("scheduling.appointment.invalid_transition", CodeOf(await JsonOfAsync(response)));
        }

        Assert.Equal(["Created", "Cancelled"], HistoryOf(await ReadAsync(scene, booked)));
        Assert.Equal(0, Assert.Single(await ReadSessionsAsync(world.Factory, scene.Resource)).Booked);
    }

    [Fact]
    public async Task NoShowAndAttendedBeforeTheStartAre422()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;
        var booked = await BookedAsync(scene, NineOClock(scene.Day), scene.Resource);

        var noShow = await PostAsync(scene.Tenant.Admin, UrlOf(scene, booked, "no-show"), null, VersionOf(booked));
        var attended = await PostAsync(scene.Tenant.Admin, UrlOf(scene, booked, "attend"), null, VersionOf(booked));

        foreach (var response in new[] { noShow, attended })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("scheduling.appointment.before_start", CodeOf(await JsonOfAsync(response)));
        }

        var reread = await ReadAsync(scene, booked);
        Assert.Equal(("Confirmed", 1), (reread.GetProperty("status").GetString(), (int)reread.GetProperty("version").GetInt64()));
        Assert.Equal(0, await AuditAsync(scene, SchedulingAuditActions.AppointmentNoShow, booked));
        Assert.Equal(0, await AuditAsync(scene, SchedulingAuditActions.AppointmentAttended, booked));
    }

    // A confirmed appointment that already started, seeded in the past (the booking API never creates one): the happy path of the two marks.
    private async Task<(Scene Scene, JsonElement Seeded)> StartedAppointmentAsync()
    {
        var scene = await SceneAsync();
        var location = await MainLocationIdAsync(scene.Tenant);
        var id = await SeedAppointmentAsync(
            world.Factory, scene.Tenant.IdText, location, scene.Resource, scene.Service, scene.Customer,
            NineOClock(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3)), AppointmentStatus.Confirmed);
        var seeded = await GetJsonAsync(scene.Tenant.Admin, $"{scene.Tenant.Url}/appointments/{id}");
        return (scene, seeded);
    }

    [Fact]
    public async Task AStartedAppointmentIsMarkedAttendedWithItsTrail()
    {
        var (scene, seeded) = await StartedAppointmentAsync();
        using var owner = scene.Tenant;

        var response = await PostAsync(scene.Tenant.Admin, UrlOf(scene, seeded, "attend"), null, VersionOf(seeded));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOfAsync(response);
        Assert.Equal("Completed", body.GetProperty("status").GetString());
        Assert.Equal(seeded.GetProperty("version").GetInt64() + 1, body.GetProperty("version").GetInt64());
        Assert.Equal(["Created", "Attended"], HistoryOf(body));
        Assert.Equal(1, await AuditAsync(scene, SchedulingAuditActions.AppointmentAttended, seeded));
        Assert.Equal(0, await AuditAsync(scene, SchedulingAuditActions.AppointmentNoShow, seeded));
    }

    [Fact]
    public async Task AStartedAppointmentIsMarkedNoShowWithItsTrail()
    {
        var (scene, seeded) = await StartedAppointmentAsync();
        using var owner = scene.Tenant;

        var response = await PostAsync(scene.Tenant.Admin, UrlOf(scene, seeded, "no-show"), null, VersionOf(seeded));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOfAsync(response);
        Assert.Equal("NoShow", body.GetProperty("status").GetString());
        Assert.Equal(seeded.GetProperty("version").GetInt64() + 1, body.GetProperty("version").GetInt64());
        Assert.Equal(["Created", "NoShow"], HistoryOf(body));
        Assert.Equal(1, await AuditAsync(scene, SchedulingAuditActions.AppointmentNoShow, seeded));
        Assert.Equal(0, await AuditAsync(scene, SchedulingAuditActions.AppointmentAttended, seeded));
    }

    [Fact]
    public async Task EveryWriteNeedsIfMatchAndAStaleVersionIs412()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;
        var booked = await BookedAsync(scene, NineOClock(scene.Day), scene.Resource);

        var missing = await PostAsync(scene.Tenant.Admin, UrlOf(scene, booked, "cancel"), new { }, null);
        var stale = await PostAsync(scene.Tenant.Admin, UrlOf(scene, booked, "cancel"), new { }, "99");
        var staleMove = await PutAsync(
            scene.Tenant.Admin, UrlOf(scene, booked, "reschedule"), new { startsAt = NineOClock(scene.Day).AddHours(1) }, "99");
        var missingMove = await PutAsync(
            scene.Tenant.Admin, UrlOf(scene, booked, "reschedule"), new { startsAt = NineOClock(scene.Day).AddHours(1) }, null);

        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.Equal("precondition.if_match_required", CodeOf(await JsonOfAsync(missing)));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("concurrency.conflict", CodeOf(await JsonOfAsync(stale)));
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleMove.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missingMove.StatusCode);
        Assert.Equal("Confirmed", (await ReadAsync(scene, booked)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task ATransitionOnAnUnknownAppointmentIs404()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;

        var response = await PostAsync(
            scene.Tenant.Admin, $"{scene.Tenant.Url}/appointments/{Guid.CreateVersion7()}/confirm", null, "1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("scheduling.appointment.not_found", CodeOf(await JsonOfAsync(response)));
    }

    [Fact]
    public async Task AMemberWithoutReadAllCannotTouchAnotherResourcesAppointmentAndWithoutManageNobodyCan()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;
        var booked = await BookedAsync(scene, NineOClock(scene.Day), scene.Resource);
        var (_, userId) = await SeedMemberAsync(world.Factory, scene.Tenant.IdText, "mora@example.com", "Mora");
        using var elsewhere = scene.Tenant.Client(userId.ToString(), SchedulingPermissions.AppointmentRead, SchedulingPermissions.AppointmentManage);
        using var readOnly = scene.Tenant.Client(null, SchedulingPermissions.AppointmentRead);
        using var stranger = await NewTenantAsync(world.Factory, "Clínica Sur");

        var outOfScope = await PostAsync(elsewhere, UrlOf(scene, booked, "cancel"), new { }, VersionOf(booked));
        var withoutManage = await PostAsync(readOnly, UrlOf(scene, booked, "cancel"), new { }, VersionOf(booked));
        var crossTenant = await PostAsync(stranger.Admin, UrlOf(scene, booked, "cancel"), new { }, VersionOf(booked));

        foreach (var response in new[] { outOfScope, withoutManage, crossTenant })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        Assert.Equal("authorization.denied", CodeOf(await JsonOfAsync(outOfScope)));
        Assert.Equal("Confirmed", (await ReadAsync(scene, booked)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task APublishedSessionTheTenantRemovedCannotBeBooked()
    {
        var scene = await SceneAsync(capacity: 5, mode: "PublishedSessions", duration: 60);
        using var owner = scene.Tenant;
        var start = NineOClock(scene.Day);
        var published = await CreatedAsync(
            scene.Tenant.Admin, $"{scene.Tenant.Url}/sessions",
            new { blockId = scene.Block, resourceId = scene.Resource, serviceId = scene.Service, startsAt = start });
        var sessionId = published.GetProperty("id").GetGuid();
        var booked = await BookedAsync(scene, start, scene.Resource);
        (await PostAsync(scene.Tenant.Admin, UrlOf(scene, booked, "cancel"), new { }, VersionOf(booked))).EnsureSuccessStatusCode();
        // Only cancelled history is left: removing the session deactivates it (PD-3) and it stays marked as published.
        var removed = await scene.Tenant.Admin.DeleteAsync($"{scene.Tenant.Url}/sessions/{sessionId}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal((true, false, true), (await ReadPublishedFlagsAsync(sessionId)));

        var again = await PostAsync(
            scene.Tenant.Admin, $"{scene.Tenant.Url}/appointments", BookBody(scene.Service, start, scene.Customer, scene.Resource), null);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("scheduling.appointment.slot_taken", CodeOf(await JsonOfAsync(again)));
        // The booking did not reopen it, and no second appointment exists.
        Assert.Equal((true, false, true), (await ReadPublishedFlagsAsync(sessionId)));
        Assert.Equal(1, (await CountAppointmentsAsync(world.Factory, scene.Resource)).Appointments);
    }

    private async Task<(bool Exists, bool IsActive, bool IsPublished)> ReadPublishedFlagsAsync(Guid sessionId)
    {
        var (exists, isActive, isPublished, _) = await ReadSessionAsync(world.Factory, sessionId);
        return (exists, isActive, isPublished);
    }

    // D-17: no write over HTTP marks its history line as the channel's, not even one that declares source Channel.
    [Fact]
    public async Task BackOfficeWritesLeaveByChannelFalse()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;
        var booked = await BookedAsync(scene, NineOClock(scene.Day), scene.Resource, source: "Channel");

        var cancelled = await PostAsync(
            scene.Tenant.Admin, UrlOf(scene, booked, "cancel"), new { reason = "Pedido por el cliente", source = "Channel" }, VersionOf(booked));

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var history = (await JsonOfAsync(cancelled)).GetProperty("history").EnumerateArray().ToArray();
        Assert.Equal(["Created", "Cancelled"], history.Select(entry => entry.GetProperty("action").GetString()));
        Assert.All(history, entry => Assert.False(entry.GetProperty("byChannel").GetBoolean()));
    }
}
