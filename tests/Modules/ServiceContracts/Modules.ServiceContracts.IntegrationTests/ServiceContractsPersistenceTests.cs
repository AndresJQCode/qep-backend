using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.ServiceContracts.Application;
using Modules.ServiceContracts.Domain;
using Modules.ServiceContracts.Infrastructure.Persistence;
using Npgsql;
using static Modules.ServiceContracts.IntegrationTests.ServiceContractsApiHarness;

namespace Modules.ServiceContracts.IntegrationTests;

/// <summary>
/// El esquema <c>billing</c> (spec 2026-10-29 §2): las unicidades que sostienen la deduplicación del
/// worker y los CHECK que la base impone aunque el dominio ya valide.
/// </summary>
public sealed class ServiceContractsPersistenceTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 13, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 29);

    [Fact]
    public async Task ASecondChargeOfTheSamePeriodIsRejected()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var service = NewService();
        var period = new DateOnly(2026, 11, 5);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
            db.Services.Add(service);
            db.Charges.Add(Charge.Generate(service, period, Now));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var again = factory.Services.CreateAsyncScope();
        var second = again.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
        // Otro vencimiento no lo salva: lo único es el período.
        second.Charges.Add(Charge.Generate(service, period, period.AddDays(9), false, Now));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => second.SaveChangesAsync(TestContext.Current.CancellationToken));
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("IX_charges_service_period", postgres.ConstraintName);
    }

    [Fact]
    public async Task TwoChargesOfAServiceMayShareTheDueDate()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var service = NewService();
        var due = new DateOnly(2026, 12, 20);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
            db.Services.Add(service);
            db.Charges.Add(Charge.Generate(service, new DateOnly(2026, 11, 5), due, false, Now));
            db.Charges.Add(Charge.Generate(service, new DateOnly(2026, 12, 5), due, true, Now));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var stored = await read.ServiceProvider.GetRequiredService<ServiceContractsDbContext>().Charges
            .AsNoTracking()
            .Where(charge => charge.ServiceId == service.Id)
            .OrderBy(charge => charge.PeriodDate)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [(new DateOnly(2026, 11, 5), due, false), (new DateOnly(2026, 12, 5), due, true)],
            stored.Select(charge => (charge.PeriodDate, charge.DueDate, charge.PriceUnreviewed)));
    }

    [Fact]
    public async Task OnlyOnePendingScheduleByService()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var service = NewService();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
            db.Services.Add(service);
            db.ServicePriceSchedules.Add(NewSchedule(service, 160000m));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var again = factory.Services.CreateAsyncScope())
        {
            var db = again.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
            db.ServicePriceSchedules.Add(NewSchedule(service, 170000m));

            var exception = await Assert.ThrowsAsync<DbUpdateException>(
                () => db.SaveChangesAsync(TestContext.Current.CancellationToken));
            var postgres = Assert.IsType<PostgresException>(exception.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
            Assert.Equal("IX_service_price_schedules_pending", postgres.ConstraintName);
        }

        // Cancelado el primero, cabe otro pendiente: el único es parcial.
        await using (var cancel = factory.Services.CreateAsyncScope())
        {
            var db = cancel.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
            var pending = await db.ServicePriceSchedules.SingleAsync(TestContext.Current.CancellationToken);
            pending.Cancel(service, Now);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.ServicePriceSchedules.Add(NewSchedule(service, 170000m));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var statuses = await read.ServiceProvider.GetRequiredService<ServiceContractsDbContext>().ServicePriceSchedules
            .AsNoTracking()
            .OrderBy(schedule => schedule.NewPrice)
            .Select(schedule => schedule.Status)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal([PriceScheduleStatus.Cancelled, PriceScheduleStatus.Pending], statuses);
    }

    [Fact]
    public async Task ThePriceScheduleRepositoryAnswersByServiceAndTenant()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var ct = TestContext.Current.CancellationToken;

        var ipc = NewService();
        ipc.InitializePriceReview(AdjustmentMode.Ipc, 2m, null);
        var manual = NewService();
        var confirmed = ServicePriceSchedule.ConfirmIpc(ipc, 2026, 5.2m, 160800m, "IPC 2026", Guid.CreateVersion7(), Now);
        var scheduled = NewSchedule(manual, 170000m);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
            db.Services.AddRange(ipc, manual);
            var repository = scope.ServiceProvider.GetRequiredService<IServicePriceScheduleRepository>();
            repository.Add(confirmed);
            repository.Add(scheduled);
            await db.SaveChangesAsync(ct);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var schedules = read.ServiceProvider.GetRequiredService<IServicePriceScheduleRepository>();

        var loaded = await schedules.FindPendingAsync(Tenant, ipc.Id, ct);
        Assert.NotNull(loaded);
        Assert.Equal(
            (confirmed.Id, confirmed.EffectiveFrom, 160800m, "COP", PriceScheduleSource.Ipc),
            (loaded.Id, loaded.EffectiveFrom, loaded.NewPrice, loaded.Currency, loaded.Source));
        Assert.Equal(
            ((int?)2026, (decimal?)5.2m, (decimal?)2m, "IPC 2026", confirmed.CreatedByMemberId, PriceScheduleStatus.Pending),
            (loaded.IpcYear, loaded.IpcRate, loaded.ExtraPoints, loaded.Reason, loaded.CreatedByMemberId, loaded.Status));
        Assert.Null(await schedules.FindPendingAsync(Guid.CreateVersion7(), ipc.Id, ct));
        Assert.Equal(2, (await schedules.ListPendingByServicesAsync(Tenant, [ipc.Id, manual.Id], ct)).Count);
        Assert.Empty(await schedules.ListPendingByServicesAsync(Guid.CreateVersion7(), [ipc.Id, manual.Id], ct));
        Assert.Equal(2, (await schedules.ListPendingByTenantAsync(Tenant, ct)).Count);
        Assert.Empty(await schedules.ListPendingByTenantAsync(Guid.CreateVersion7(), ct));
        Assert.Equal([2026], await schedules.ListPendingIpcYearsAsync(Tenant, ct));

        var db2 = read.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
        // Con otro tenant no se borra nada, aunque el id del contrato sea el correcto.
        await schedules.RemoveByServiceAsync(Guid.CreateVersion7(), ipc.Id, ct);
        await db2.SaveChangesAsync(ct);
        Assert.Equal(2, (await schedules.ListPendingByTenantAsync(Tenant, ct)).Count);

        await schedules.RemoveByServiceAsync(Tenant, ipc.Id, ct);
        await db2.SaveChangesAsync(ct);
        Assert.Equal(manual.Id, Assert.Single(await schedules.ListPendingByTenantAsync(Tenant, ct)).ServiceId);
        Assert.Empty(await schedules.ListPendingIpcYearsAsync(Tenant, ct));
    }

    [Fact]
    public async Task TheIpcRateRoundTripsAndRejectsOutOfRange()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var ct = TestContext.Current.CancellationToken;

        var author = Guid.CreateVersion7();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var rates = scope.ServiceProvider.GetRequiredService<IIpcRateRepository>();
            rates.Add(IpcRate.Create(Tenant, 2025, -1.5m, null, Now));
            rates.Add(IpcRate.Create(Tenant, 2026, 100m, author, Now));
            await scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>().SaveChangesAsync(ct);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var repository = read.ServiceProvider.GetRequiredService<IIpcRateRepository>();
        var listed = await repository.ListAsync(Tenant, ct);
        Assert.Equal([(2026, 100m), (2025, -1.5m)], listed.Select(rate => (rate.Year, rate.Rate)));
        var found = await repository.FindAsync(Tenant, 2026, ct);
        Assert.Equal((author, Now, 1), (found!.UpdatedByMemberId, found.UpdatedAt, found.Version));
        Assert.Null(await repository.FindAsync(Guid.CreateVersion7(), 2026, ct));

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => read.ServiceProvider.GetRequiredService<ServiceContractsDbContext>().Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE billing.ipc_rates SET rate = 100.01 WHERE tenant_id = {Tenant} AND year = 2026", ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("CK_ipc_rates_rate", exception.ConstraintName);
    }

    // Fuera de una transacción el candado se soltaría con su propia sentencia: se rechaza en vez de proteger nada.
    [Fact]
    public async Task TheIpcRateLocksNeedAnOpenTransaction()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var ct = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var rates = scope.ServiceProvider.GetRequiredService<IIpcRateRepository>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => rates.FindForShareAsync(Tenant, 2026, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rates.FindForUpdateAsync(Tenant, 2026, ct));
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IServiceContractsUnitOfWork>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        Assert.Null(await rates.FindForShareAsync(Tenant, 2026, ct));
        Assert.Null(await rates.FindForUpdateAsync(Tenant, 2026, ct));
    }

    [Fact]
    public async Task AServiceRoundTripsItsPriceReview()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var service = NewService();
        service.MarkGenerated(new DateOnly(2026, 12, 5));
        service.InitializePriceReview(AdjustmentMode.Ipc, 2.5m, new DateOnly(2027, 1, 5));
        ChargeGenerator.Generate(service, null, GenerationMode.Run, new DateOnly(2027, 1, 1), Now);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
            db.Services.Add(service);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var loaded = await read.ServiceProvider.GetRequiredService<ServiceContractsDbContext>().Services
            .AsNoTracking()
            .SingleAsync(value => value.Id == service.Id, TestContext.Current.CancellationToken);

        Assert.Equal(AdjustmentMode.Ipc, loaded.AdjustmentMode);
        Assert.Equal(2.5m, loaded.IpcExtraPoints);
        Assert.Equal(new DateOnly(2027, 1, 5), loaded.NextReviewDate);
        Assert.Equal(new DateOnly(2027, 1, 1), loaded.HeldSince);
    }

    // R-5: una acción del usuario que choca con la corrida diaria responde 412, como el token de versión.
    [Fact]
    public async Task APeriodAnotherWriterGeneratedSurfacesAsAConcurrencyConflict()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var service = NewService();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
            db.Services.Add(service);
            db.Charges.Add(Charge.Generate(service, new DateOnly(2026, 11, 5), Now));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var again = factory.Services.CreateAsyncScope();
        again.ServiceProvider.GetRequiredService<ServiceContractsDbContext>().Charges
            .Add(Charge.Generate(service, new DateOnly(2026, 11, 5), Now));

        var exception = await Assert.ThrowsAsync<RequestConcurrencyException>(
            () => again.ServiceProvider.GetRequiredService<IServiceContractsUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal("concurrency.conflict", exception.Code);
    }

    [Fact]
    public async Task ASecondPendingScheduleSurfacesAsAConcurrencyConflict()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var service = NewService();
        await using var first = factory.Services.CreateAsyncScope();
        await using var second = factory.Services.CreateAsyncScope();
        var db = first.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
        db.Services.Add(service);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ServicePriceSchedules.Add(NewSchedule(service, 160000m));
        second.ServiceProvider.GetRequiredService<ServiceContractsDbContext>().ServicePriceSchedules
            .Add(NewSchedule(service, 170000m));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<RequestConcurrencyException>(
            () => second.ServiceProvider.GetRequiredService<IServiceContractsUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal("concurrency.conflict", exception.Code);
    }

    [Fact]
    public async Task TwoWritersOfTheSameIpcRateNeverOverwriteEachOther()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var ct = TestContext.Current.CancellationToken;

        await using var first = factory.Services.CreateAsyncScope();
        await using var second = factory.Services.CreateAsyncScope();
        // Los dos vieron «no hay tasa»: pierde el segundo insert.
        first.ServiceProvider.GetRequiredService<IIpcRateRepository>().Add(IpcRate.Create(Tenant, 2026, 5.2m, null, Now));
        second.ServiceProvider.GetRequiredService<IIpcRateRepository>().Add(IpcRate.Create(Tenant, 2026, 5.3m, null, Now));
        await first.ServiceProvider.GetRequiredService<IServiceContractsUnitOfWork>().SaveChangesAsync(ct);

        var insert = await Assert.ThrowsAsync<ResourceConflictException>(
            () => second.ServiceProvider.GetRequiredService<IServiceContractsUnitOfWork>().SaveChangesAsync(ct));
        Assert.Equal("billing.concurrent_first_write", insert.Code);

        // Los dos cargaron la misma versión: pierde el segundo update.
        await using var third = factory.Services.CreateAsyncScope();
        await using var fourth = factory.Services.CreateAsyncScope();
        var loadedByThird = await third.ServiceProvider.GetRequiredService<IIpcRateRepository>().FindAsync(Tenant, 2026, ct);
        var loadedByFourth = await fourth.ServiceProvider.GetRequiredService<IIpcRateRepository>().FindAsync(Tenant, 2026, ct);
        loadedByThird!.Change(6m, null, Now);
        loadedByFourth!.Change(7m, null, Now);
        await third.ServiceProvider.GetRequiredService<IServiceContractsUnitOfWork>().SaveChangesAsync(ct);

        var update = await Assert.ThrowsAsync<RequestConcurrencyException>(
            () => fourth.ServiceProvider.GetRequiredService<IServiceContractsUnitOfWork>().SaveChangesAsync(ct));
        Assert.Equal("concurrency.conflict", update.Code);
    }

    private static ServicePriceSchedule NewSchedule(ServiceContract service, decimal price) =>
        ServicePriceSchedule.Schedule(service, null, price, new DateOnly(2027, 1, 5), "Más usuarios", null, Now);

    [Fact]
    public async Task ASecondMessageForTheSameCustomerAndDayIsRejected()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var customerId = Guid.CreateVersion7();
        var day = new DateOnly(2026, 10, 29);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
            db.CustomerMessages.Add(NewMessage(customerId, day));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var again = factory.Services.CreateAsyncScope();
        var second = again.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
        second.CustomerMessages.Add(NewMessage(customerId, day));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => second.SaveChangesAsync(TestContext.Current.CancellationToken));
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("IX_customer_messages_tenant_customer_day", postgres.ConstraintName);
    }

    [Fact]
    public async Task ASecondStepRowForTheSameChargeAndStepIsRejected()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var chargeId = Guid.CreateVersion7();
        var message = NewMessage(Guid.CreateVersion7(), new DateOnly(2026, 10, 29));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
            db.CustomerMessages.Add(message);
            db.ReminderSteps.Add(new ReminderStep
            {
                ChargeId = chargeId,
                Step = "before_5",
                MessageId = message.Id,
                TenantId = Tenant,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var again = factory.Services.CreateAsyncScope();
        var second = again.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
        second.ReminderSteps.Add(new ReminderStep
        {
            ChargeId = chargeId,
            Step = "before_5",
            MessageId = message.Id,
            TenantId = Tenant,
        });

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => second.SaveChangesAsync(TestContext.Current.CancellationToken));
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("PK_reminder_steps", postgres.ConstraintName);
    }

    [Fact]
    public async Task AServiceRoundTripsCycleStatusReactivationAndLastGenerated()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var service = NewService(BillingCycle.Semiannual);
        service.Pause(Today, Now);
        service.Resume(new DateOnly(2026, 12, 1), Now);
        service.MarkGenerated(new DateOnly(2026, 11, 5));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
            db.Services.Add(service);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var loaded = await read.ServiceProvider.GetRequiredService<ServiceContractsDbContext>().Services
            .AsNoTracking()
            .SingleAsync(value => value.Id == service.Id, TestContext.Current.CancellationToken);

        Assert.Equal(BillingCycle.Semiannual, loaded.Cycle);
        Assert.Equal(ServiceContractStatus.Active, loaded.Status);
        Assert.Equal(new DateOnly(2026, 12, 1), loaded.ReactivatedOn);
        Assert.Equal(new DateOnly(2026, 11, 5), loaded.LastGeneratedPeriodDate);
        Assert.Equal(service.Version, loaded.Version);
        Assert.Equal(new DateOnly(2026, 11, 5), loaded.FirstDueDate);
    }

    // La lista se muta en su sitio (MarkGenerated saca el período): EF tiene que notar el cambio, no sólo guardarla al insertar.
    [Fact]
    public async Task AServiceRoundTripsItsCarriedHeldPeriods()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var first = new DateOnly(2026, 12, 5);
        var second = new DateOnly(2027, 1, 5);
        var service = NewService();
        service.MarkGenerated(new DateOnly(2026, 11, 5));
        service.InitializePriceReview(AdjustmentMode.Ipc, 0m, first);
        ChargeGenerator.Generate(service, null, GenerationMode.Run, new DateOnly(2026, 11, 30), Now);
        service.Pause(new DateOnly(2026, 12, 31), Now);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
            db.Services.Add(service);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
            var tracked = await db.Services.SingleAsync(value => value.Id == service.Id, TestContext.Current.CancellationToken);
            Assert.Equal([first, second], tracked.CarriedHeldPeriods);

            tracked.MarkGenerated(first);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var loaded = await read.ServiceProvider.GetRequiredService<ServiceContractsDbContext>().Services
            .AsNoTracking()
            .SingleAsync(value => value.Id == service.Id, TestContext.Current.CancellationToken);

        Assert.Equal([second], loaded.CarriedHeldPeriods);
        Assert.Equal(first, loaded.LastGeneratedPeriodDate);
    }

    [Fact]
    public async Task TheCycleMonthsCheckRejectsOtherValues()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var service = NewService();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
        db.Services.Add(service);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE billing.services SET cycle_months = 3 WHERE id = {service.Id.Value}",
                TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    private static ServiceContract NewService(BillingCycle cycle = BillingCycle.Monthly) =>
        ServiceContract.Create(
            ServiceContractId.New(),
            Tenant,
            Guid.CreateVersion7(),
            "Hosting",
            150000m,
            "COP",
            cycle,
            new DateOnly(2026, 11, 5),
            Now);

    [Fact]
    public async Task TwoFirstWritesOfTheSameProfileSurfaceAsAConflictNotAServerError()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        // Un cliente real: guardar un perfil nuevo toma el candado del cliente y exige que exista (R7 revocado).
        var customerId = await SeedCustomerAsync(factory, Tenant, "Panaderia La Esquina", "+573001234567", "pan@example.com");
        await using var first = factory.Services.CreateAsyncScope();
        await using var second = factory.Services.CreateAsyncScope();
        // Los dos pasaron el «no hay perfil» antes de que cualquiera guardara: es la carrera del primer PUT.
        first.ServiceProvider.GetRequiredService<ServiceContractsDbContext>().Profiles.Add(
            CustomerBillingProfile.Create(Tenant, customerId, BillingChannel.Email, Guid.CreateVersion7(), Now));
        second.ServiceProvider.GetRequiredService<ServiceContractsDbContext>().Profiles.Add(
            CustomerBillingProfile.Create(Tenant, customerId, BillingChannel.WhatsApp, Guid.CreateVersion7(), Now));
        await first.ServiceProvider.GetRequiredService<IServiceContractsUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ResourceConflictException>(
            () => second.ServiceProvider.GetRequiredService<IServiceContractsUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal("billing.concurrent_first_write", exception.Code);
    }

    private static CustomerMessage NewMessage(Guid customerId, DateOnly runDate) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = Tenant,
        CustomerId = customerId,
        RunDate = runDate,
        Kind = CustomerMessageKind.Reminder,
        Channel = BillingChannel.WhatsApp,
        Status = MessageDeliveryStatus.Sending,
        ClaimedUntil = Now.AddMinutes(5),
        AttemptedAt = Now,
    };
}
