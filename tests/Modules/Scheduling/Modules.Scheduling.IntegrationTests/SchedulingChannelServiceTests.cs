using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Application;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Scheduling.Application;
using Modules.Scheduling.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using static Modules.Scheduling.IntegrationTests.SchedulingApiHarness;

namespace Modules.Scheduling.IntegrationTests;

/// <summary>
/// <c>IChannelScheduling</c> (spec 2026-11-23 §6.3): el único camino sin sesión para operar la Agenda. Corre el servicio real, sin HTTP,
/// contra una base migrada: las lecturas que el agente de IA y el playground piden, con las horas en la zona del tenant y sólo los
/// servicios reservables por canal.
/// </summary>
public sealed class SchedulingChannelServiceTests(SchedulingApiWorld world) : IClassFixture<SchedulingApiWorld>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<T> WithServiceAsync<T>(Func<IChannelScheduling, Task<T>> action)
    {
        await using var scope = world.Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IChannelScheduling>());
    }

    private static async Task<Guid> CreateServiceAsync(SchedulingTenant tenant, string name, bool bookableByChannel = true, int duration = 30, int buffer = 0) =>
        (await CreatedAsync(
                tenant.Admin,
                $"{tenant.Url}/services",
                new { name, durationMinutes = duration, bufferMinutes = buffer, bookableByChannel }))
            .GetProperty("id").GetGuid();

    private static async Task CreateBlockAsync(
        SchedulingTenant tenant, DateOnly day, Guid resource, Guid service, bool allowClientChoice = false, string name = "Jornada") =>
        (await tenant.Admin.PostAsJsonAsync(
            $"{tenant.Url}/blocks",
            new
            {
                name,
                kind = "OneOff",
                oneOff = new { date = day, ranges = new[] { new { @from = 540, to = 720 } } },
                capacityPerResource = 1,
                allowClientChoice,
                resourceIds = new[] { resource },
                serviceIds = new[] { service },
            },
            Ct)).EnsureSuccessStatusCode();

    private async Task DisableSchedulingAsync(SchedulingTenant tenant)
    {
        await using var scope = world.Factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        await tenancy.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM tenancy.tenant_modules WHERE tenant_id = {tenant.Id} AND module_key = 'scheduling'", Ct);
    }

    private async Task SetTimeZoneAsync(SchedulingTenant tenant, string timeZone)
    {
        await using var scope = world.Factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        await tenancy.Database.ExecuteSqlInterpolatedAsync($"UPDATE tenancy.tenants SET time_zone = {timeZone} WHERE id = {tenant.Id}", Ct);
    }

    [Fact]
    public async Task ReadinessCountsOnlyChannelServicesOfferedByAnActiveBlock()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var offered = await CreateServiceAsync(tenant, "Limpieza");
        var notForChannels = await CreateServiceAsync(tenant, "Cirugía", bookableByChannel: false);
        await CreateServiceAsync(tenant, "Sin franja");
        await CreateBlockAsync(tenant, FutureMonday(), resource, offered);
        await CreateBlockAsync(tenant, FutureMonday(), resource, notForChannels, name: "Otra");

        var readiness = await WithServiceAsync(service => service.GetReadinessAsync(tenant.Id, Ct));

        Assert.Equal(new ChannelReadiness(ModuleEnabled: true, BookableServices: 1), readiness);
    }

    [Fact]
    public async Task ReadinessWithTheModuleOffIsNotEnabled()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        await DisableSchedulingAsync(tenant);

        var readiness = await WithServiceAsync(service => service.GetReadinessAsync(tenant.Id, Ct));

        Assert.Equal(new ChannelReadiness(ModuleEnabled: false, BookableServices: 0), readiness);
    }

    [Fact]
    public async Task ListsBookableServicesByNameWithTheCap()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        foreach (var name in new[] { "Carillas", "Ajuste", "Blanqueamiento" })
        {
            await CreateBlockAsync(tenant, FutureMonday(), resource, await CreateServiceAsync(tenant, name), name: name);
        }

        var list = await WithServiceAsync(service => service.ListBookableServicesAsync(tenant.Id, 2, Ct));

        Assert.Equal(["Ajuste", "Blanqueamiento"], list.Services.Select(item => item.Name));
        Assert.True(list.Truncated);
        Assert.Equal("America/Bogota", list.TimeZone);
        Assert.All(list.Services, item => Assert.Equal(30, item.DurationMinutes));
    }

    [Fact]
    public async Task SearchRejectsAServiceWithoutBookableByChannel()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var service = await CreateServiceAsync(tenant, "Cirugía", bookableByChannel: false);
        var day = FutureMonday();

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            WithServiceAsync(channel => channel.SearchAvailabilityAsync(tenant.Id, service, day, day, null, 50, Ct)));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "serviceId");
    }

    [Fact]
    public async Task SearchHidesResourcesWhereTheBlockDoesNotAllowChoice()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var service = await CreateServiceAsync(tenant, "Limpieza");
        var day = FutureMonday();
        await CreateBlockAsync(tenant, day, resource, service, allowClientChoice: false);

        var open = await WithServiceAsync(channel => channel.SearchAvailabilityAsync(tenant.Id, service, day, day, null, 50, Ct));
        var error = await Assert.ThrowsAsync<SchedulingDomainException>(() =>
            WithServiceAsync(channel => channel.SearchAvailabilityAsync(tenant.Id, service, day, day, resource, 50, Ct)));

        Assert.NotEmpty(open.Slots);
        Assert.All(open.Slots, slot => Assert.Empty(slot.Resources));
        Assert.Equal(SchedulingErrorCodes.ResourceNotChoosable, error.Code);
    }

    [Fact]
    public async Task SearchNamesTheResourcesWhereTheBlockAllowsChoice()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var service = await CreateServiceAsync(tenant, "Limpieza");
        var day = FutureMonday();
        await CreateBlockAsync(tenant, day, resource, service, allowClientChoice: true);

        var chosen = await WithServiceAsync(channel => channel.SearchAvailabilityAsync(tenant.Id, service, day, day, resource, 50, Ct));

        Assert.NotEmpty(chosen.Slots);
        Assert.All(chosen.Slots, slot => Assert.Equal([new ChannelSlotResource(resource, "Dra. Torres")], slot.Resources));
    }

    [Fact]
    public async Task SearchRejectsAnArchivedOrUnknownResource()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var service = await CreateServiceAsync(tenant, "Limpieza");
        var day = FutureMonday();

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            WithServiceAsync(channel => channel.SearchAvailabilityAsync(tenant.Id, service, day, day, Guid.CreateVersion7(), 50, Ct)));

        Assert.Equal(SchedulingErrorCodes.ResourceNotFound, error.Code);
    }

    [Fact]
    public async Task SearchReturnsLocalTimesInTheTenantZoneAndCapsTheList()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var service = await CreateServiceAsync(tenant, "Limpieza");
        var day = FutureMonday();
        await CreateBlockAsync(tenant, day, resource, service);

        var all = await WithServiceAsync(channel => channel.SearchAvailabilityAsync(tenant.Id, service, day, day, null, 50, Ct));
        var capped = await WithServiceAsync(channel => channel.SearchAvailabilityAsync(tenant.Id, service, day, day, null, 2, Ct));

        // America/Bogota is UTC-5 all year: 09:00 local is 14:00Z, and the offset must be the zone's, not UTC.
        Assert.Equal("America/Bogota", all.TimeZone);
        Assert.Equal(TimeSpan.FromHours(-5), all.Slots[0].StartsAt.Offset);
        Assert.Equal(new DateTime(day.Year, day.Month, day.Day, 9, 0, 0), all.Slots[0].StartsAt.DateTime);
        Assert.False(all.Truncated);
        Assert.Equal(2, capped.Slots.Count);
        Assert.True(capped.Truncated);
    }

    [Fact]
    public async Task UpcomingByCustomerSkipsCancelledPastAndOtherCustomers()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var location = await MainLocationIdAsync(tenant);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var service = await CreateServiceAsync(tenant, "Limpieza");
        var mine = Guid.CreateVersion7();
        var tomorrow = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1).AddHours(15), TimeSpan.Zero);
        var later = await SeedAppointmentAsync(
            world.Factory, tenant.IdText, location, resource, service, mine, tomorrow.AddDays(2), AppointmentStatus.Confirmed);
        var sooner = await SeedAppointmentAsync(
            world.Factory, tenant.IdText, location, resource, service, mine, tomorrow, AppointmentStatus.Pending);
        await SeedAppointmentAsync(world.Factory, tenant.IdText, location, resource, service, mine, tomorrow.AddDays(1), AppointmentStatus.Cancelled);
        await SeedAppointmentAsync(
            world.Factory, tenant.IdText, location, resource, service, mine, tomorrow.AddDays(-5), AppointmentStatus.Confirmed);
        await SeedAppointmentAsync(
            world.Factory, tenant.IdText, location, resource, service, Guid.CreateVersion7(), tomorrow.AddDays(3), AppointmentStatus.Confirmed);

        var list = await WithServiceAsync(channel => channel.ListCustomerAppointmentsAsync(tenant.Id, mine, 10, Ct));

        Assert.Equal([sooner, later], list.Appointments.Select(item => item.AppointmentId));
        Assert.False(list.Truncated);
        Assert.Equal("Pending", list.Appointments[0].Status);
        Assert.All(list.Appointments, item => Assert.Equal(("Limpieza", "Dra. Torres"), (item.ServiceName, item.ResourceName)));
        var capped = await WithServiceAsync(channel => channel.ListCustomerAppointmentsAsync(tenant.Id, mine, 1, Ct));
        Assert.Single(capped.Appointments);
        Assert.True(capped.Truncated);
    }

    [Fact]
    public async Task FindReturnsNullForAnotherCustomersAppointment()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var location = await MainLocationIdAsync(tenant);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var service = await CreateServiceAsync(tenant, "Limpieza");
        var owner = Guid.CreateVersion7();
        var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(4).AddHours(15), TimeSpan.Zero);
        var cancelled = await SeedAppointmentAsync(world.Factory, tenant.IdText, location, resource, service, owner, start, AppointmentStatus.Cancelled);

        var theirs = await WithServiceAsync(channel => channel.FindCustomerAppointmentAsync(tenant.Id, owner, cancelled, Ct));
        var stranger = await WithServiceAsync(channel => channel.FindCustomerAppointmentAsync(tenant.Id, Guid.CreateVersion7(), cancelled, Ct));
        var unknown = await WithServiceAsync(channel => channel.FindCustomerAppointmentAsync(tenant.Id, owner, Guid.CreateVersion7(), Ct));

        Assert.Equal(("Cancelled", false, false), (theirs!.Status, theirs.CanChangeByChat, theirs.CanRescheduleByChat));
        Assert.Null(stranger);
        Assert.Null(unknown);
    }

    [Fact]
    public async Task CanRescheduleByChatIsFalseForANonChannelService()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var location = await MainLocationIdAsync(tenant);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var channelService = await CreateServiceAsync(tenant, "Limpieza");
        var backOfficeOnly = await CreateServiceAsync(tenant, "Cirugía", bookableByChannel: false);
        var customer = Guid.CreateVersion7();
        var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(10).AddHours(15), TimeSpan.Zero);
        var movable = await SeedAppointmentAsync(world.Factory, tenant.IdText, location, resource, channelService, customer, start, AppointmentStatus.Confirmed);
        var fixedByTheOffice = await SeedAppointmentAsync(
            world.Factory, tenant.IdText, location, resource, backOfficeOnly, customer, start.AddDays(1), AppointmentStatus.Confirmed);

        var first = await WithServiceAsync(channel => channel.FindCustomerAppointmentAsync(tenant.Id, customer, movable, Ct));
        var second = await WithServiceAsync(channel => channel.FindCustomerAppointmentAsync(tenant.Id, customer, fixedByTheOffice, Ct));

        // Cancelling does not need bookableByChannel (D-2), moving does (D-15). The default cutoff is 24 h, so both are 10 days away.
        Assert.Equal((true, true), (first!.CanChangeByChat, first.CanRescheduleByChat));
        Assert.Equal((true, false), (second!.CanChangeByChat, second.CanRescheduleByChat));
        Assert.Equal(start.AddHours(-24), first.ChangeByChatUntil);
        Assert.Equal(TimeSpan.FromHours(-5), first.ChangeByChatUntil.Offset);
    }

    [Fact]
    public async Task AnAppointmentInsideTheCutoffCannotBeChangedByChat()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var location = await MainLocationIdAsync(tenant);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var service = await CreateServiceAsync(tenant, "Limpieza");
        var customer = Guid.CreateVersion7();
        var soon = await SeedAppointmentAsync(
            world.Factory, tenant.IdText, location, resource, service, customer, DateTimeOffset.UtcNow.AddHours(3), AppointmentStatus.Confirmed);

        var found = await WithServiceAsync(channel => channel.FindCustomerAppointmentAsync(tenant.Id, customer, soon, Ct));

        Assert.Equal((false, false), (found!.CanChangeByChat, found.CanRescheduleByChat));
    }

    [Fact]
    public async Task CheckStartOfANonexistentLocalTimeIsOutsideRules()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        await SetTimeZoneAsync(tenant, "America/New_York");
        var service = await CreateServiceAsync(tenant, "Limpieza");

        // 2027-03-14 02:30 does not exist in New York: the clocks jump from 02:00 to 03:00.
        var error = await Assert.ThrowsAsync<SchedulingDomainException>(() =>
            WithServiceAsync(channel => channel.CheckStartAsync(tenant.Id, service, new DateTime(2027, 3, 14, 2, 30, 0), null, Ct)));

        Assert.Equal(SchedulingErrorCodes.OutsideRules, error.Code);
    }

    [Fact]
    public async Task CheckStartDescribesWhatTheBookingWouldDoWithoutWriting()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var service = await CreateServiceAsync(tenant, "Limpieza");
        var day = FutureMonday();
        await CreateBlockAsync(tenant, day, resource, service);

        var check = await WithServiceAsync(channel => channel.CheckStartAsync(tenant.Id, service, day.ToDateTime(new TimeOnly(9, 0)), null, Ct));

        Assert.Equal(("Confirmed", "Limpieza", "Dra. Torres"), (check.Status, check.ServiceName, check.ResourceName));
        Assert.Equal(TimeSpan.FromHours(-5), check.StartsAt.Offset);
        Assert.Equal((0, 0), await CountAppointmentsAsync(world.Factory, resource));
    }

    [Fact]
    public async Task EveryReadWithTheModuleOffIsForbidden()
    {
        using var tenant = await NewTenantAsync(world.Factory);
        await DisableSchedulingAsync(tenant);
        var day = FutureMonday();
        var id = Guid.CreateVersion7();

        var calls = new Func<IChannelScheduling, Task>[]
        {
            channel => channel.ListBookableServicesAsync(tenant.Id, 10, Ct),
            channel => channel.SearchAvailabilityAsync(tenant.Id, id, day, day, null, 10, Ct),
            channel => channel.ListCustomerAppointmentsAsync(tenant.Id, id, 10, Ct),
            channel => channel.FindCustomerAppointmentAsync(tenant.Id, id, id, Ct),
            channel => channel.CheckStartAsync(tenant.Id, id, day.ToDateTime(new TimeOnly(9, 0)), null, Ct),
            channel => channel.CheckRescheduleAsync(tenant.Id, id, id, day.ToDateTime(new TimeOnly(9, 0)), null, Ct),
        };
        foreach (var call in calls)
        {
            var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => WithServiceAsync(async channel =>
            {
                await call(channel);
                return 0;
            }));
            Assert.Equal("tenancy.module_not_enabled", error.Code);
        }
    }

    // --- Writes (Task 5) ---

    private sealed record BookScene(SchedulingTenant Tenant, Guid Resource, Guid SecondResource, Guid Service, Guid Customer, DateOnly Day, Guid Agent);

    private async Task<BookScene> BookSceneAsync(bool allowClientChoice = true, bool bookableByChannel = true, int duration = 30, int buffer = 0)
    {
        var tenant = await NewTenantAsync(world.Factory);
        var resource = await CreateResourceAsync(tenant, "Dra. Torres");
        var second = await CreateResourceAsync(tenant, "Dr. Pardo");
        var service = await CreateServiceAsync(tenant, "Limpieza", bookableByChannel, duration, buffer);
        var day = FutureMonday();
        (await tenant.Admin.PostAsJsonAsync(
            $"{tenant.Url}/blocks",
            new
            {
                name = "Jornada",
                kind = "OneOff",
                oneOff = new { date = day, ranges = new[] { new { @from = 540, to = 720 } } },
                capacityPerResource = 1,
                allowClientChoice,
                resourceIds = new[] { resource, second },
                serviceIds = new[] { service },
            },
            Ct)).EnsureSuccessStatusCode();
        return new BookScene(tenant, resource, second, service, await CreateCustomerAsync(tenant), day, Guid.CreateVersion7());
    }

    private static DateTime At(BookScene scene, int hour, int minute = 0) => scene.Day.ToDateTime(new TimeOnly(hour, minute));

    private static ChannelBooking Booking(
        BookScene scene, DateTime localStart, string key, Guid? resource = null, Guid? customer = null, Guid? conversation = null) =>
        new(scene.Tenant.Id, scene.Agent, customer ?? scene.Customer, conversation, scene.Service, localStart, resource, "Primera vez", key);

    private static async Task SetRulesAsync(SchedulingTenant tenant, string mode = "Automatic", int cutoffHours = 24)
    {
        var current = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/settings");
        (await PutAsync(
            tenant.Admin,
            $"{tenant.Url}/settings",
            new
            {
                confirmationMode = mode,
                minNoticeMinutes = 120,
                horizonDays = 30,
                clientCancelCutoffHours = cutoffHours,
                reminderEnabled = false,
                reminderOffsetHours = 24,
            },
            VersionOf(current))).EnsureSuccessStatusCode();
    }

    private static Task<JsonElement> ReadAppointmentAsync(BookScene scene, Guid id) =>
        GetJsonAsync(scene.Tenant.Admin, $"{scene.Tenant.Url}/appointments/{id}");

    private async Task<int> AgentAuditAsync(BookScene scene, string action, Guid appointmentId)
    {
        await using var scope = world.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Modules.Scheduling.Infrastructure.Persistence.SchedulingDbContext>();
        var tenant = scene.Tenant.Id;
        var agent = scene.Agent;
        var id = appointmentId.ToString();
        return await db.Database
            .SqlQuery<int>(
                $"""
                SELECT count(*)::int AS "Value" FROM audit.entries
                WHERE tenant_id = {tenant} AND action = {action} AND resource_id = {id}
                  AND actor_type = 'AiAgent' AND actor_id = {agent} AND source = 'scheduling' AND outcome = 'success'
                """)
            .SingleAsync(Ct);
    }

    [Theory]
    [InlineData("Automatic", "Confirmed")]
    [InlineData("Manual", "Pending")]
    public async Task BookFollowsConfirmationModeAsAChannelAppointment(string mode, string status)
    {
        var scene = await BookSceneAsync();
        using var owner = scene.Tenant;
        await SetRulesAsync(scene.Tenant, mode);
        var conversation = Guid.CreateVersion7();

        var result = await WithServiceAsync(channel => channel.BookAsync(Booking(scene, At(scene, 9), "key-1", conversation: conversation), Ct));

        Assert.False(result.AlreadyDone);
        Assert.Equal(status, result.Appointment.Status);
        var stored = await ReadAppointmentAsync(scene, result.Appointment.AppointmentId);
        Assert.Equal("Channel", stored.GetProperty("source").GetString());
        Assert.Equal(conversation, stored.GetProperty("conversationId").GetGuid());
        var created = stored.GetProperty("history")[0];
        Assert.True(created.GetProperty("byChannel").GetBoolean());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("actorName").ValueKind);
        // The time travels back in the tenant zone: 09:00 local, not 14:00Z.
        Assert.Equal((TimeSpan.FromHours(-5), 9), (result.Appointment.StartsAt.Offset, result.Appointment.StartsAt.Hour));
    }

    [Fact]
    public async Task BookAuditsAsTheAgent()
    {
        var scene = await BookSceneAsync();
        using var owner = scene.Tenant;

        var result = await WithServiceAsync(channel => channel.BookAsync(Booking(scene, At(scene, 9), "key-audit"), Ct));

        Assert.Equal(1, await AgentAuditAsync(scene, SchedulingAuditActions.AppointmentCreated, result.Appointment.AppointmentId));
        // The one entry there is belongs to the agent: nothing was written under a person's subject.
        Assert.Equal(1, await CountAuditEntriesAsync(
            world.Factory, scene.Tenant.IdText, SchedulingAuditActions.AppointmentCreated, SchedulingAuditActions.Resources.Appointment,
            result.Appointment.AppointmentId));
    }

    [Fact]
    public async Task SameKeyInParallelBooksOnceAndBothDescribeIt()
    {
        var scene = await BookSceneAsync();
        using var owner = scene.Tenant;

        // Two different resources: the resource lock does not serialize them, only the unique key does.
        var results = await Task.WhenAll(
            WithServiceAsync(channel => channel.BookAsync(Booking(scene, At(scene, 9), "same-key", scene.Resource), Ct)),
            WithServiceAsync(channel => channel.BookAsync(Booking(scene, At(scene, 9), "same-key", scene.SecondResource), Ct)));

        Assert.Equal(1, results.Count(result => !result.AlreadyDone));
        Assert.Equal(1, results.Count(result => result.AlreadyDone));
        Assert.Single(results.Select(result => result.Appointment.AppointmentId).Distinct());
        var first = (await CountAppointmentsAsync(world.Factory, scene.Resource)).Appointments;
        var second = (await CountAppointmentsAsync(world.Factory, scene.SecondResource)).Appointments;
        Assert.Equal(1, first + second);
    }

    [Fact]
    public async Task RepeatingTheKeyReturnsTheStoredAppointmentEvenWithOtherArguments()
    {
        var scene = await BookSceneAsync();
        using var owner = scene.Tenant;
        var booked = await WithServiceAsync(channel => channel.BookAsync(Booking(scene, At(scene, 9), "repeat-key"), Ct));

        var repeated = await WithServiceAsync(channel => channel.BookAsync(Booking(scene, At(scene, 10), "repeat-key"), Ct));

        Assert.True(repeated.AlreadyDone);
        Assert.Equal(booked.Appointment.AppointmentId, repeated.Appointment.AppointmentId);
        Assert.Equal(booked.Appointment.StartsAt, repeated.Appointment.StartsAt);
    }

    [Fact]
    public async Task BookOfANonChannelServiceIsOutsideRules()
    {
        var scene = await BookSceneAsync(bookableByChannel: false);
        using var owner = scene.Tenant;

        var error = await Assert.ThrowsAsync<SchedulingDomainException>(() =>
            WithServiceAsync(channel => channel.BookAsync(Booking(scene, At(scene, 9), "key-non-channel"), Ct)));

        Assert.Equal(SchedulingErrorCodes.OutsideRules, error.Code);
    }

    [Fact]
    public async Task BookOnAResourceThatCannotBeChosenIsResourceNotChoosable()
    {
        var scene = await BookSceneAsync(allowClientChoice: false);
        using var owner = scene.Tenant;

        var error = await Assert.ThrowsAsync<SchedulingDomainException>(() =>
            WithServiceAsync(channel => channel.BookAsync(Booking(scene, At(scene, 9), "key-no-choice", scene.Resource), Ct)));

        Assert.Equal(SchedulingErrorCodes.ResourceNotChoosable, error.Code);
        Assert.Equal(0, (await CountAppointmentsAsync(world.Factory, scene.Resource)).Appointments);
        // Without naming a resource the same start books: the agenda picks the first free one.
        var unnamed = await WithServiceAsync(channel => channel.BookAsync(Booking(scene, At(scene, 9), "key-no-choice-2"), Ct));
        Assert.False(unnamed.AlreadyDone);
    }

    [Fact]
    public async Task BookWithAnUnknownOrInactiveCustomerFailsOnCustomerId()
    {
        var scene = await BookSceneAsync();
        using var owner = scene.Tenant;

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            WithServiceAsync(channel => channel.BookAsync(Booking(scene, At(scene, 9), "key-customer", customer: Guid.CreateVersion7()), Ct)));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "customerId");
    }

    [Fact]
    public async Task AnotherCustomersAppointmentIsNotFound()
    {
        var scene = await BookSceneAsync();
        using var owner = scene.Tenant;
        var booked = await WithServiceAsync(channel => channel.BookAsync(Booking(scene, At(scene, 9), "key-owner"), Ct));
        var id = booked.Appointment.AppointmentId;
        var stranger = await CreateCustomerAsync(scene.Tenant, "Otra Persona", "3009998888");
        var before = await ReadAppointmentAsync(scene, id);

        var cancel = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            WithServiceAsync(channel => channel.CancelAsync(scene.Tenant.Id, scene.Agent, stranger, id, null, Ct)));
        var move = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            WithServiceAsync(channel => channel.RescheduleAsync(scene.Tenant.Id, scene.Agent, stranger, id, At(scene, 10), null, Ct)));

        var check = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            WithServiceAsync(channel => channel.CheckRescheduleAsync(scene.Tenant.Id, stranger, id, At(scene, 10), null, Ct)));

        Assert.Equal(
            (SchedulingErrorCodes.AppointmentNotFound, SchedulingErrorCodes.AppointmentNotFound, SchedulingErrorCodes.AppointmentNotFound),
            (cancel.Code, move.Code, check.Code));
        var after = await ReadAppointmentAsync(scene, id);
        Assert.Equal(before.GetProperty("version").GetInt64(), after.GetProperty("version").GetInt64());
        Assert.Equal("Confirmed", after.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CancelInsideTheCutoffIsInvalidTransition()
    {
        var scene = await BookSceneAsync();
        using var owner = scene.Tenant;
        var booked = await WithServiceAsync(channel => channel.BookAsync(Booking(scene, At(scene, 9), "key-cutoff"), Ct));
        await SetRulesAsync(scene.Tenant, cutoffHours: 720);

        var error = await Assert.ThrowsAsync<SchedulingDomainException>(() =>
            WithServiceAsync(channel => channel.CancelAsync(scene.Tenant.Id, scene.Agent, scene.Customer, booked.Appointment.AppointmentId, null, Ct)));

        Assert.Equal(SchedulingErrorCodes.InvalidTransition, error.Code);
        Assert.Equal("Confirmed", (await ReadAppointmentAsync(scene, booked.Appointment.AppointmentId)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task CancellingTwiceIsAlreadyDone()
    {
        var scene = await BookSceneAsync();
        using var owner = scene.Tenant;
        var booked = await WithServiceAsync(channel => channel.BookAsync(Booking(scene, At(scene, 9), "key-cancel"), Ct));
        var id = booked.Appointment.AppointmentId;

        var first = await WithServiceAsync(channel => channel.CancelAsync(scene.Tenant.Id, scene.Agent, scene.Customer, id, "No puedo ir", Ct));
        var versionAfterFirst = (await ReadAppointmentAsync(scene, id)).GetProperty("version").GetInt64();
        var second = await WithServiceAsync(channel => channel.CancelAsync(scene.Tenant.Id, scene.Agent, scene.Customer, id, "No puedo ir", Ct));

        Assert.False(first.AlreadyDone);
        Assert.True(second.AlreadyDone);
        Assert.Equal("Cancelled", second.Appointment.Status);
        var stored = await ReadAppointmentAsync(scene, id);
        Assert.Equal(versionAfterFirst, stored.GetProperty("version").GetInt64());
        Assert.True(stored.GetProperty("history").EnumerateArray().Last().GetProperty("byChannel").GetBoolean());
        Assert.Equal(1, await AgentAuditAsync(scene, SchedulingAuditActions.AppointmentCancelled, id));
    }

    // Block 1 review: the status check runs before the lock, so a write that lands in between makes the save lose its version (412 in the
    // core). The agent must read what really happened: its own goal already reached is success (D-12), anything else is "changed".
    // The stale copy is tracked first, as the real race leaves it, so the core saves with an old version.
    private async Task<(AsyncServiceScope Scope, Guid Id)> StaleCopyAsync(BookScene scene, string key)
    {
        var booked = await WithServiceAsync(channel => channel.BookAsync(Booking(scene, At(scene, 9), key), Ct));
        var id = booked.Appointment.AppointmentId;
        var scope = world.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IAppointmentRepository>().FindAsync(scene.Tenant.Id, new AppointmentId(id), Ct);
        return (scope, id);
    }

    private static async Task HumanCancelsAsync(BookScene scene, Guid id)
    {
        var current = await ReadAppointmentAsync(scene, id);
        (await PostAsync(scene.Tenant.Admin, $"{scene.Tenant.Url}/appointments/{id}/cancel", new { }, VersionOf(current))).EnsureSuccessStatusCode();
    }

    private static async Task HumanMovesAsync(BookScene scene, Guid id, int hour)
    {
        var current = await ReadAppointmentAsync(scene, id);
        var startsAt = new DateTimeOffset(At(scene, hour), TimeSpan.FromHours(-5));
        (await PutAsync(scene.Tenant.Admin, $"{scene.Tenant.Url}/appointments/{id}/reschedule", new { startsAt }, VersionOf(current))).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ACancelThatLostTheRaceToAnotherCancelIsAlreadyDone()
    {
        var scene = await BookSceneAsync();
        using var owner = scene.Tenant;
        var (scope, id) = await StaleCopyAsync(scene, "race-cancel");
        await using var _ = scope;
        await HumanCancelsAsync(scene, id);

        var result = await scope.ServiceProvider.GetRequiredService<IChannelScheduling>()
            .CancelAsync(scene.Tenant.Id, scene.Agent, scene.Customer, id, null, Ct);

        Assert.True(result.AlreadyDone);
        Assert.Equal("Cancelled", result.Appointment.Status);
        Assert.Equal(0, await AgentAuditAsync(scene, SchedulingAuditActions.AppointmentCancelled, id));
    }

    [Fact]
    public async Task ARescheduleThatLostTheRaceToTheSameMoveIsAlreadyDone()
    {
        var scene = await BookSceneAsync();
        using var owner = scene.Tenant;
        var (scope, id) = await StaleCopyAsync(scene, "race-same-move");
        await using var _ = scope;
        await HumanMovesAsync(scene, id, 10);

        var result = await scope.ServiceProvider.GetRequiredService<IChannelScheduling>()
            .RescheduleAsync(scene.Tenant.Id, scene.Agent, scene.Customer, id, At(scene, 10), null, Ct);

        Assert.True(result.AlreadyDone);
        Assert.Equal(10, result.Appointment.StartsAt.Hour);
    }

    [Fact]
    public async Task ARescheduleAfterAnotherMoveIsAConcurrencyConflict()
    {
        var scene = await BookSceneAsync();
        using var owner = scene.Tenant;
        var (scope, id) = await StaleCopyAsync(scene, "race-other-move");
        await using var _ = scope;
        await HumanMovesAsync(scene, id, 11);

        // Block 2 review: the cutoff and the same-location filter were decided from the pre-lock read (9:00). Once the lock shows a different
        // start the decision is stale, so the caller re-reads instead of writing over the other move.
        await Assert.ThrowsAsync<RequestConcurrencyException>(() => scope.ServiceProvider.GetRequiredService<IChannelScheduling>()
            .RescheduleAsync(scene.Tenant.Id, scene.Agent, scene.Customer, id, At(scene, 10), null, Ct));

        var stored = await ReadAppointmentAsync(scene, id);
        Assert.Equal("Confirmed", stored.GetProperty("status").GetString());
        Assert.Equal(11, stored.GetProperty("startsAt").GetDateTimeOffset().ToOffset(TimeSpan.FromHours(-5)).Hour);
        Assert.Equal(1, (await CountAppointmentsAsync(world.Factory, scene.Resource)).Appointments);
    }

    [Fact]
    public async Task ACancelAfterAMoveIsAConcurrencyConflict()
    {
        var scene = await BookSceneAsync();
        using var owner = scene.Tenant;
        var (scope, id) = await StaleCopyAsync(scene, "race-cancel-vs-move");
        await using var _ = scope;
        await HumanMovesAsync(scene, id, 10);

        // Block 2 review: the cutoff used the old start and the lock the old resource; after the move both are stale, so nothing is written.
        await Assert.ThrowsAsync<RequestConcurrencyException>(() => scope.ServiceProvider.GetRequiredService<IChannelScheduling>()
            .CancelAsync(scene.Tenant.Id, scene.Agent, scene.Customer, id, null, Ct));

        Assert.Equal("Confirmed", (await ReadAppointmentAsync(scene, id)).GetProperty("status").GetString());
    }

    // Final review W2: the job shares one DbContext across its writes. A write that fails must not leave its entities tracked, or the
    // next write of the same job flushes them again.
    private static int TrackedEntries(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<Modules.Scheduling.Infrastructure.Persistence.SchedulingDbContext>().ChangeTracker.Entries().Count();

    [Fact]
    public async Task AFailedChannelCancelLeavesNothingTracked()
    {
        var scene = await BookSceneAsync();
        using var owner = scene.Tenant;
        var (scope, id) = await StaleCopyAsync(scene, "discard-cancel");
        await using var _ = scope;
        await HumanMovesAsync(scene, id, 10);

        await Assert.ThrowsAsync<RequestConcurrencyException>(() => scope.ServiceProvider.GetRequiredService<IChannelScheduling>()
            .CancelAsync(scene.Tenant.Id, scene.Agent, scene.Customer, id, null, Ct));

        Assert.Equal(0, TrackedEntries(scope));
    }

    [Fact]
    public async Task AFailedChannelRescheduleLeavesNothingTracked()
    {
        var scene = await BookSceneAsync();
        using var owner = scene.Tenant;
        var (scope, id) = await StaleCopyAsync(scene, "discard-move");
        await using var _ = scope;
        await HumanMovesAsync(scene, id, 11);

        await Assert.ThrowsAsync<RequestConcurrencyException>(() => scope.ServiceProvider.GetRequiredService<IChannelScheduling>()
            .RescheduleAsync(scene.Tenant.Id, scene.Agent, scene.Customer, id, At(scene, 10), null, Ct));

        Assert.Equal(0, TrackedEntries(scope));
    }

    [Fact]
    public async Task AFailedChannelBookLeavesNothingTracked()
    {
        var scene = await BookSceneAsync();
        using var owner = scene.Tenant;
        await using var scope = world.Factory.Services.CreateAsyncScope();
        var channel = scope.ServiceProvider.GetRequiredService<IChannelScheduling>();
        await channel.BookAsync(Booking(scene, At(scene, 9), "discard-book-1", scene.Resource), Ct);

        // The same slot of the same resource under another key: the second booking fails, and the first one's entities are not its business.
        await Assert.ThrowsAnyAsync<Exception>(() => channel.BookAsync(Booking(scene, At(scene, 9), "discard-book-2", scene.Resource), Ct));

        Assert.Equal(0, TrackedEntries(scope));
    }

    // Final review W3: the human path passes the If-Match version down. A write that lands between the load and the lock without moving the
    // appointment (a confirmation) must still answer 412; the channel path has no If-Match and tolerates it.
    private async Task<(AsyncServiceScope Scope, Guid Id, long LoadedVersion)> LoadedPendingAsync(BookScene scene, string key)
    {
        await SetRulesAsync(scene.Tenant, "Manual");
        var booked = await WithServiceAsync(channel => channel.BookAsync(Booking(scene, At(scene, 9), key), Ct));
        var id = booked.Appointment.AppointmentId;
        var scope = world.Factory.Services.CreateAsyncScope();
        var loaded = await scope.ServiceProvider.GetRequiredService<IAppointmentRepository>().FindAsync(scene.Tenant.Id, new AppointmentId(id), Ct);
        var current = await ReadAppointmentAsync(scene, id);
        (await PostAsync(scene.Tenant.Admin, $"{scene.Tenant.Url}/appointments/{id}/confirm", null, VersionOf(current))).EnsureSuccessStatusCode();
        return (scope, id, loaded!.Version);
    }

    [Fact]
    public async Task AHumanCancelWithAStaleVersionAfterAConfirmationIsAConcurrencyConflict()
    {
        var scene = await BookSceneAsync();
        using var owner = scene.Tenant;
        var (scope, id, loadedVersion) = await LoadedPendingAsync(scene, "stale-human-cancel");
        await using var _ = scope;
        var appointment = (await scope.ServiceProvider.GetRequiredService<IAppointmentRepository>().FindAsync(scene.Tenant.Id, new AppointmentId(id), Ct))!;

        await Assert.ThrowsAsync<RequestConcurrencyException>(() => scope.ServiceProvider.GetRequiredService<AppointmentCancellation>()
            .CancelAsync(scene.Tenant.Id, appointment, null, byClient: false, SchedulingActor.Human(Guid.CreateVersion7(), null), loadedVersion, Ct));

        Assert.Equal("Confirmed", (await ReadAppointmentAsync(scene, id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task AChannelCancelToleratesAConfirmationBetweenTheReadAndTheLock()
    {
        var scene = await BookSceneAsync();
        using var owner = scene.Tenant;
        var (scope, id, _) = await LoadedPendingAsync(scene, "stale-channel-cancel");
        await using var __ = scope;

        var result = await scope.ServiceProvider.GetRequiredService<IChannelScheduling>()
            .CancelAsync(scene.Tenant.Id, scene.Agent, scene.Customer, id, null, Ct);

        Assert.False(result.AlreadyDone);
        Assert.Equal("Cancelled", (await ReadAppointmentAsync(scene, id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task AHumanRescheduleWithAStaleVersionAfterAConfirmationIsAConcurrencyConflict()
    {
        var scene = await BookSceneAsync();
        using var owner = scene.Tenant;
        var (scope, id, loadedVersion) = await LoadedPendingAsync(scene, "stale-human-move");
        await using var _ = scope;
        var appointment = (await scope.ServiceProvider.GetRequiredService<IAppointmentRepository>().FindAsync(scene.Tenant.Id, new AppointmentId(id), Ct))!;

        await Assert.ThrowsAsync<RequestConcurrencyException>(() => scope.ServiceProvider.GetRequiredService<AppointmentRescheduling>()
            .RescheduleAsync(
                scene.Tenant.Id, appointment, ResourceScope.All, null, new DateTimeOffset(At(scene, 10), TimeSpan.FromHours(-5)),
                SchedulingActor.Human(Guid.CreateVersion7(), null), byClient: false, requireClientChoice: false, loadedVersion, Ct));
    }

    // Block 1 review: a replay answers with the stored appointment, so it must be this customer's. Another customer's key is a conflict that
    // names nothing of the stored one.
    [Fact]
    public async Task ReplayingAnotherCustomersKeyIsRejectedWithoutDescribingIt()
    {
        var scene = await BookSceneAsync();
        using var owner = scene.Tenant;
        await WithServiceAsync(channel => channel.BookAsync(Booking(scene, At(scene, 9), "shared-key"), Ct));
        var stranger = await CreateCustomerAsync(scene.Tenant, "Otra Persona", "3009998888");

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            WithServiceAsync(channel => channel.BookAsync(Booking(scene, At(scene, 10), "shared-key", customer: stranger), Ct)));

        Assert.Contains(error.Errors, failure => failure.PropertyName == "clientRequestId");
        Assert.DoesNotContain("Limpieza", error.Message, StringComparison.Ordinal);
        var first = (await CountAppointmentsAsync(world.Factory, scene.Resource)).Appointments;
        var second = (await CountAppointmentsAsync(world.Factory, scene.SecondResource)).Appointments;
        Assert.Equal(1, first + second);
    }

    [Fact]
    public async Task TheSameKeyRacedByTwoCustomersFailsTheLoser()
    {
        var scene = await BookSceneAsync();
        using var owner = scene.Tenant;
        var stranger = await CreateCustomerAsync(scene.Tenant, "Otra Persona", "3009998888");

        var outcomes = await Task.WhenAll(
            Capture(WithServiceAsync(channel => channel.BookAsync(Booking(scene, At(scene, 9), "race-key", scene.Resource), Ct))),
            Capture(WithServiceAsync(channel => channel.BookAsync(Booking(scene, At(scene, 9), "race-key", scene.SecondResource, stranger), Ct))));

        Assert.Equal(1, outcomes.Count(outcome => outcome.Error is null));
        Assert.Single(outcomes, outcome => outcome.Error is ValidationException);
    }

    private static async Task<(ChannelWriteResult? Result, Exception? Error)> Capture(Task<ChannelWriteResult> task)
    {
        try
        {
            return (await task, null);
        }
        catch (Exception error)
        {
            return (null, error);
        }
    }

    [Fact]
    public async Task RescheduleInsideTheCutoffIsInvalidTransition()
    {
        var scene = await BookSceneAsync();
        using var owner = scene.Tenant;
        var booked = await WithServiceAsync(channel => channel.BookAsync(Booking(scene, At(scene, 9), "key-move-cutoff"), Ct));
        await SetRulesAsync(scene.Tenant, cutoffHours: 720);

        var error = await Assert.ThrowsAsync<SchedulingDomainException>(() =>
            WithServiceAsync(channel => channel.RescheduleAsync(
                scene.Tenant.Id, scene.Agent, scene.Customer, booked.Appointment.AppointmentId, At(scene, 10), null, Ct)));

        Assert.Equal(SchedulingErrorCodes.InvalidTransition, error.Code);
        Assert.Contains("720 horas", error.Message, StringComparison.Ordinal);
        Assert.Equal(booked.Appointment.StartsAt, (await WithServiceAsync(channel => channel.FindCustomerAppointmentAsync(
            scene.Tenant.Id, scene.Customer, booked.Appointment.AppointmentId, Ct)))!.StartsAt);
    }

    [Fact]
    public async Task RescheduleOfANonChannelServiceIsInvalidTransition()
    {
        var scene = await BookSceneAsync(bookableByChannel: false);
        using var owner = scene.Tenant;
        // The back office booked it (source BackOffice, so the channel rule does not apply to it): only a person may move it afterwards.
        var response = await PostAsync(
            scene.Tenant.Admin,
            $"{scene.Tenant.Url}/appointments",
            BookBody(scene.Service, NineOClock(scene.Day), scene.Customer, scene.Resource),
            null);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        var id = (await JsonOfAsync(response)).GetProperty("id").GetGuid();

        var error = await Assert.ThrowsAsync<SchedulingDomainException>(() =>
            WithServiceAsync(channel => channel.RescheduleAsync(scene.Tenant.Id, scene.Agent, scene.Customer, id, At(scene, 10), null, Ct)));

        Assert.Equal(SchedulingErrorCodes.InvalidTransition, error.Code);
        Assert.Equal(AppointmentCancellationRules.NotMovableByChatMessage, error.Message);
    }

    [Fact]
    public async Task CheckRescheduleIgnoresTheSeatTheAppointmentHolds()
    {
        // With a margin, the range of the booked 09:00 (09:00-10:00) overlaps any start before 10:00: a booking preview of that start fails.
        var scene = await BookSceneAsync(duration: 30, buffer: 30);
        using var owner = scene.Tenant;
        var offered = await WithServiceAsync(channel => channel.SearchAvailabilityAsync(scene.Tenant.Id, scene.Service, scene.Day, scene.Day, scene.Resource, 50, Ct));
        var target = offered.Slots.Select(slot => slot.StartsAt).First(start => start.TimeOfDay > TimeSpan.FromHours(9) && start.TimeOfDay < TimeSpan.FromHours(10));
        var targetLocal = new DateTime(target.Year, target.Month, target.Day, target.Hour, target.Minute, 0);
        var booked = await WithServiceAsync(channel => channel.BookAsync(Booking(scene, At(scene, 9), "key-check-own-seat", scene.Resource), Ct));
        var id = booked.Appointment.AppointmentId;
        var before = (await ReadAppointmentAsync(scene, id)).GetProperty("version").GetInt64();

        // The guard: the scene does reproduce the error of the booking preview.
        var taken = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            WithServiceAsync(channel => channel.CheckStartAsync(scene.Tenant.Id, scene.Service, targetLocal, scene.Resource, Ct)));
        Assert.Equal(SchedulingErrorCodes.SlotTaken, taken.Code);

        var check = await WithServiceAsync(channel => channel.CheckRescheduleAsync(scene.Tenant.Id, scene.Customer, id, targetLocal, scene.Resource, Ct));

        Assert.False(check.AlreadyDone);
        Assert.Equal(targetLocal.TimeOfDay, check.Start.StartsAt.TimeOfDay);
        Assert.Equal("Confirmed", check.Start.Status);
        Assert.Equal("Dra. Torres", check.Start.ResourceName);
        Assert.Equal(before, (await ReadAppointmentAsync(scene, id)).GetProperty("version").GetInt64());

        // Parity: the real move does the same thing.
        var moved = await WithServiceAsync(channel => channel.RescheduleAsync(scene.Tenant.Id, scene.Agent, scene.Customer, id, targetLocal, scene.Resource, Ct));
        Assert.False(moved.AlreadyDone);
    }

    [Fact]
    public async Task CheckRescheduleToTheSameTimeIsAlreadyDone()
    {
        var scene = await BookSceneAsync();
        using var owner = scene.Tenant;
        var booked = await WithServiceAsync(channel => channel.BookAsync(Booking(scene, At(scene, 9), "key-check-same"), Ct));
        var id = booked.Appointment.AppointmentId;
        var before = (await ReadAppointmentAsync(scene, id)).GetProperty("version").GetInt64();

        var check = await WithServiceAsync(channel => channel.CheckRescheduleAsync(scene.Tenant.Id, scene.Customer, id, At(scene, 9), null, Ct));

        Assert.True(check.AlreadyDone);
        Assert.Equal(9, check.Start.StartsAt.Hour);
        Assert.Equal(before, (await ReadAppointmentAsync(scene, id)).GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task RescheduleToTheSameTimeIsAlreadyDone()
    {
        var scene = await BookSceneAsync();
        using var owner = scene.Tenant;
        var booked = await WithServiceAsync(channel => channel.BookAsync(Booking(scene, At(scene, 9), "key-same-time"), Ct));
        var id = booked.Appointment.AppointmentId;
        var before = (await ReadAppointmentAsync(scene, id)).GetProperty("version").GetInt64();

        var result = await WithServiceAsync(channel => channel.RescheduleAsync(scene.Tenant.Id, scene.Agent, scene.Customer, id, At(scene, 9), null, Ct));

        Assert.True(result.AlreadyDone);
        Assert.Equal(before, (await ReadAppointmentAsync(scene, id)).GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task AgentRescheduleMarksTheHistoryByChannel()
    {
        var scene = await BookSceneAsync();
        using var owner = scene.Tenant;
        // Born in the back office: the chat move must still be marked as the client's, not as the system's.
        var response = await PostAsync(
            scene.Tenant.Admin, $"{scene.Tenant.Url}/appointments", BookBody(scene.Service, NineOClock(scene.Day), scene.Customer, scene.Resource), null);
        var id = (await JsonOfAsync(response)).GetProperty("id").GetGuid();

        var result = await WithServiceAsync(channel => channel.RescheduleAsync(scene.Tenant.Id, scene.Agent, scene.Customer, id, At(scene, 10), null, Ct));

        Assert.False(result.AlreadyDone);
        Assert.Equal(10, result.Appointment.StartsAt.Hour);
        var history = (await ReadAppointmentAsync(scene, id)).GetProperty("history").EnumerateArray().ToArray();
        Assert.False(history[0].GetProperty("byChannel").GetBoolean());
        Assert.Equal(("Rescheduled", true), (history[^1].GetProperty("action").GetString(), history[^1].GetProperty("byChannel").GetBoolean()));
        Assert.Equal(1, await AgentAuditAsync(scene, SchedulingAuditActions.AppointmentRescheduled, id));
    }

    [Fact]
    public async Task WritesWithTheModuleOffAreForbidden()
    {
        var scene = await BookSceneAsync();
        using var owner = scene.Tenant;
        await DisableSchedulingAsync(scene.Tenant);
        var id = Guid.CreateVersion7();

        var calls = new Func<IChannelScheduling, Task>[]
        {
            channel => channel.BookAsync(Booking(scene, At(scene, 9), "key-off"), Ct),
            channel => channel.CancelAsync(scene.Tenant.Id, scene.Agent, scene.Customer, id, null, Ct),
            channel => channel.RescheduleAsync(scene.Tenant.Id, scene.Agent, scene.Customer, id, At(scene, 10), null, Ct),
        };
        foreach (var call in calls)
        {
            var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => WithServiceAsync(async channel =>
            {
                await call(channel);
                return 0;
            }));
            Assert.Equal("tenancy.module_not_enabled", error.Code);
        }
    }
}
