using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Scheduling.Application;
using static Modules.Scheduling.IntegrationTests.SchedulingApiHarness;

namespace Modules.Scheduling.IntegrationTests;

/// <summary>
/// <c>POST appointments</c> (plan 2026-11-21, Tarea 9): one transaction that revalidates the start, locks the resource row, finds or opens
/// the session, checks the seat and writes the appointment, its history and its audit. The concurrency tests are the point of the task
/// (Review Focus 1): the last seat goes to exactly one request.
/// </summary>
public sealed class SchedulingBookingApiTests(SchedulingApiWorld world) : IClassFixture<SchedulingApiWorld>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Scene(SchedulingTenant Tenant, Guid Resource, Guid Service, Guid Customer, DateOnly Day, Guid Block);

    private async Task<Scene> SceneAsync(
        int capacity = 1, string? mode = null, int duration = 30, int buffer = 0, bool bookableByChannel = true)
    {
        var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var service = (await CreatedAsync(
                tenant.Admin,
                $"{tenant.Url}/services",
                new { name = "Limpieza", durationMinutes = duration, bufferMinutes = buffer, bookableByChannel }))
            .GetProperty("id").GetGuid();
        var day = FutureMonday();
        var block = (await CreatedAsync(
                tenant.Admin, $"{tenant.Url}/blocks", OneOffBlockBody(day, [resource], [service], "Jornada", capacity, mode)))
            .GetProperty("id").GetGuid();
        return new Scene(tenant, resource, service, await CreateCustomerAsync(tenant), day, block);
    }

    private static Task<HttpResponseMessage> BookAsync(
        Scene scene,
        DateTimeOffset start,
        Guid? resource = null,
        string? source = null,
        string? clientRequestId = null,
        HttpClient? client = null,
        Guid? customer = null) =>
        PostAsync(
            client ?? scene.Tenant.Admin,
            $"{scene.Tenant.Url}/appointments",
            BookBody(scene.Service, start, customer ?? scene.Customer, resource, source, clientRequestId),
            null);

    private static async Task<JsonElement> BookedAsync(
        Scene scene,
        DateTimeOffset start,
        Guid? resource = null,
        string? source = null,
        string? clientRequestId = null,
        HttpClient? client = null)
    {
        var response = await BookAsync(scene, start, resource, source, clientRequestId, client);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    private static async Task SetConfirmationModeAsync(SchedulingTenant tenant, string mode, int cutoffHours = 24)
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
    public async Task TwentyParallelBookingsOfTheLastSlotCreateExactlyOne()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;
        var start = NineOClock(scene.Day);

        // No resource chosen: every request goes through the FirstFree assignment inside the transaction.
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => BookAsync(scene, start)));

        var created = responses.Where(response => response.StatusCode == HttpStatusCode.Created).ToArray();
        var conflicts = responses.Where(response => response.StatusCode == HttpStatusCode.Conflict).ToArray();
        Assert.Single(created);
        Assert.Equal(19, conflicts.Length);
        foreach (var conflict in conflicts)
        {
            Assert.Equal("scheduling.appointment.slot_taken", CodeOf(await JsonOfAsync(conflict)));
        }

        var appointment = await JsonOfAsync(created[0]);
        Assert.Equal((1, 1), await CountAppointmentsAsync(world.Factory, scene.Resource));
        var session = Assert.Single(await ReadSessionsAsync(world.Factory, scene.Resource));
        Assert.Equal((1, 1, true), (session.Capacity, session.Booked, session.IsActive));
        Assert.Equal(
            1,
            await CountAuditEntriesAsync(
                world.Factory, scene.Tenant.IdText, SchedulingAuditActions.AppointmentCreated, SchedulingAuditActions.Resources.Appointment,
                appointment.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task TwentyParallelBookingsNamingTheResourceAlsoCreateExactlyOne()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => BookAsync(scene, NineOClock(scene.Day), scene.Resource)));

        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
        Assert.Equal(19, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal((1, 1), await CountAppointmentsAsync(world.Factory, scene.Resource));
    }

    [Fact]
    public async Task ASessionOfFiveSeatsNeverHoldsSix()
    {
        var scene = await SceneAsync(capacity: 5, mode: "FirstBookingOpens", duration: 60);
        using var owner = scene.Tenant;

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => BookAsync(scene, NineOClock(scene.Day), scene.Resource)));

        Assert.Equal(5, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
        var full = responses.Where(response => response.StatusCode == HttpStatusCode.Conflict).ToArray();
        Assert.Equal(7, full.Length);
        foreach (var conflict in full)
        {
            Assert.Equal("scheduling.appointment.session_full", CodeOf(await JsonOfAsync(conflict)));
        }

        var session = Assert.Single(await ReadSessionsAsync(world.Factory, scene.Resource));
        Assert.Equal((5, 5), (session.Capacity, session.Booked));
        Assert.Equal(5, (await CountAppointmentsAsync(world.Factory, scene.Resource)).Appointments);
    }

    // Written, not run (owner's order: the suites run at the end). A closure and a booking for the same half hour at once: both take the
    // resource lock, so one of them goes first and the other sees it. Never an appointment inside a closure.
    [Fact]
    public async Task AClosureAndABookingOfTheSameStartNeverBothLand()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;
        var starts = Enumerable.Range(0, 6).Select(index => NineOClock(scene.Day).AddMinutes(30 * index)).ToArray();

        var pairs = await Task.WhenAll(starts.Select(async start =>
        {
            var closing = scene.Tenant.Admin.PostAsJsonAsync(
                $"{scene.Tenant.Url}/closures",
                new { resourceId = scene.Resource, startsAt = start, endsAt = start.AddMinutes(30), reason = "Carrera" },
                TestContext.Current.CancellationToken);
            var booking = BookAsync(scene, start, scene.Resource);
            return (Closure: await closing, Booking: await booking);
        }));

        foreach (var (closure, booking) in pairs)
        {
            var closed = closure.StatusCode == HttpStatusCode.Created;
            var booked = booking.StatusCode == HttpStatusCode.Created;
            Assert.False(closed && booked, "A closure and an appointment landed on the same start.");
            if (!closed)
            {
                Assert.Equal(HttpStatusCode.UnprocessableEntity, closure.StatusCode);
                Assert.Equal("scheduling.closure.overlaps_appointments", CodeOf(await JsonOfAsync(closure)));
            }

            if (!booked)
            {
                Assert.Equal(HttpStatusCode.Conflict, booking.StatusCode);
                Assert.Equal("scheduling.appointment.slot_taken", CodeOf(await JsonOfAsync(booking)));
            }
        }

        var closures = (await GetJsonAsync(scene.Tenant.Admin, $"{scene.Tenant.Url}/closures")).GetArrayLength();
        var appointments = (await CountAppointmentsAsync(world.Factory, scene.Resource)).Appointments;
        Assert.Equal(starts.Length, closures + appointments);
    }

    // Written, not run. Archiving a resource and booking it at once: the archive refuses when the booking got the lock first, and the
    // booking is refused when the archive did. Never an archived resource holding a future appointment.
    [Fact]
    public async Task ArchivingAResourceAndBookingItNeverBothLand()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;

        var archiving = scene.Tenant.Admin.PostAsync(
            $"{scene.Tenant.Url}/resources/{scene.Resource}/archive", null, TestContext.Current.CancellationToken);
        var booking = BookAsync(scene, NineOClock(scene.Day), scene.Resource);
        var archived = await archiving;
        var booked = await booking;

        var archivedOk = archived.StatusCode == HttpStatusCode.OK;
        var bookedOk = booked.StatusCode == HttpStatusCode.Created;
        Assert.False(archivedOk && bookedOk, "A resource was archived with a future appointment on it.");
        if (!archivedOk)
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, archived.StatusCode);
            Assert.Equal("scheduling.resource.has_future_appointments", CodeOf(await JsonOfAsync(archived)));
        }

        Assert.Equal(bookedOk ? 1 : 0, (await CountAppointmentsAsync(world.Factory, scene.Resource)).Appointments);
    }

    // Written, not run. Deleting a customer and booking an appointment for them at once (spec 2026-11-10, Review Focus 5): the booking
    // holds the customer's shared reference lock while it saves, so either it goes first and the deletion is blocked by the new
    // appointment, or the deletion goes first and the booking is refused. Never an appointment pointing at a deleted customer.
    [Fact]
    public async Task ADeletedCustomerAndABookingForThemNeverBothLand()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;
        var doomed = await CreateCustomerAsync(scene.Tenant, "Cliente efímero", "3009876543");
        using var deleter = scene.Tenant.Client(null, "customers.customer.delete");

        var booking = BookAsync(scene, NineOClock(scene.Day), scene.Resource, customer: doomed);
        var deleting = deleter.PostAsJsonAsync(
            $"/api/v1/tenants/{scene.Tenant.IdText}/customers/bulk-delete",
            new { customerIds = new[] { doomed } },
            TestContext.Current.CancellationToken);
        var booked = await booking;
        var deletion = await JsonOfAsync(await deleting);

        var deleted = deletion.GetProperty("deleted").EnumerateArray().Select(id => id.GetGuid()).Contains(doomed);
        var appointments = (await CountAppointmentsAsync(world.Factory, scene.Resource)).Appointments;
        if (booked.StatusCode == HttpStatusCode.Created)
        {
            Assert.False(deleted, "A customer was deleted with an appointment pointing at them.");
            Assert.Equal(1, appointments);
        }
        else
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, booked.StatusCode);
            Assert.True(HasError(await JsonOfAsync(booked), "customerId"));
            Assert.True(deleted);
            Assert.Equal(0, appointments);
        }
    }

    [Fact]
    public async Task AStartThatIsNotAValidStartIs422OutsideRules()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;

        // The grid is every 30 minutes from 09:00: 09:10 is not a start, and neither is a day without a block. The server rules over the client.
        var offGrid = await BookAsync(scene, NineOClock(scene.Day).AddMinutes(10), scene.Resource);
        var otherDay = await BookAsync(scene, NineOClock(scene.Day.AddDays(1)), scene.Resource);
        var afterClosing = await BookAsync(scene, NineOClock(scene.Day).AddHours(3), scene.Resource);

        foreach (var response in new[] { offGrid, otherDay, afterClosing })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("scheduling.appointment.outside_rules", CodeOf(await JsonOfAsync(response)));
        }

        Assert.Equal((0, 0), await CountAppointmentsAsync(world.Factory, scene.Resource));
        Assert.Empty(await ReadSessionsAsync(world.Factory, scene.Resource));
    }

    [Fact]
    public async Task ARepeatedClientRequestIdReturnsTheSameAppointment()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;
        var start = NineOClock(scene.Day);

        var first = await BookedAsync(scene, start, clientRequestId: "wa-1001");
        var repeat = await BookedAsync(scene, start, clientRequestId: "wa-1001");

        Assert.Equal(first.GetProperty("id").GetGuid(), repeat.GetProperty("id").GetGuid());
        Assert.Equal((1, 1), await CountAppointmentsAsync(world.Factory, scene.Resource));
        Assert.Equal(1, Assert.Single(await ReadSessionsAsync(world.Factory, scene.Resource)).Booked);
        Assert.Equal(
            1,
            await CountAuditEntriesAsync(
                world.Factory, scene.Tenant.IdText, SchedulingAuditActions.AppointmentCreated, SchedulingAuditActions.Resources.Appointment,
                first.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task TheSameClientRequestIdInParallelCreatesOneAppointment()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 5).Select(_ => BookAsync(scene, NineOClock(scene.Day), scene.Resource, clientRequestId: "wa-2002")));

        // On one resource the lock serializes the five: the first books, the other four meet its appointment once they hold the lock and
        // answer it. Every response is a 201 (a 409 here would mean a repeat was refused; the index race of two resources does not apply).
        var ids = new HashSet<Guid>();
        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            ids.Add((await JsonOfAsync(response)).GetProperty("id").GetGuid());
        }

        Assert.Single(ids);
        Assert.Equal((1, 1), await CountAppointmentsAsync(world.Factory, scene.Resource));
    }

    [Fact]
    public async Task ABackOfficeBookingIsConfirmedAndAChannelOneFollowsTheMode()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;
        var start = NineOClock(scene.Day);

        var automatic = await BookedAsync(scene, start, source: "Channel");
        await SetConfirmationModeAsync(scene.Tenant, "Manual");
        var manualChannel = await BookedAsync(scene, start.AddMinutes(30), source: "Channel");
        var backOffice = await BookedAsync(scene, start.AddMinutes(60));

        Assert.Equal("Confirmed", automatic.GetProperty("status").GetString());
        Assert.Equal("Channel", automatic.GetProperty("source").GetString());
        Assert.Equal("Pending", manualChannel.GetProperty("status").GetString());
        Assert.Equal("Confirmed", backOffice.GetProperty("status").GetString());
        Assert.Equal("BackOffice", backOffice.GetProperty("source").GetString());
        foreach (var appointment in new[] { automatic, manualChannel, backOffice })
        {
            var entry = Assert.Single(appointment.GetProperty("history").EnumerateArray());
            Assert.Equal("Created", entry.GetProperty("action").GetString());
        }
    }

    [Fact]
    public async Task AChannelCannotBookAServiceThatIsNotBookableByChannel()
    {
        var scene = await SceneAsync(bookableByChannel: false);
        using var owner = scene.Tenant;

        var channel = await BookAsync(scene, NineOClock(scene.Day), source: "Channel");
        var backOffice = await BookAsync(scene, NineOClock(scene.Day));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, channel.StatusCode);
        Assert.Equal("scheduling.appointment.outside_rules", CodeOf(await JsonOfAsync(channel)));
        Assert.Equal(HttpStatusCode.Created, backOffice.StatusCode);
    }

    [Fact]
    public async Task APendingAppointmentAlreadyHoldsItsSeat()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;
        var start = NineOClock(scene.Day);
        await SetConfirmationModeAsync(scene.Tenant, "Manual");
        var pending = await BookedAsync(scene, start, source: "Channel");

        var second = await BookAsync(scene, start, scene.Resource);
        var availability = await GetJsonAsync(
            scene.Tenant.Admin,
            $"{scene.Tenant.Url}/availability?serviceId={scene.Service}&from={scene.Day:yyyy-MM-dd}&to={scene.Day:yyyy-MM-dd}");

        Assert.Equal("Pending", pending.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("scheduling.appointment.slot_taken", CodeOf(await JsonOfAsync(second)));
        Assert.DoesNotContain(
            availability.GetProperty("slots").EnumerateArray(), slot => slot.GetProperty("startsAt").GetDateTimeOffset() == start);
    }

    [Fact]
    public async Task ABookedStartLeavesTheListAndASeatOfASharedSessionIsCounted()
    {
        var scene = await SceneAsync(capacity: 5, mode: "FirstBookingOpens");
        using var owner = scene.Tenant;
        var start = NineOClock(scene.Day);
        await BookedAsync(scene, start, scene.Resource);

        var availability = await GetJsonAsync(
            scene.Tenant.Admin,
            $"{scene.Tenant.Url}/availability?serviceId={scene.Service}&from={scene.Day:yyyy-MM-dd}&to={scene.Day:yyyy-MM-dd}");

        var slots = availability.GetProperty("slots").EnumerateArray().ToArray();
        Assert.Equal(4, slots.Single(slot => slot.GetProperty("startsAt").GetDateTimeOffset() == start).GetProperty("seatsLeft").GetInt32());
        Assert.Equal(
            5,
            slots.Single(slot => slot.GetProperty("startsAt").GetDateTimeOffset() == start.AddMinutes(30)).GetProperty("seatsLeft").GetInt32());
    }

    [Fact]
    public async Task TheBufferOfAServiceBlocksTheNextStart()
    {
        var scene = await SceneAsync(buffer: 10);
        using var owner = scene.Tenant;
        var start = NineOClock(scene.Day);
        await BookedAsync(scene, start, scene.Resource);

        // 09:30 would start while the 10 minutes of margin after the 09:00 appointment still hold the resource.
        var next = await BookAsync(scene, start.AddMinutes(30), scene.Resource);
        var later = await BookAsync(scene, start.AddMinutes(60), scene.Resource);

        Assert.Equal(HttpStatusCode.Conflict, next.StatusCode);
        Assert.Equal("scheduling.appointment.slot_taken", CodeOf(await JsonOfAsync(next)));
        Assert.Equal(HttpStatusCode.Created, later.StatusCode);
    }

    [Fact]
    public async Task WithoutAResourceTheFirstFreeOneOfTheBlockTakesIt()
    {
        var tenant = await NewTenantAsync(world.Factory);
        using var owner = tenant;
        var first = await CreateResourceAsync(tenant, "Dra. Torres");
        var second = await CreateResourceAsync(tenant, "Dr. Mora");
        var service = await CreateServiceAsync(tenant, "Limpieza", 30);
        var day = FutureMonday();
        await CreatedAsync(tenant.Admin, $"{tenant.Url}/blocks", OneOffBlockBody(day, [first, second], [service]));
        var customer = await CreateCustomerAsync(tenant);
        var scene = new Scene(tenant, first, service, customer, day, Guid.Empty);
        var start = NineOClock(day);

        var one = await BookedAsync(scene, start);
        var two = await BookedAsync(scene, start);
        var three = await BookAsync(scene, start);

        Assert.Equal(first, one.GetProperty("resourceId").GetGuid());
        Assert.Equal(second, two.GetProperty("resourceId").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict, three.StatusCode);
        Assert.Equal("scheduling.appointment.slot_taken", CodeOf(await JsonOfAsync(three)));
    }

    [Fact]
    public async Task ABookingOnAResourceOutsideTheScopeIs403AndOnItsOwnIs201()
    {
        var tenant = await NewTenantAsync(world.Factory);
        using var owner = tenant;
        var (own, _) = await CreateProviderAsync(tenant, "torres@example.com", "Torres");
        var (memberId, userId) = await SeedMemberAsync(world.Factory, tenant.IdText, "mora@example.com", "Mora");
        var mine = await CreateResourceAsync(tenant, "Dr. Mora", memberId);
        var service = await CreateServiceAsync(tenant, "Limpieza", 30);
        var day = FutureMonday();
        await CreatedAsync(tenant.Admin, $"{tenant.Url}/blocks", OneOffBlockBody(day, [own, mine], [service]));
        var customer = await CreateCustomerAsync(tenant);
        using var provider = tenant.Client(userId.ToString(), SchedulingPermissions.AppointmentRead, SchedulingPermissions.AppointmentManage);
        var scene = new Scene(tenant, mine, service, customer, day, Guid.Empty);

        var foreign = await BookAsync(scene, NineOClock(day), own, client: provider);
        var allowed = await BookAsync(scene, NineOClock(day), mine, client: provider);
        // Without a resource, the assignment only looks inside the scope: it can never land on the other doctor.
        var assigned = await BookedAsync(scene, NineOClock(day).AddMinutes(30), client: provider);

        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal("authorization.denied", CodeOf(await JsonOfAsync(foreign)));
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        Assert.Equal(mine, assigned.GetProperty("resourceId").GetGuid());
    }

    [Fact]
    public async Task WithoutAppointmentManageOrInAnotherTenantABookingIs403()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;
        using var readOnly = scene.Tenant.Client(null, SchedulingPermissions.AppointmentRead);
        using var stranger = await NewTenantAsync(world.Factory, "Clínica Sur");

        var withoutPermission = await BookAsync(scene, NineOClock(scene.Day), client: readOnly);
        var crossTenant = await BookAsync(scene, NineOClock(scene.Day), client: stranger.Admin);

        Assert.Equal(HttpStatusCode.Forbidden, withoutPermission.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crossTenant.StatusCode);
        Assert.Equal("authorization.denied", CodeOf(await JsonOfAsync(crossTenant)));
        Assert.Equal((0, 0), await CountAppointmentsAsync(world.Factory, scene.Resource));
    }

    [Fact]
    public async Task AnUnknownCustomerOrOneOfAnotherTenantIs422OnTheCustomerField()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;
        using var other = await NewTenantAsync(world.Factory, "Clínica Sur");
        var foreignCustomer = await CreateCustomerAsync(other, "Luis Mora");

        var unknown = await BookAsync(scene, NineOClock(scene.Day), customer: Guid.CreateVersion7());
        var foreign = await BookAsync(scene, NineOClock(scene.Day), customer: foreignCustomer);

        foreach (var response in new[] { unknown, foreign })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.True(HasError(await JsonOfAsync(response), "customerId"));
        }
    }

    [Fact]
    public async Task AnUnknownServiceOrResourceIs422OnItsField()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;

        var service = await PostAsync(
            scene.Tenant.Admin, $"{scene.Tenant.Url}/appointments",
            BookBody(Guid.CreateVersion7(), NineOClock(scene.Day), scene.Customer), null);
        var resource = await BookAsync(scene, NineOClock(scene.Day), Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, service.StatusCode);
        Assert.True(HasError(await JsonOfAsync(service), "serviceId"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resource.StatusCode);
        Assert.True(HasError(await JsonOfAsync(resource), "resourceId"));
    }

    [Fact]
    public async Task ABookingWithoutTheRequiredFieldsIs422WithTheErrorsMap()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;

        var response = await PostAsync(scene.Tenant.Admin, $"{scene.Tenant.Url}/appointments", new { note = "sin datos" }, null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await JsonOfAsync(response);
        Assert.True(HasError(problem, "serviceId"));
        Assert.True(HasError(problem, "customerId"));
        Assert.True(HasError(problem, "startsAt"));
    }

    [Fact]
    public async Task AnAppointmentWritesItsAuditAndHistory()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;
        var customer = await CreateCustomerAsync(scene.Tenant, "Carlos Peña", "3151112233");

        var booked = await JsonOfAsync(
            await PostAsync(
                scene.Tenant.Admin, $"{scene.Tenant.Url}/appointments",
                BookBody(scene.Service, NineOClock(scene.Day), customer, scene.Resource, note: "Primera vez"), null));
        var id = booked.GetProperty("id").GetGuid();
        var read = await GetJsonAsync(scene.Tenant.Admin, $"{scene.Tenant.Url}/appointments/{id}");

        Assert.Equal(1, await CountAuditEntriesAsync(
            world.Factory, scene.Tenant.IdText, SchedulingAuditActions.AppointmentCreated, SchedulingAuditActions.Resources.Appointment, id));
        Assert.Equal("Confirmed", read.GetProperty("status").GetString());
        Assert.Equal("Carlos Peña", read.GetProperty("customerName").GetString());
        Assert.Equal("3151112233", read.GetProperty("customerPhone").GetString());
        Assert.Equal("Dra. Torres", read.GetProperty("resourceName").GetString());
        Assert.Equal("Limpieza", read.GetProperty("serviceName").GetString());
        Assert.Equal("Primera vez", read.GetProperty("note").GetString());
        Assert.Equal(NineOClock(scene.Day), read.GetProperty("startsAt").GetDateTimeOffset());
        Assert.Equal(NineOClock(scene.Day).AddMinutes(30), read.GetProperty("endsAt").GetDateTimeOffset());
        var history = read.GetProperty("history").EnumerateArray().ToArray();
        var entry = Assert.Single(history);
        Assert.Equal("Created", entry.GetProperty("action").GetString());
        Assert.Equal((1, 1), await CountAppointmentsAsync(world.Factory, scene.Resource));
    }

    [Fact]
    public async Task AnUnknownAppointmentIs404AndAnotherTenantsIs403()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;
        using var stranger = await NewTenantAsync(world.Factory, "Clínica Sur");

        var unknown = await scene.Tenant.Admin.GetAsync($"{scene.Tenant.Url}/appointments/{Guid.CreateVersion7()}", Ct);
        var crossTenant = await stranger.Admin.GetAsync($"{scene.Tenant.Url}/appointments/{Guid.CreateVersion7()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("scheduling.appointment.not_found", CodeOf(await JsonOfAsync(unknown)));
        Assert.Equal(HttpStatusCode.Forbidden, crossTenant.StatusCode);
    }

    [Fact]
    public async Task AnAppointmentOfAnotherResourceIs403ForAMemberWithoutReadAll()
    {
        var scene = await SceneAsync();
        using var owner = scene.Tenant;
        var booked = await BookedAsync(scene, NineOClock(scene.Day), scene.Resource);
        var (_, provider) = await CreateProviderAsync(scene.Tenant, "torres@example.com", "Torres");
        using var providerClient = provider;

        var response = await provider.GetAsync($"{scene.Tenant.Url}/appointments/{booked.GetProperty("id").GetGuid()}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("authorization.denied", CodeOf(await JsonOfAsync(response)));
    }
}
