using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Billing.Application;
using Modules.Billing.Domain;
using Modules.Billing.Infrastructure.Persistence;
using Npgsql;
using static Modules.Billing.IntegrationTests.BillingApiHarness;

namespace Modules.Billing.IntegrationTests;

/// <summary>
/// El esquema <c>billing</c> (spec 2026-10-29 §2): las unicidades que sostienen la deduplicación del
/// worker y los CHECK que la base impone aunque el dominio ya valide.
/// </summary>
public sealed class BillingPersistenceTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ASecondChargeWithTheSameServiceAndDueDateIsRejected()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var service = NewService();
        var due = new DateOnly(2026, 11, 5);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            db.Services.Add(service);
            db.Charges.Add(Charge.Generate(service, due, Now));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var again = factory.Services.CreateAsyncScope();
        var second = again.ServiceProvider.GetRequiredService<BillingDbContext>();
        second.Charges.Add(Charge.Generate(service, due, Now));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => second.SaveChangesAsync(TestContext.Current.CancellationToken));
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("IX_charges_service_due", postgres.ConstraintName);
    }

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
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            db.CustomerMessages.Add(NewMessage(customerId, day));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var again = factory.Services.CreateAsyncScope();
        var second = again.ServiceProvider.GetRequiredService<BillingDbContext>();
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
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
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
        var second = again.ServiceProvider.GetRequiredService<BillingDbContext>();
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
        service.Pause(Now);
        service.Resume(new DateOnly(2026, 12, 1), Now);
        service.MarkGenerated(new DateOnly(2026, 11, 5));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
            db.Services.Add(service);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var loaded = await read.ServiceProvider.GetRequiredService<BillingDbContext>().Services
            .AsNoTracking()
            .SingleAsync(value => value.Id == service.Id, TestContext.Current.CancellationToken);

        Assert.Equal(BillingCycle.Semiannual, loaded.Cycle);
        Assert.Equal(BillingServiceStatus.Active, loaded.Status);
        Assert.Equal(new DateOnly(2026, 12, 1), loaded.ReactivatedOn);
        Assert.Equal(new DateOnly(2026, 11, 5), loaded.LastGeneratedDueDate);
        Assert.Equal(service.Version, loaded.Version);
        Assert.Equal(new DateOnly(2026, 11, 5), loaded.FirstDueDate);
    }

    [Fact]
    public async Task TheCycleMonthsCheckRejectsOtherValues()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var service = NewService();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        db.Services.Add(service);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE billing.services SET cycle_months = 3 WHERE id = {service.Id.Value}",
                TestContext.Current.CancellationToken));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    private static BillingService NewService(BillingCycle cycle = BillingCycle.Monthly) =>
        BillingService.Create(
            BillingServiceId.New(),
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

        var customerId = Guid.CreateVersion7();
        await using var first = factory.Services.CreateAsyncScope();
        await using var second = factory.Services.CreateAsyncScope();
        // Los dos pasaron el «no hay perfil» antes de que cualquiera guardara: es la carrera del primer PUT.
        first.ServiceProvider.GetRequiredService<BillingDbContext>().Profiles.Add(
            CustomerBillingProfile.Create(Tenant, customerId, BillingChannel.Email, Guid.CreateVersion7(), Now));
        second.ServiceProvider.GetRequiredService<BillingDbContext>().Profiles.Add(
            CustomerBillingProfile.Create(Tenant, customerId, BillingChannel.WhatsApp, Guid.CreateVersion7(), Now));
        await first.ServiceProvider.GetRequiredService<IBillingUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ResourceConflictException>(
            () => second.ServiceProvider.GetRequiredService<IBillingUnitOfWork>()
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
