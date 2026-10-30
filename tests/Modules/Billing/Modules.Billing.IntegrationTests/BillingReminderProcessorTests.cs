using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Billing.Application;
using Modules.Billing.Domain;
using Modules.Billing.Infrastructure.Persistence;
using Modules.Tenancy.Application;
using Testcontainers.PostgreSql;
using static Modules.Billing.IntegrationTests.BillingApiHarness;

namespace Modules.Billing.IntegrationTests;

/// <summary>
/// La corrida diaria de un tenant (spec cobros-recurrentes §3.3 y §3.4) contra Postgres real: la deduplicación vive en índices
/// únicos y sólo la base la prueba. El reloj del tenant es fijo (09:00 del 2026-03-10 en Bogotá) y los envíos van a grabadores:
/// nada depende del día en que corre la prueba.
/// </summary>
public sealed class BillingReminderProcessorTests : IAsyncLifetime
{
    private static readonly Guid Tenant = Guid.Parse(BillingApiHarness.TenantId);
    private static readonly DateOnly Today = new(2026, 3, 10);

    // 09:00 en America/Bogota (UTC-5, sin horario de verano), una hora después de SendHourLocal = 8.
    private static readonly DateTimeOffset NineLocal = new(2026, 3, 10, 14, 0, 0, TimeSpan.Zero);

    private readonly MutableTenantClock clock = new(NineLocal);
    private readonly RecordingCustomerEmail customerEmail = new();
    private readonly RecordingStaffEmail staffEmail = new();
    private readonly RecordingWhatsApp whatsApp = new();
    private PostgreSqlContainer database = null!;
    private QepApiFactory factory = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        database = await StartDatabaseAsync();
        factory = new QepApiFactory(database.GetConnectionString(), services =>
        {
            services.RemoveAll<ITenantClock>();
            services.AddSingleton<ITenantClock>(clock);
            services.RemoveAll<IBillingCustomerEmail>();
            services.AddSingleton<IBillingCustomerEmail>(customerEmail);
            services.RemoveAll<IBillingStaffEmail>();
            services.AddSingleton<IBillingStaffEmail>(staffEmail);
            services.RemoveAll<IBillingWhatsAppOutbound>();
            services.AddSingleton<IBillingWhatsAppOutbound>(whatsApp);
        });
        await SeedTenantAsync(factory);
    }

    public async ValueTask DisposeAsync()
    {
        await factory.DisposeAsync();
        await database.DisposeAsync();
    }

    [Fact]
    public async Task ADueTodayChargeGetsOneEmailOneMessageRowAndOneStepRow()
    {
        var assignee = await SeedMemberAsync(factory, "owner@example.com", "Owner", "billing");
        var customer = await SeedCustomerAsync(factory, Tenant, "Acme", email: "acme@example.com");
        await SeedProfileAsync(customer, BillingChannel.Email, assignee);
        var charge = await SeedChargeAsync(customer, Today);

        await ProcessAsync();

        var sent = Assert.Single(customerEmail.Sent);
        Assert.Equal(BillingMessageKind.Reminder, sent.Kind);
        Assert.Equal("acme@example.com", sent.ToAddress);
        Assert.Equal("Acme", sent.CustomerName);
        Assert.Equal(Today, sent.NearestDueDate);
        var message = Assert.Single(await MessagesAsync());
        Assert.Equal(MessageDeliveryStatus.Sent, message.Status);
        Assert.Equal(CustomerMessageKind.Reminder, message.Kind);
        Assert.Equal(BillingChannel.Email, message.Channel);
        Assert.Equal(Today, message.RunDate);
        var step = Assert.Single(await StepsAsync());
        Assert.Equal((charge.Id, "due", message.Id), (step.ChargeId, step.Step, step.MessageId));
        var run = Assert.Single(await RunsAsync());
        Assert.NotNull(run.FinishedAt);
    }

    [Fact]
    public async Task RunningTwiceTheSameDaySendsNothingMore()
    {
        var assignee = await SeedMemberAsync(factory, "owner@example.com", "Owner", "billing");
        var customer = await SeedCustomerAsync(factory, Tenant, "Acme", email: "acme@example.com");
        await SeedProfileAsync(customer, BillingChannel.Email, assignee);
        await SeedChargeAsync(customer, Today);

        await ProcessAsync();
        clock.UtcNow = NineLocal.AddHours(3);
        await ProcessAsync();

        Assert.Single(customerEmail.Sent);
        Assert.Single(staffEmail.Sent);
        Assert.Single(await MessagesAsync());
    }

    [Fact]
    public async Task TwoConcurrentRunsGenerateOnceAndMessageOnce()
    {
        var assignee = await SeedMemberAsync(factory, "owner@example.com", "Owner", "billing");
        var customer = await SeedCustomerAsync(factory, Tenant, "Acme", email: "acme@example.com");
        await SeedProfileAsync(customer, BillingChannel.Email, assignee);
        // El período anterior está pagado; el de hoy entra en ventana y lo tiene que generar la corrida.
        var service = await SeedServiceAsync(customer, Today.AddMonths(-1), lastGenerated: Today.AddMonths(-1), paidDueDates: [Today.AddMonths(-1)]);

        await Task.WhenAll(ProcessAsync(), ProcessAsync());

        Assert.Equal([Today.AddMonths(-1), Today], (await ChargesOfAsync(service)).Select(c => c.DueDate));
        Assert.Single(customerEmail.Sent);
        Assert.Single(await MessagesAsync());
        Assert.Single(await StepsAsync());
    }

    [Fact]
    public async Task ACatchUpCoversOnlyTheLatestDueStep()
    {
        var assignee = await SeedMemberAsync(factory, "owner@example.com", "Owner", "billing");
        var customer = await SeedCustomerAsync(factory, Tenant, "Acme", email: "acme@example.com");
        await SeedProfileAsync(customer, BillingChannel.Email, assignee);
        var charge = await SeedChargeAsync(customer, Today.AddDays(-12));

        await ProcessAsync();

        var step = Assert.Single(await StepsAsync());
        Assert.Equal((charge.Id, "after_10"), (step.ChargeId, step.Step));
        Assert.Equal(BillingMessageKind.Overdue, Assert.Single(customerEmail.Sent).Kind);
    }

    [Fact]
    public async Task ACatchUpGeneratesEveryMissedPeriod()
    {
        var customer = await SeedCustomerAsync(factory, Tenant, "Acme", email: "acme@example.com");
        // Fecha fija (no relativa al día de la corrida): 10 dic → 10 ene, 10 feb, 10 mar caen en la ventana de hoy + 5.
        var anchor = new DateOnly(2025, 12, 10);
        var service = await SeedServiceAsync(customer, anchor, lastGenerated: anchor, paidDueDates: [anchor]);

        await ProcessAsync();

        Assert.Equal(
            [anchor, new DateOnly(2026, 1, 10), new DateOnly(2026, 2, 10), new DateOnly(2026, 3, 10)],
            (await ChargesOfAsync(service)).Select(c => c.DueDate));
        Assert.Equal(new DateOnly(2026, 3, 10), (await ServiceAsync(service)).LastGeneratedDueDate);
    }

    [Fact]
    public async Task TheRunGeneratesTheChargeFiveDaysBeforeItsDueDateAndNotEarlier()
    {
        var customer = await SeedCustomerAsync(factory, Tenant, "Acme", email: "acme@example.com");
        var inWindow = Today.AddDays(5);
        var outOfWindow = Today.AddDays(6);
        var reached = await SeedServiceAsync(customer, inWindow.AddMonths(-1), lastGenerated: inWindow.AddMonths(-1), paidDueDates: [inWindow.AddMonths(-1)]);
        var notYet = await SeedServiceAsync(customer, outOfWindow.AddMonths(-1), lastGenerated: outOfWindow.AddMonths(-1), paidDueDates: [outOfWindow.AddMonths(-1)]);

        await ProcessAsync();

        Assert.Contains(inWindow, (await ChargesOfAsync(reached)).Select(c => c.DueDate));
        Assert.DoesNotContain(outOfWindow, (await ChargesOfAsync(notYet)).Select(c => c.DueDate));
    }

    [Fact]
    public async Task AnOverdueMessageGroupsEveryOverdueChargeWithOneTotalPerCurrency()
    {
        var assignee = await SeedMemberAsync(factory, "owner@example.com", "Owner", "billing");
        var customer = await SeedCustomerAsync(factory, Tenant, "Acme", email: "acme@example.com");
        await SeedProfileAsync(customer, BillingChannel.Email, assignee);
        await SeedChargeAsync(customer, Today.AddDays(-3), price: 450000m, currency: "COP", name: "Hosting");
        await SeedChargeAsync(customer, Today.AddDays(-10), price: 20m, currency: "USD", name: "Dominio");

        await ProcessAsync();

        var sent = Assert.Single(customerEmail.Sent);
        Assert.Equal(BillingMessageKind.Overdue, sent.Kind);
        Assert.Equal(2, sent.Lines.Count);
        Assert.Equal(
            new HashSet<CurrencyAmountDto> { new("COP", 450000m), new("USD", 20m) },
            sent.TotalByCurrency.ToHashSet());
        Assert.Equal(Today.AddDays(-10), sent.NearestDueDate);
        Assert.Equal(10, sent.Lines.Single(line => line.Currency == "USD").DaysOverdue);
        Assert.Single(await MessagesAsync());
        Assert.Equal(2, (await StepsAsync()).Count);
    }

    [Fact]
    public async Task TwoCustomersGetTwoMessagesAndOneCustomerNeverGetsTwo()
    {
        var assignee = await SeedMemberAsync(factory, "owner@example.com", "Owner", "billing");
        var acme = await SeedCustomerAsync(factory, Tenant, "Acme", email: "acme@example.com");
        var globex = await SeedCustomerAsync(factory, Tenant, "Globex", email: "globex@example.com");
        await SeedProfileAsync(acme, BillingChannel.Email, assignee);
        await SeedProfileAsync(globex, BillingChannel.Email, assignee);
        await SeedChargeAsync(acme, Today, name: "Hosting");
        await SeedChargeAsync(acme, Today.AddDays(4), name: "Soporte");
        await SeedChargeAsync(globex, Today, name: "Hosting");

        await ProcessAsync();

        Assert.Equal(["acme@example.com", "globex@example.com"], customerEmail.Sent.Select(m => m.ToAddress).Order());
        Assert.Equal(2, customerEmail.Sent.Single(m => m.ToAddress == "acme@example.com").Lines.Count);
        Assert.Equal(2, (await MessagesAsync()).Count);
        Assert.Equal(3, (await StepsAsync()).Count);
    }

    [Fact]
    public async Task PaidAndCancelledChargesGetNoReminderButAPausedServicesPendingChargesDo()
    {
        var assignee = await SeedMemberAsync(factory, "owner@example.com", "Owner", "billing");
        var acme = await SeedCustomerAsync(factory, Tenant, "Acme", email: "acme@example.com");
        var globex = await SeedCustomerAsync(factory, Tenant, "Globex", email: "globex@example.com");
        await SeedProfileAsync(acme, BillingChannel.Email, assignee);
        await SeedProfileAsync(globex, BillingChannel.Email, assignee);
        await SeedServiceAsync(acme, Today, lastGenerated: Today, paidDueDates: [Today]);
        await SeedServiceAsync(acme, Today, lastGenerated: Today, pendingDueDates: [Today], status: BillingServiceStatus.Cancelled);
        await SeedServiceAsync(globex, Today, lastGenerated: Today, pendingDueDates: [Today], status: BillingServiceStatus.Paused);

        await ProcessAsync();

        Assert.Equal("globex@example.com", Assert.Single(customerEmail.Sent).ToAddress);
    }

    [Fact]
    public async Task APausedServiceGeneratesNoNewCharge()
    {
        var customer = await SeedCustomerAsync(factory, Tenant, "Acme", email: "acme@example.com");
        var service = await SeedServiceAsync(
            customer, Today.AddMonths(-1), lastGenerated: Today.AddMonths(-1), paidDueDates: [Today.AddMonths(-1)], status: BillingServiceStatus.Paused);

        await ProcessAsync();

        Assert.Equal([Today.AddMonths(-1)], (await ChargesOfAsync(service)).Select(c => c.DueDate));
    }

    [Fact]
    public async Task BeforeTheSendHourNothingHappens()
    {
        var assignee = await SeedMemberAsync(factory, "owner@example.com", "Owner", "billing");
        var customer = await SeedCustomerAsync(factory, Tenant, "Acme", email: "acme@example.com");
        await SeedProfileAsync(customer, BillingChannel.Email, assignee);
        await SeedChargeAsync(customer, Today);
        var service = await SeedServiceAsync(customer, Today.AddMonths(-1), lastGenerated: Today.AddMonths(-1), paidDueDates: [Today.AddMonths(-1)]);
        clock.UtcNow = new DateTimeOffset(2026, 3, 10, 12, 59, 0, TimeSpan.Zero); // 07:59 en Bogotá

        await ProcessAsync();

        Assert.Empty(customerEmail.Sent);
        Assert.Empty(staffEmail.Sent);
        Assert.Empty(await MessagesAsync());
        Assert.Empty(await RunsAsync());
        Assert.Single(await ChargesOfAsync(service));
    }

    [Fact]
    public async Task AWhatsAppProfileWithoutConnectionFailsWithChannelNotConfiguredAndNeverFallsBackToEmail()
    {
        var assignee = await SeedMemberAsync(factory, "owner@example.com", "Owner", "billing");
        var customer = await SeedCustomerAsync(factory, Tenant, "Acme", phoneE164: "+573001112233", email: "acme@example.com");
        await SeedProfileAsync(customer, BillingChannel.WhatsApp, assignee);
        await SeedChargeAsync(customer, Today);

        await ProcessAsync();

        Assert.Empty(customerEmail.Sent);
        Assert.Empty(whatsApp.Sent);
        var message = Assert.Single(await MessagesAsync());
        Assert.Equal((MessageDeliveryStatus.Failed, "channel_not_configured", BillingChannel.WhatsApp), (message.Status, message.FailureCode, message.Channel));
        Assert.Single(await StepsAsync());
    }

    [Fact]
    public async Task AWhatsAppProfileWithAConnectionSendsThroughItToThePhone()
    {
        var assignee = await SeedMemberAsync(factory, "owner@example.com", "Owner", "billing");
        var customer = await SeedCustomerAsync(factory, Tenant, "Acme", phoneE164: "+573001112233", email: "acme@example.com");
        await SeedProfileAsync(customer, BillingChannel.WhatsApp, assignee);
        await SeedChargeAsync(customer, Today);
        var connectionId = Guid.NewGuid();
        await SeedWhatsAppConnectionAsync(connectionId);

        await ProcessAsync();

        var (sent, usedConnection) = Assert.Single(whatsApp.Sent);
        Assert.Equal(("+573001112233", connectionId), (sent.ToAddress, usedConnection));
        Assert.Empty(customerEmail.Sent);
        Assert.Equal(MessageDeliveryStatus.Sent, Assert.Single(await MessagesAsync()).Status);
    }

    [Fact]
    public async Task AWhatsAppProfileWhoseCustomerLostThePhoneFailsWithNoPhone()
    {
        var assignee = await SeedMemberAsync(factory, "owner@example.com", "Owner", "billing");
        var customer = await SeedCustomerAsync(factory, Tenant, "Acme", email: "acme@example.com");
        await SeedProfileAsync(customer, BillingChannel.WhatsApp, assignee);
        await SeedChargeAsync(customer, Today);
        await SeedWhatsAppConnectionAsync(Guid.NewGuid());

        await ProcessAsync();

        Assert.Empty(whatsApp.Sent);
        Assert.Empty(customerEmail.Sent);
        Assert.Equal("no_phone", Assert.Single(await MessagesAsync()).FailureCode);
    }

    [Fact]
    public async Task AnEmailProfileWhoseCustomerLostTheAddressFailsWithNoEmail()
    {
        var assignee = await SeedMemberAsync(factory, "owner@example.com", "Owner", "billing");
        var customer = await SeedCustomerAsync(factory, Tenant, "Acme", phoneE164: "+573001112233");
        await SeedProfileAsync(customer, BillingChannel.Email, assignee);
        await SeedChargeAsync(customer, Today);

        await ProcessAsync();

        Assert.Empty(customerEmail.Sent);
        Assert.Empty(whatsApp.Sent);
        var message = Assert.Single(await MessagesAsync());
        Assert.Equal((MessageDeliveryStatus.Failed, "no_email"), (message.Status, message.FailureCode));
    }

    [Fact]
    public async Task TheDigestGoesOncePerAssigneeWithDueSoonOverdueAndFailedRows()
    {
        var assignee = await SeedMemberAsync(factory, "owner@example.com", "Owner", "billing");
        var upcoming = await SeedCustomerAsync(factory, Tenant, "Upcoming", email: "upcoming@example.com");
        var late = await SeedCustomerAsync(factory, Tenant, "Late", email: "late@example.com");
        var unreachable = await SeedCustomerAsync(factory, Tenant, "Unreachable");
        foreach (var customer in new[] { upcoming, late, unreachable })
        {
            await SeedProfileAsync(customer, BillingChannel.Email, assignee);
        }

        await SeedChargeAsync(upcoming, Today.AddDays(3), name: "Due soon");
        await SeedChargeAsync(upcoming, Today.AddDays(6), name: "Too far");
        await SeedChargeAsync(late, Today.AddDays(-4), name: "Overdue");
        await SeedChargeAsync(unreachable, Today.AddDays(1), name: "Failed");

        await ProcessAsync();
        clock.UtcNow = NineLocal.AddHours(2);
        await ProcessAsync();

        var digest = Assert.Single(staffEmail.Sent);
        Assert.Equal(("owner@example.com", "Owner"), (digest.ToAddress, digest.MemberName));
        Assert.Equal(["Due soon", "Failed"], digest.DueSoon.Select(row => row.ServiceName).Order());
        Assert.Equal(["Overdue"], digest.Overdue.Select(row => row.ServiceName));
        var failed = Assert.Single(digest.FailedReminders);
        Assert.Equal(("Failed", "Unreachable", "no_email"), (failed.ServiceName, failed.CustomerName, failed.FailureCode));
        Assert.Equal([new CurrencyAmountDto("COP", 100000m)], digest.OverdueTotalByCurrency);
        var row = Assert.Single(await DigestsAsync());
        Assert.Equal((assignee, MessageDeliveryStatus.Sent), (row.MemberId, row.Status));
    }

    [Fact]
    public async Task AnAssigneeNoLongerActiveOrWithoutManageSendsTheRowsToEveryManager()
    {
        var removed = await SeedMemberAsync(factory, "gone@example.com", "Gone", "billing", removed: true);
        var advisor = await SeedMemberAsync(factory, "advisor@example.com", "Advisor", "advisor");
        await SeedMemberAsync(factory, "admin@example.com", "Admin", "admin");
        await SeedMemberAsync(factory, "billing@example.com", "Billing", "billing");
        var acme = await SeedCustomerAsync(factory, Tenant, "Acme", email: "acme@example.com");
        var globex = await SeedCustomerAsync(factory, Tenant, "Globex", email: "globex@example.com");
        await SeedProfileAsync(acme, BillingChannel.Email, removed);
        await SeedProfileAsync(globex, BillingChannel.Email, advisor);
        await SeedChargeAsync(acme, Today, name: "Acme hosting");
        await SeedChargeAsync(globex, Today, name: "Globex hosting");

        await ProcessAsync();

        Assert.Equal(["admin@example.com", "billing@example.com"], staffEmail.Sent.Select(d => d.ToAddress).Order());
        Assert.All(staffEmail.Sent, digest =>
            Assert.Equal(["Acme hosting", "Globex hosting"], digest.DueSoon.Select(row => row.ServiceName).Order()));
        Assert.Equal(2, (await DigestsAsync()).Count);
    }

    [Fact]
    public async Task NoRowsOfInterestSendsNoDigest()
    {
        var assignee = await SeedMemberAsync(factory, "owner@example.com", "Owner", "billing");
        var customer = await SeedCustomerAsync(factory, Tenant, "Acme", email: "acme@example.com");
        await SeedProfileAsync(customer, BillingChannel.Email, assignee);
        await SeedChargeAsync(customer, Today.AddDays(20));

        await ProcessAsync();

        Assert.Empty(staffEmail.Sent);
        Assert.Empty(await DigestsAsync());
        Assert.NotNull(Assert.Single(await RunsAsync()).FinishedAt);
    }

    [Fact]
    public async Task AFailingCustomerSendDoesNotStopTheOthers()
    {
        var assignee = await SeedMemberAsync(factory, "owner@example.com", "Owner", "billing");
        var broken = await SeedCustomerAsync(factory, Tenant, "Broken", email: "broken@example.com");
        var fine = await SeedCustomerAsync(factory, Tenant, "Fine", email: "fine@example.com");
        await SeedProfileAsync(broken, BillingChannel.Email, assignee);
        await SeedProfileAsync(fine, BillingChannel.Email, assignee);
        var brokenCharge = await SeedChargeAsync(broken, Today);
        await SeedChargeAsync(fine, Today);
        customerEmail.ThrowFor = "broken@example.com";

        await ProcessAsync();

        Assert.Equal("fine@example.com", Assert.Single(customerEmail.Sent).ToAddress);
        var messages = await MessagesAsync();
        var failed = messages.Single(m => m.CustomerId == broken);
        Assert.Equal((MessageDeliveryStatus.Failed, "send_failed"), (failed.Status, failed.FailureCode));
        Assert.Equal(MessageDeliveryStatus.Sent, messages.Single(m => m.CustomerId == fine).Status);
        Assert.Contains(await StepsAsync(), step => step.ChargeId == brokenCharge.Id && step.MessageId == failed.Id);
    }

    [Fact]
    public async Task ASendingMessageWhoseLeaseExpiredIsRetriedOnce()
    {
        var assignee = await SeedMemberAsync(factory, "owner@example.com", "Owner", "billing");
        var customer = await SeedCustomerAsync(factory, Tenant, "Acme", email: "acme@example.com");
        await SeedProfileAsync(customer, BillingChannel.Email, assignee);
        var charge = await SeedChargeAsync(customer, Today);
        // Una réplica reclamó el mensaje y se cayó antes de enviarlo: la fila quedó Sending con el lease vencido y la corrida sin terminar.
        var messageId = await SeedStaleSendingMessageAsync(customer, charge.Id, "due", alreadyRetried: false);

        await ProcessAsync();

        Assert.Equal("acme@example.com", Assert.Single(customerEmail.Sent).ToAddress);
        var message = Assert.Single(await MessagesAsync());
        Assert.Equal((messageId, MessageDeliveryStatus.Sent, (string?)null), (message.Id, message.Status, message.FailureCode));
        Assert.Single(await StepsAsync());
    }

    [Fact]
    public async Task ASendingMessageThatAlreadyHadItsRetryIsMarkedFailed()
    {
        var assignee = await SeedMemberAsync(factory, "owner@example.com", "Owner", "billing");
        var customer = await SeedCustomerAsync(factory, Tenant, "Acme", email: "acme@example.com");
        await SeedProfileAsync(customer, BillingChannel.Email, assignee);
        var charge = await SeedChargeAsync(customer, Today);
        await SeedStaleSendingMessageAsync(customer, charge.Id, "due", alreadyRetried: true);

        await ProcessAsync();

        Assert.Empty(customerEmail.Sent);
        var message = Assert.Single(await MessagesAsync());
        Assert.Equal((MessageDeliveryStatus.Failed, "send_failed"), (message.Status, message.FailureCode));
    }

    private async Task ProcessAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IBillingReminderProcessor>().ProcessTenantAsync(Tenant, Ct);
    }

    private async Task<T> WithDbAsync<T>(Func<BillingDbContext, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<BillingDbContext>());
    }

    private Task<List<CustomerMessage>> MessagesAsync() => WithDbAsync(db => db.CustomerMessages.AsNoTracking().ToListAsync(Ct));

    private Task<List<ReminderStep>> StepsAsync() => WithDbAsync(db => db.ReminderSteps.AsNoTracking().ToListAsync(Ct));

    private Task<List<DailyDigest>> DigestsAsync() => WithDbAsync(db => db.DailyDigests.AsNoTracking().ToListAsync(Ct));

    private Task<List<TenantRun>> RunsAsync() => WithDbAsync(db => db.TenantRuns.AsNoTracking().ToListAsync(Ct));

    private Task<List<Charge>> ChargesOfAsync(BillingServiceId serviceId) =>
        WithDbAsync(db => db.Charges.AsNoTracking().Where(c => c.ServiceId == serviceId).OrderBy(c => c.DueDate).ToListAsync(Ct));

    private Task<BillingService> ServiceAsync(BillingServiceId serviceId) =>
        WithDbAsync(db => db.Services.AsNoTracking().SingleAsync(s => s.Id == serviceId, Ct));

    private Task<bool> SeedProfileAsync(Guid customerId, BillingChannel channel, Guid assignee) =>
        WithDbAsync(async db =>
        {
            db.Profiles.Add(CustomerBillingProfile.Create(Tenant, customerId, channel, assignee, NineLocal));
            await db.SaveChangesAsync(Ct);
            return true;
        });

    private Task<bool> SeedWhatsAppConnectionAsync(Guid connectionId) =>
        WithDbAsync(async db =>
        {
            var settings = BillingSettings.Create(Tenant, NineLocal);
            settings.ChangeWhatsAppConnection(connectionId, NineLocal);
            db.Settings.Add(settings);
            await db.SaveChangesAsync(Ct);
            return true;
        });

    /// <summary>
    /// Un servicio mensual anclado en <paramref name="firstDueDate"/> que ya generó hasta <paramref name="lastGenerated"/>, con sus
    /// cobros sembrados a mano (pagados o pendientes): así la corrida sólo genera lo que de verdad falta.
    /// </summary>
    private Task<BillingServiceId> SeedServiceAsync(
        Guid customerId,
        DateOnly firstDueDate,
        DateOnly lastGenerated,
        DateOnly[]? paidDueDates = null,
        DateOnly[]? pendingDueDates = null,
        BillingServiceStatus status = BillingServiceStatus.Active,
        decimal price = 100000m,
        string currency = "COP",
        string name = "Hosting") =>
        WithDbAsync(async db =>
        {
            var service = BillingService.Create(
                BillingServiceId.New(), Tenant, customerId, name, price, currency, BillingCycle.Monthly, firstDueDate, NineLocal);
            service.MarkGenerated(lastGenerated);
            var charges = new List<Charge>();
            foreach (var due in paidDueDates ?? [])
            {
                var paid = Charge.Generate(service, due, NineLocal);
                paid.MarkPaid(due, null, Guid.NewGuid(), Today, NineLocal);
                charges.Add(paid);
            }

            charges.AddRange((pendingDueDates ?? []).Select(due => Charge.Generate(service, due, NineLocal)));
            if (status == BillingServiceStatus.Paused)
            {
                service.Pause(NineLocal);
            }

            if (status == BillingServiceStatus.Cancelled)
            {
                service.Cancel(NineLocal);
                charges.ForEach(charge =>
                {
                    if (charge.Status == ChargeStatus.Pending)
                    {
                        charge.Cancel(NineLocal);
                    }
                });
            }

            db.Services.Add(service);
            db.Charges.AddRange(charges);
            await db.SaveChangesAsync(Ct);
            return service.Id;
        });

    /// <summary>Un cobro <c>Pending</c> con ese vencimiento en un servicio propio que ya generó hasta ahí: la corrida no le agrega nada.</summary>
    private async Task<Charge> SeedChargeAsync(
        Guid customerId, DateOnly dueDate, decimal price = 100000m, string currency = "COP", string name = "Hosting")
    {
        var service = await SeedServiceAsync(
            customerId, dueDate, dueDate, pendingDueDates: [dueDate], price: price, currency: currency, name: name);
        return Assert.Single(await ChargesOfAsync(service));
    }

    private Task<Guid> SeedStaleSendingMessageAsync(Guid customerId, Guid chargeId, string step, bool alreadyRetried) =>
        WithDbAsync(async db =>
        {
            var message = new CustomerMessage
            {
                Id = Guid.CreateVersion7(),
                TenantId = Tenant,
                CustomerId = customerId,
                RunDate = Today,
                Kind = CustomerMessageKind.Reminder,
                Channel = BillingChannel.Email,
                Status = MessageDeliveryStatus.Sending,
                FailureCode = alreadyRetried ? "send_failed" : null,
                AttemptedAt = NineLocal.AddMinutes(-20),
                ClaimedUntil = NineLocal.AddMinutes(-15),
            };
            db.CustomerMessages.Add(message);
            db.ReminderSteps.Add(new ReminderStep { ChargeId = chargeId, Step = step, MessageId = message.Id, TenantId = Tenant });
            db.TenantRuns.Add(new TenantRun { TenantId = Tenant, RunDate = Today, ClaimedUntil = NineLocal.AddMinutes(-15) });
            await db.SaveChangesAsync(Ct);
            return message.Id;
        });

    private sealed class MutableTenantClock(DateTimeOffset utcNow) : ITenantClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new TenantCalendar(UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota")));
    }

    private sealed class RecordingCustomerEmail : IBillingCustomerEmail
    {
        private readonly ConcurrentQueue<BillingCustomerMessage> sent = new();

        public string? ThrowFor { get; set; }

        public IReadOnlyList<BillingCustomerMessage> Sent => sent.ToArray();

        public Task<BillingSendResult> SendAsync(BillingCustomerMessage message, CancellationToken cancellationToken)
        {
            if (message.ToAddress == ThrowFor)
            {
                throw new InvalidOperationException("The provider is down.");
            }

            sent.Enqueue(message);
            return Task.FromResult<BillingSendResult>(new BillingSendResult.Sent());
        }
    }

    private sealed class RecordingStaffEmail : IBillingStaffEmail
    {
        private readonly ConcurrentQueue<BillingDigestEmail> sent = new();

        public IReadOnlyList<BillingDigestEmail> Sent => sent.ToArray();

        public Task<BillingSendResult> SendDigestAsync(BillingDigestEmail digest, CancellationToken cancellationToken)
        {
            sent.Enqueue(digest);
            return Task.FromResult<BillingSendResult>(new BillingSendResult.Sent());
        }
    }

    private sealed class RecordingWhatsApp : IBillingWhatsAppOutbound
    {
        private readonly ConcurrentQueue<(BillingCustomerMessage, Guid)> sent = new();

        public IReadOnlyList<(BillingCustomerMessage Message, Guid ConnectionId)> Sent => sent.ToArray();

        public Task<BillingSendResult> SendAsync(BillingCustomerMessage message, Guid connectionId, CancellationToken cancellationToken)
        {
            sent.Enqueue((message, connectionId));
            return Task.FromResult<BillingSendResult>(new BillingSendResult.Sent());
        }

        public Task<IReadOnlyList<BillingConnectionDto>> ListConnectionsAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BillingConnectionDto>>([]);
    }
}
