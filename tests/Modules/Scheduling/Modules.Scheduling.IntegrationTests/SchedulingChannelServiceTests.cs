using System.Net.Http.Json;
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

    private static async Task<Guid> CreateServiceAsync(SchedulingTenant tenant, string name, bool bookableByChannel = true, int duration = 30) =>
        (await CreatedAsync(
                tenant.Admin,
                $"{tenant.Url}/services",
                new { name, durationMinutes = duration, bufferMinutes = 0, bookableByChannel }))
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
