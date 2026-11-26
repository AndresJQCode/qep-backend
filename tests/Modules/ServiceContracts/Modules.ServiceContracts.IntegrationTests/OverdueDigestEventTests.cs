using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.ServiceContracts.Application;
using Modules.ServiceContracts.Domain;
using Modules.ServiceContracts.Infrastructure.Persistence;
using Modules.Tenancy.Application;
using Npgsql;
using Testcontainers.PostgreSql;
using static Modules.ServiceContracts.IntegrationTests.ServiceContractsApiHarness;

namespace Modules.ServiceContracts.IntegrationTests;

/// <summary>
/// Spec 2026-11-22 D-17 and D-18: the daily run writes <c>billing.charges.overdue-digest.v1</c> right after it claims the digest of a
/// member and before it looks at the member's email, only when something is overdue. The clock of the tenant is fixed (09:00 of
/// 2026-03-10 in Bogota) and the emails go to recorders, as in <see cref="ServiceContractsReminderProcessorTests"/>.
/// </summary>
public sealed class OverdueDigestEventTests : IAsyncLifetime
{
    private const string EventName = "billing.charges.overdue-digest.v1";
    private const string PriceReviewEventName = "billing.price-reviews.digest.v1";

    private static readonly Guid Tenant = Guid.Parse(ServiceContractsApiHarness.TenantId);
    private static readonly DateOnly Today = new(2026, 3, 10);

    // 09:00 in America/Bogota (UTC-5), one hour after SendHourLocal = 8.
    private static readonly DateTimeOffset NineLocal = new(2026, 3, 10, 14, 0, 0, TimeSpan.Zero);

    private readonly FixedTenantClock clock = new(NineLocal);
    private readonly RecordingStaffEmail staffEmail = new();
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
            services.RemoveAll<IServiceContractsStaffEmail>();
            services.AddSingleton<IServiceContractsStaffEmail>(staffEmail);
            // The customer messages are not what is under test: nothing leaves the process.
            services.RemoveAll<IServiceContractsCustomerEmail>();
            services.AddSingleton<IServiceContractsCustomerEmail, SilentCustomerEmail>();
        });
        await SeedTenantAsync(factory);
    }

    public async ValueTask DisposeAsync()
    {
        await factory.DisposeAsync();
        await database.DisposeAsync();
    }

    [Fact]
    public async Task ADigestWithOverdueChargesPublishesTheEvent()
    {
        var assignee = await SeedMemberAsync(factory, "owner@example.com", "Owner", "billing");
        var customer = await SeedCustomerAsync(factory, Tenant, "Acme", email: "acme@example.com");
        await SeedProfileAsync(customer, assignee);
        await SeedPendingChargeAsync(customer, Today.AddDays(-4), "Overdue 1");
        await SeedPendingChargeAsync(customer, Today.AddDays(-12), "Overdue 2");
        await SeedPendingChargeAsync(customer, Today.AddDays(3), "Due soon");

        await ProcessAsync();

        var row = Assert.Single(await EventsAsync());
        Assert.Equal($"{Tenant:D}:{assignee:D}:2026-03-10", row.CorrelationId);
        using var payload = JsonDocument.Parse(row.Payload);
        var root = payload.RootElement;
        Assert.Equal(Tenant, root.GetProperty("tenantId").GetGuid());
        Assert.Equal(assignee, root.GetProperty("memberId").GetGuid());
        Assert.Equal("2026-03-10", root.GetProperty("runDate").GetString());
        // Only what is overdue: the charge that is due soon is not counted.
        Assert.Equal(2, root.GetProperty("overdueCount").GetInt32());
        Assert.Equal(NineLocal, root.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal(NineLocal, row.OccurredAt);
        Assert.Single(staffEmail.Sent);
    }

    [Fact]
    public async Task ADigestWithoutOverdueChargesPublishesNothing()
    {
        var assignee = await SeedMemberAsync(factory, "owner@example.com", "Owner", "billing");
        var customer = await SeedCustomerAsync(factory, Tenant, "Acme", email: "acme@example.com");
        await SeedProfileAsync(customer, assignee);
        await SeedPendingChargeAsync(customer, Today.AddDays(3), "Due soon");

        await ProcessAsync();

        // The digest itself goes out (something is due soon), but there is nothing overdue to tell the bell.
        Assert.Single(staffEmail.Sent);
        Assert.Empty(await EventsAsync());
    }

    [Fact]
    public async Task AMemberWithoutEmailStillGetsTheEvent()
    {
        // A member without a user row has no email (no_email), but the bell does not need an address.
        var assignee = await SeedMemberAsync(factory, string.Empty, "Owner", "billing", withUser: false);
        var customer = await SeedCustomerAsync(factory, Tenant, "Acme", email: "acme@example.com");
        await SeedProfileAsync(customer, assignee);
        await SeedPendingChargeAsync(customer, Today.AddDays(-4), "Overdue");

        await ProcessAsync();

        Assert.Empty(staffEmail.Sent);
        Assert.Equal("no_email", Assert.Single(await DigestsAsync()).FailureCode);
        var row = Assert.Single(await EventsAsync());
        using var payload = JsonDocument.Parse(row.Payload);
        Assert.Equal(assignee, payload.RootElement.GetProperty("memberId").GetGuid());
    }

    // D-17: the process died between the claim and the save, so the digest was left Sending. When its lease expires, the next run of the
    // same day claims it again and publishes.
    [Fact]
    public async Task AClaimLeftSendingIsRetakenAndPublishes()
    {
        var assignee = await SeedMemberAsync(factory, "owner@example.com", "Owner", "billing");
        var customer = await SeedCustomerAsync(factory, Tenant, "Acme", email: "acme@example.com");
        await SeedProfileAsync(customer, assignee);
        await SeedPendingChargeAsync(customer, Today.AddDays(-4), "Overdue");
        await WithDbAsync(async db =>
        {
            db.TenantRuns.Add(new TenantRun { TenantId = Tenant, RunDate = Today, ClaimedUntil = NineLocal.AddMinutes(-1) });
            db.DailyDigests.Add(new DailyDigest
            {
                TenantId = Tenant,
                MemberId = assignee,
                RunDate = Today,
                Status = MessageDeliveryStatus.Sending,
                ClaimedUntil = NineLocal.AddMinutes(-6),
            });
            return await db.SaveChangesAsync(Ct);
        });

        await ProcessAsync();

        Assert.Single(await EventsAsync());
        Assert.Single(staffEmail.Sent);
        Assert.Equal(MessageDeliveryStatus.Sent, Assert.Single(await DigestsAsync()).Status);
    }

    // Spec reajuste §6.6: el evento de la campana de revisiones se publica al reclamar el digest, con los dos conteos.
    [Fact]
    public async Task AMemberWithReviewsGetsThePriceReviewEvent()
    {
        var assignee = await SeedMemberAsync(factory, "owner@example.com", "Owner", "billing");
        var customer = await SeedCustomerAsync(factory, Tenant, "Acme", email: "acme@example.com");
        await SeedProfileAsync(customer, assignee);
        await SeedReviewAsync(customer, Today.AddDays(10));
        await SeedReviewAsync(customer, Today.AddDays(2), heldOn: Today);

        await ProcessAsync();

        var row = Assert.Single(await EventsAsync(PriceReviewEventName));
        Assert.Equal($"{Tenant:D}:{assignee:D}:2026-03-10", row.CorrelationId);
        using var payload = JsonDocument.Parse(row.Payload);
        var root = payload.RootElement;
        Assert.Equal(Tenant, root.GetProperty("tenantId").GetGuid());
        Assert.Equal(assignee, root.GetProperty("memberId").GetGuid());
        Assert.Equal("2026-03-10", root.GetProperty("runDate").GetString());
        Assert.Equal(1, root.GetProperty("dueCount").GetInt32());
        Assert.Equal(1, root.GetProperty("heldCount").GetInt32());
        Assert.Equal(NineLocal, root.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal(NineLocal, row.OccurredAt);
        // Nada vencido: la campana de vencidos no suena.
        Assert.Empty(await EventsAsync());
        Assert.Single(staffEmail.Sent);
    }

    [Fact]
    public async Task NoReviewsNoPriceReviewEvent()
    {
        var assignee = await SeedMemberAsync(factory, "owner@example.com", "Owner", "billing");
        var customer = await SeedCustomerAsync(factory, Tenant, "Acme", email: "acme@example.com");
        await SeedProfileAsync(customer, assignee);
        await SeedPendingChargeAsync(customer, Today.AddDays(-4), "Overdue");

        await ProcessAsync();

        Assert.Single(await EventsAsync());
        Assert.Empty(await EventsAsync(PriceReviewEventName));
    }

    private async Task ProcessAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IServiceContractsReminderProcessor>().ProcessTenantAsync(Tenant, Ct);
    }

    private async Task<T> WithDbAsync<T>(Func<ServiceContractsDbContext, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ServiceContractsDbContext>());
    }

    private Task<List<DailyDigest>> DigestsAsync() => WithDbAsync(db => db.DailyDigests.AsNoTracking().ToListAsync(Ct));

    private Task<bool> SeedProfileAsync(Guid customerId, Guid assignee) =>
        WithDbAsync(async db =>
        {
            db.Profiles.Add(CustomerBillingProfile.Create(Tenant, customerId, BillingChannel.Email, assignee, NineLocal));
            await db.SaveChangesAsync(Ct);
            return true;
        });

    /// <summary>A <c>Pending</c> charge in a service of its own that already generated up to its due date: the run adds nothing to it.</summary>
    private Task<bool> SeedPendingChargeAsync(Guid customerId, DateOnly dueDate, string name) =>
        WithDbAsync(async db =>
        {
            var service = ServiceContract.Create(
                ServiceContractId.New(), Tenant, customerId, name, 100000m, "COP", BillingCycle.Monthly, dueDate, NineLocal);
            service.MarkGenerated(dueDate);
            db.Services.Add(service);
            db.Charges.Add(Charge.Generate(service, dueDate, NineLocal));
            await db.SaveChangesAsync(Ct);
            return true;
        });

    /// <summary>
    /// A monthly <c>Ipc</c> contract whose next period is its review. With <paramref name="heldOn"/> it is left held since that day,
    /// as the run of that day would.
    /// </summary>
    private Task<bool> SeedReviewAsync(Guid customerId, DateOnly reviewDate, DateOnly? heldOn = null) =>
        WithDbAsync(async db =>
        {
            var previous = reviewDate.AddMonths(-1);
            var service = ServiceContract.Create(
                ServiceContractId.New(), Tenant, customerId, "Hosting", 100000m, "COP", BillingCycle.Monthly, previous, NineLocal);
            service.MarkGenerated(previous);
            service.InitializePriceReview(AdjustmentMode.Ipc, 0m, reviewDate);
            if (heldOn is { } held)
            {
                ChargeGenerator.Generate(service, null, GenerationMode.Run, held, NineLocal);
                Assert.Equal(held, service.HeldSince);
            }

            db.Services.Add(service);
            await db.SaveChangesAsync(Ct);
            return true;
        });

    private async Task<IReadOnlyList<OutboxRow>> EventsAsync(string eventName = EventName)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT payload::text, correlation_id, occurred_at FROM platform.outbox_messages WHERE event_name = @name ORDER BY occurred_at", connection);
        command.Parameters.AddWithValue("name", eventName);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<OutboxRow>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(new OutboxRow(reader.GetString(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return rows;
    }

    private sealed record OutboxRow(string Payload, string CorrelationId, DateTimeOffset OccurredAt);

    private sealed class FixedTenantClock(DateTimeOffset utcNow) : ITenantClock
    {
        public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new TenantCalendar(utcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota")));
    }

    private sealed class SilentCustomerEmail : IServiceContractsCustomerEmail
    {
        public Task<ServiceContractsSendResult> SendAsync(ServiceContractsCustomerMessage message, CancellationToken cancellationToken) =>
            Task.FromResult<ServiceContractsSendResult>(new ServiceContractsSendResult.Sent());
    }

    private sealed class RecordingStaffEmail : IServiceContractsStaffEmail
    {
        private readonly ConcurrentQueue<ServiceContractsDigestEmail> sent = new();

        public IReadOnlyList<ServiceContractsDigestEmail> Sent => sent.ToArray();

        public Task<ServiceContractsSendResult> SendDigestAsync(ServiceContractsDigestEmail digest, CancellationToken cancellationToken)
        {
            sent.Enqueue(digest);
            return Task.FromResult<ServiceContractsSendResult>(new ServiceContractsSendResult.Sent());
        }
    }
}
