using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.ServiceContracts.Application;
using Modules.ServiceContracts.Domain;
using Modules.ServiceContracts.Infrastructure.Persistence;
using static Modules.ServiceContracts.IntegrationTests.ServiceContractsApiHarness;

namespace Modules.ServiceContracts.IntegrationTests;

/// <summary>Las consultas de los repositorios que el worker y la lista de cobros usan y que las pruebas de esquema no ejercen.</summary>
public sealed class ServiceContractsRepositoryTests
{
    private static readonly Guid Tenant = Guid.Parse(TenantId);
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 13, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 29);

    [Fact]
    public async Task TheSummaryGroupsPendingChargesByCurrencyAndCountsFailedReminders()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var customerId = Guid.CreateVersion7();
        var cop = Service(customerId, "COP", 100m);
        var usd = Service(customerId, "USD", 20m);
        var overdueCop = Charge.Generate(cop, Today.AddDays(-3), Now);
        var upcomingCop = Charge.Generate(cop, Today.AddDays(4), Now);
        var overdueUsd = Charge.Generate(usd, Today.AddDays(-1), Now);
        var paid = Charge.Generate(cop, Today.AddDays(-40), Now);
        paid.MarkPaid(Today.AddDays(-39), null, Guid.CreateVersion7(), Today, Now);
        var failedMessage = Message(customerId, Today, MessageDeliveryStatus.Failed, Now);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
            db.Services.AddRange(cop, usd);
            db.Charges.AddRange(overdueCop, upcomingCop, overdueUsd, paid);
            db.CustomerMessages.Add(failedMessage);
            db.ReminderSteps.Add(new ReminderStep
            {
                ChargeId = overdueCop.Id,
                Step = "after_3",
                MessageId = failedMessage.Id,
                TenantId = Tenant,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var repository = new ChargeRepository(read.ServiceProvider.GetRequiredService<ServiceContractsDbContext>());
        var summary = await repository.SummaryAsync(Tenant, Today, TestContext.Current.CancellationToken);

        Assert.Equal(3, summary.OpenCount);
        Assert.Equal(2, summary.OverdueCount);
        Assert.Equal(1, summary.FailedReminderCount);
        Assert.Equal([new CurrencyAmountDto("COP", 200m), new CurrencyAmountDto("USD", 20m)], summary.OpenAmountByCurrency);
        Assert.Equal([new CurrencyAmountDto("COP", 100m), new CurrencyAmountDto("USD", 20m)], summary.OverdueAmountByCurrency);
    }

    [Fact]
    public async Task TheLastReminderOfAChargeIsTheLatestAttemptedMessage()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var customerId = Guid.CreateVersion7();
        var service = Service(customerId, "COP", 100m);
        var charge = Charge.Generate(service, Today.AddDays(-12), Now);
        var untouched = Charge.Generate(service, Today.AddDays(30), Now);
        var early = Message(customerId, Today.AddDays(-17), MessageDeliveryStatus.Sent, Now.AddDays(-17));
        var late = Message(customerId, Today, MessageDeliveryStatus.Failed, Now);
        late.FailureCode = "no_phone";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
            db.Services.Add(service);
            db.Charges.AddRange(charge, untouched);
            db.CustomerMessages.AddRange(early, late);
            // after_10 se cubrió después de due aunque "after_10" ordene antes que "due" en texto.
            db.ReminderSteps.AddRange(
                new ReminderStep { ChargeId = charge.Id, Step = "due", MessageId = early.Id, TenantId = Tenant },
                new ReminderStep { ChargeId = charge.Id, Step = "after_10", MessageId = late.Id, TenantId = Tenant });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var repository = new ChargeRepository(read.ServiceProvider.GetRequiredService<ServiceContractsDbContext>());
        var reminders = await repository.ListLastRemindersAsync(
            [charge.Id, untouched.Id], TestContext.Current.CancellationToken);

        var reminder = Assert.Single(reminders).Value;
        Assert.Equal(charge.Id, reminders.Keys.Single());
        Assert.Equal("after_10", reminder.Step);
        Assert.Equal("Failed", reminder.Status);
        Assert.Equal("no_phone", reminder.FailureCode);
        Assert.Equal("Reminder", reminder.Kind);
        Assert.Equal("WhatsApp", reminder.Channel);
    }

    [Fact]
    public async Task OpenChargesAreListedByDueDateAndFilteredByAssignee()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var mine = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var me = Guid.CreateVersion7();
        var myService = Service(mine, "COP", 100m);
        var otherService = Service(other, "COP", 50m);
        var later = Charge.Generate(myService, Today.AddDays(10), Now);
        var sooner = Charge.Generate(myService, Today.AddDays(2), Now);
        var notMine = Charge.Generate(otherService, Today.AddDays(1), Now);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
            db.Services.AddRange(myService, otherService);
            db.Charges.AddRange(later, sooner, notMine);
            db.Profiles.AddRange(
                CustomerBillingProfile.Create(Tenant, mine, BillingChannel.WhatsApp, me, Now),
                CustomerBillingProfile.Create(Tenant, other, BillingChannel.Email, Guid.CreateVersion7(), Now));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var repository = new ChargeRepository(read.ServiceProvider.GetRequiredService<ServiceContractsDbContext>());
        var (all, allTotal) = await repository.ListAsync(
            new ChargeQuery(Tenant, ChargeListStatus.Open, null, null, null, null, 1, 25),
            TestContext.Current.CancellationToken);
        var (assigned, assignedTotal) = await repository.ListAsync(
            new ChargeQuery(Tenant, ChargeListStatus.Open, null, me, null, null, 1, 25),
            TestContext.Current.CancellationToken);
        var (byName, byNameTotal) = await repository.ListAsync(
            new ChargeQuery(Tenant, ChargeListStatus.Open, null, null, "hosting", [other], 1, 25),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, allTotal);
        Assert.Equal([notMine.Id, sooner.Id, later.Id], all.Select(charge => charge.Id));
        Assert.Equal(2, assignedTotal);
        Assert.Equal([sooner.Id, later.Id], assigned.Select(charge => charge.Id));
        Assert.Equal(3, byNameTotal);
        Assert.Equal(3, byName.Count);
    }

    [Fact]
    public async Task SettingsAreCreatedOnFirstUpsertAndChangedAfterwards()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var connectionId = Guid.CreateVersion7();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
            var repository = new ServiceContractsSettingsRepository(db);
            var settings = await repository.GetOrDefaultAsync(Tenant, Now, TestContext.Current.CancellationToken);
            Assert.Null(settings.WhatsAppConnectionId);
            settings.ChangeWhatsAppConnection(connectionId, Now);
            repository.Upsert(settings);
            await new ServiceContractsUnitOfWork(db).SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var readDb = read.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
        var loaded = await new ServiceContractsSettingsRepository(readDb)
            .GetOrDefaultAsync(Tenant, Now, TestContext.Current.CancellationToken);
        Assert.Equal(connectionId, loaded.WhatsAppConnectionId);
        Assert.Equal(2, loaded.Version);
    }

    [Fact]
    public async Task AStaleVersionIsTranslatedToARequestConcurrencyException()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var service = Service(Guid.CreateVersion7(), "COP", 100m);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
            db.Services.Add(service);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var first = factory.Services.CreateAsyncScope();
        await using var second = factory.Services.CreateAsyncScope();
        var firstDb = first.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
        var secondDb = second.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
        var firstCopy = await new ServiceContractRepository(firstDb)
            .FindAsync(Tenant, service.Id, TestContext.Current.CancellationToken);
        var secondCopy = await new ServiceContractRepository(secondDb)
            .FindAsync(Tenant, service.Id, TestContext.Current.CancellationToken);
        firstCopy!.Pause(Now);
        secondCopy!.Cancel(Now);
        await new ServiceContractsUnitOfWork(firstDb).SaveChangesAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<RequestConcurrencyException>(
            () => new ServiceContractsUnitOfWork(secondDb).SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ListAllReturnsEveryFilteredServiceOfTheTenantWithoutPaging()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var customerId = Guid.CreateVersion7();
        var otherCustomer = Guid.CreateVersion7();
        var services = Enumerable.Range(0, 3).Select(_ => Service(customerId, "COP", 10m)).ToList();
        var paused = Service(customerId, "COP", 10m);
        paused.Pause(Now);
        var foreign = Service(otherCustomer, "COP", 10m);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
            db.Services.AddRange(services);
            db.Services.AddRange(paused, foreign);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var repository = new ServiceContractRepository(read.ServiceProvider.GetRequiredService<ServiceContractsDbContext>());
        var active = await repository.ListAllAsync(
            new ServiceContractQuery(Tenant, customerId, ServiceContractStatus.Active, null, null, 1, 1),
            TestContext.Current.CancellationToken);
        var byCustomerName = await repository.ListAllAsync(
            new ServiceContractQuery(Tenant, null, null, "zzz", [otherCustomer], 1, 1),
            TestContext.Current.CancellationToken);
        var otherTenant = await repository.ListAllAsync(
            new ServiceContractQuery(Guid.CreateVersion7(), null, null, null, null, 1, 25),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, active.Count);
        Assert.Equal(foreign.Id, Assert.Single(byCustomerName).Id);
        Assert.Empty(otherTenant);
    }

    [Fact]
    public async Task AServiceRoundTripsItsStatusChangedAt()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var service = Service(Guid.CreateVersion7(), "COP", 10m);
        service.Pause(Now.AddDays(1));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
            db.Services.Add(service);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var loaded = await new ServiceContractRepository(read.ServiceProvider.GetRequiredService<ServiceContractsDbContext>())
            .FindAsync(Tenant, service.Id, TestContext.Current.CancellationToken);
        Assert.Equal(Now.AddDays(1), loaded!.StatusChangedAt);
    }

    [Fact]
    public async Task ProfilesAreListedByCustomerWithinTheTenantOnly()
    {
        var (database, factory) = await OpenAsync();
        await using var _ = database;
        using var __ = factory;

        var first = CustomerBillingProfile.Create(Tenant, Guid.CreateVersion7(), BillingChannel.Email, Guid.CreateVersion7(), Now);
        var second = CustomerBillingProfile.Create(Tenant, Guid.CreateVersion7(), BillingChannel.WhatsApp, Guid.CreateVersion7(), Now);
        var unrequested = CustomerBillingProfile.Create(Tenant, Guid.CreateVersion7(), BillingChannel.Email, Guid.CreateVersion7(), Now);
        // El mismo cliente bajo otro tenant no se lee, aunque el id coincida con uno pedido.
        var foreign = CustomerBillingProfile.Create(Guid.CreateVersion7(), first.CustomerId, BillingChannel.Email, Guid.CreateVersion7(), Now);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>();
            db.Profiles.AddRange(first, second, unrequested, foreign);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = factory.Services.CreateAsyncScope();
        var repository = new CustomerBillingProfileRepository(read.ServiceProvider.GetRequiredService<ServiceContractsDbContext>());
        var found = await repository.ListByCustomersAsync(
            Tenant, [first.CustomerId, second.CustomerId], TestContext.Current.CancellationToken);
        var none = await repository.ListByCustomersAsync(Tenant, [], TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { first.CustomerId, second.CustomerId }.Order(),
            found.Select(profile => profile.CustomerId).Order());
        Assert.All(found, profile => Assert.Equal(Tenant, profile.TenantId));
        Assert.Empty(none);
    }

    private static ServiceContract Service(Guid customerId, string currency, decimal price) =>
        ServiceContract.Create(
            ServiceContractId.New(), Tenant, customerId, "Hosting", price, currency, BillingCycle.Monthly, Today.AddMonths(-1), Now);

    private static CustomerMessage Message(
        Guid customerId, DateOnly runDate, MessageDeliveryStatus status, DateTimeOffset attemptedAt) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = Tenant,
        CustomerId = customerId,
        RunDate = runDate,
        Kind = CustomerMessageKind.Reminder,
        Channel = BillingChannel.WhatsApp,
        Status = status,
        ClaimedUntil = attemptedAt.AddMinutes(5),
        AttemptedAt = attemptedAt,
    };
}
