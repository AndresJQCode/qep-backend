using System.Collections.Concurrent;
using BuildingBlocks.Application.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Modules.Notifications.Application;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.HrBirthdays;
using Testcontainers.PostgreSql;
using static Modules.Notifications.IntegrationTests.HrBirthdayTestSupport;
using static Modules.Notifications.IntegrationTests.InAppTestSupport;
using static Modules.Notifications.IntegrationTests.NotificationsDeliveryHarness;

namespace Modules.Notifications.IntegrationTests;

/// <summary>
/// HR birthday notices, spec 2026-11-26 §5.3: the delivery processor against a real database, with doubles for the recipient port, the
/// WhatsApp port and the email channel. <c>RunAsync</c> takes an explicit <c>now</c>, so the stale cutoff and the attempt count are exercised
/// without waiting. There is no presence deferral, no read check and no fallback between channels: each row is its own channel.
/// </summary>
public sealed class HrBirthdayDeliveryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 26, 15, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WhatsAppIsSentOnceWithTheDeliveryIdAsClientId()
    {
        await using var rig = await Rig.StartAsync();
        var row = await rig.SeedAsync(HrBirthdayDeliveryChannels.WhatsApp);

        await rig.RunAsync(T0);
        await rig.RunAsync(T0.AddMinutes(5));

        var send = Assert.Single(rig.WhatsApp.Sends);
        Assert.Equal((rig.TenantId, rig.ConnectionId, rig.Author, row.Id), (send.TenantId, send.ConnectionId, send.SentByMemberId, send.ClientId));
        Assert.Equal(("+573001234567", "Ana Gómez"), (send.PhoneE164, send.RecipientName));
        Assert.Equal(("Ana Pérez", "Luis Gómez", "viernes 27 de noviembre"), (send.Today, send.Upcoming, send.UpcomingDate));
        var state = Assert.Single(await DeliveriesAsync(rig.ConnectionString, rig.TenantId));
        Assert.Equal(("Sent", null, 1), (state.Status, state.ResultCode, state.Attempts));
        Assert.NotNull(state.CompletedAt);
        Assert.Null(state.ClaimedUntil);
        Assert.Empty(rig.Email.Sent);
    }

    [Fact]
    public async Task BlankRecipientNameFallsBackToEquipo()
    {
        await using var rig = await Rig.StartAsync();
        await rig.SeedAsync(HrBirthdayDeliveryChannels.WhatsApp, recipient: rig.Recipient with { DisplayName = " 	 " });

        await rig.RunAsync(T0);

        Assert.Equal("equipo", Assert.Single(rig.WhatsApp.Sends).RecipientName);
    }

    [Fact]
    public async Task NoLeadSendsADashAndNadie()
    {
        await using var rig = await Rig.StartAsync();
        await rig.SeedAsync(
            HrBirthdayDeliveryChannels.WhatsApp,
            payload: PartsPayload([new Person(Guid.CreateVersion7(), "Ana Pérez")], 1, [], 0, null));

        await rig.RunAsync(T0);

        var send = Assert.Single(rig.WhatsApp.Sends);
        Assert.Equal(("Ana Pérez", "nadie", "—"), (send.Today, send.Upcoming, send.UpcomingDate));
    }

    [Fact]
    public async Task EmailIsSent()
    {
        await using var rig = await Rig.StartAsync();
        var row = await rig.SeedAsync(HrBirthdayDeliveryChannels.Email);

        await rig.RunAsync(T0);

        var message = Assert.Single(rig.Email.Sent);
        Assert.Equal(("ana@example.com", "Cumpleaños de hoy en tu equipo"), (message.ToAddress, message.Subject));
        Assert.Contains("Ana Pérez", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("/hr/employees/", message.HtmlBody, StringComparison.Ordinal);
        Assert.Empty(rig.WhatsApp.Sends);
        var state = Assert.Single(await DeliveriesAsync(rig.ConnectionString, rig.TenantId));
        Assert.Equal((row.Id, "Sent"), (state.Id, state.Status));
    }

    [Fact]
    public async Task NoPhoneIsSkipped()
    {
        await using var rig = await Rig.StartAsync();
        await rig.SeedAsync(HrBirthdayDeliveryChannels.WhatsApp, recipient: rig.Recipient with { PhoneE164 = null });

        await rig.RunAsync(T0);

        var state = Assert.Single(await DeliveriesAsync(rig.ConnectionString, rig.TenantId));
        Assert.Equal(("Skipped", "no_phone"), (state.Status, state.ResultCode));
        Assert.Empty(rig.WhatsApp.Sends);
    }

    [Fact]
    public async Task NoEmailIsSkipped()
    {
        await using var rig = await Rig.StartAsync();
        await rig.SeedAsync(HrBirthdayDeliveryChannels.Email, recipient: rig.Recipient with { Email = null });

        await rig.RunAsync(T0);

        var state = Assert.Single(await DeliveriesAsync(rig.ConnectionString, rig.TenantId));
        Assert.Equal(("Skipped", "no_email"), (state.Status, state.ResultCode));
        Assert.Empty(rig.Email.Sent);
    }

    [Fact]
    public async Task InactiveMemberIsSkipped()
    {
        await using var rig = await Rig.StartAsync();
        await rig.SeedAsync(HrBirthdayDeliveryChannels.WhatsApp, recipient: rig.Recipient with { IsActive = false });
        await rig.SeedAsync(HrBirthdayDeliveryChannels.Email, recipient: rig.Recipient with { IsActive = false });

        await rig.RunAsync(T0);

        var rows = await DeliveriesAsync(rig.ConnectionString, rig.TenantId);
        Assert.All(rows, row => Assert.Equal(("Skipped", "member_inactive"), (row.Status, row.ResultCode)));
        Assert.Empty(rig.WhatsApp.Sends);
        Assert.Empty(rig.Email.Sent);
    }

    // Review focus 5: a «Hoy» sent after the run date is over would be wrong, so the cutoff is the expires_at carried by the row.
    [Fact]
    public async Task ExpiredIsSkippedStale()
    {
        await using var rig = await Rig.StartAsync();
        await rig.SeedAsync(HrBirthdayDeliveryChannels.WhatsApp, expiresAt: T0.AddMinutes(-1));
        await rig.SeedAsync(HrBirthdayDeliveryChannels.Email, expiresAt: T0);

        await rig.RunAsync(T0);

        var rows = await DeliveriesAsync(rig.ConnectionString, rig.TenantId);
        Assert.All(rows, row => Assert.Equal(("Skipped", "stale"), (row.Status, row.ResultCode)));
        Assert.Empty(rig.WhatsApp.Sends);
        Assert.Empty(rig.Email.Sent);
        Assert.Equal(0, rig.Recipients.Calls);
    }

    [Fact]
    public async Task FailedWhatsAppDoesNotBlockEmail()
    {
        await using var rig = await Rig.StartAsync();
        rig.WhatsApp.Next = new TaskNoticeWhatsAppResult.Failed("template_not_approved");
        await rig.SeedAsync(HrBirthdayDeliveryChannels.WhatsApp);
        await rig.SeedAsync(HrBirthdayDeliveryChannels.Email);

        await rig.RunAsync(T0);

        var rows = await DeliveriesAsync(rig.ConnectionString, rig.TenantId);
        var whatsapp = Assert.Single(rows, row => row.Channel == "whatsapp");
        var email = Assert.Single(rows, row => row.Channel == "email");
        Assert.Equal(("Failed", "template_not_approved"), (whatsapp.Status, whatsapp.ResultCode));
        Assert.Equal("Sent", email.Status);
        Assert.Single(rig.Email.Sent);
    }

    [Fact]
    public async Task AWhatsAppThatThrowsIsFailedWithItsCodeAndTheEmailGoesOn()
    {
        await using var rig = await Rig.StartAsync();
        rig.WhatsApp.Throws = true;
        await rig.SeedAsync(HrBirthdayDeliveryChannels.WhatsApp);
        await rig.SeedAsync(HrBirthdayDeliveryChannels.Email);

        await rig.RunAsync(T0);

        var rows = await DeliveriesAsync(rig.ConnectionString, rig.TenantId);
        Assert.Equal(("Failed", "send_failed:exception"), (rows.Single(row => row.Channel == "whatsapp").Status, rows.Single(row => row.Channel == "whatsapp").ResultCode));
        Assert.Equal("Sent", rows.Single(row => row.Channel == "email").Status);
    }

    [Fact]
    public async Task AttemptsExhaustedFails()
    {
        await using var rig = await Rig.StartAsync();
        await rig.SeedAsync(HrBirthdayDeliveryChannels.WhatsApp, attempts: HrBirthdayDeliveryProcessor.MaxAttempts);

        await rig.RunAsync(T0);

        var state = Assert.Single(await DeliveriesAsync(rig.ConnectionString, rig.TenantId));
        Assert.Equal(("Failed", "attempts_exhausted", HrBirthdayDeliveryProcessor.MaxAttempts + 1), (state.Status, state.ResultCode, state.Attempts));
        Assert.Empty(rig.WhatsApp.Sends);
    }

    [Fact]
    public async Task ARowWithAnAliveLeaseIsNotTakenAgain()
    {
        await using var rig = await Rig.StartAsync();
        var row = await rig.SeedAsync(HrBirthdayDeliveryChannels.Email);
        await ExecuteAsync(
            rig.ConnectionString,
            "UPDATE notifications.hr_birthday_deliveries SET claimed_until = @until WHERE id = @id",
            ("until", T0.AddMinutes(1)), ("id", row.Id));

        var claimed = await rig.RunAsync(T0);

        Assert.Equal(0, claimed);
        Assert.Empty(rig.Email.Sent);
    }

    private sealed record Seeded(Guid Id, Guid MemberId);

    private sealed class FakeRecipients : ITaskNoticeRecipients
    {
        private int _calls;

        public ConcurrentDictionary<Guid, TaskNoticeRecipient> Members { get; } = new();

        public int Calls => Volatile.Read(ref _calls);

        public Task<TaskNoticeRecipient?> FindAsync(Guid tenantId, Guid memberId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(Members.GetValueOrDefault(memberId));
        }
    }

    private sealed class FakeHrWhatsApp : IHrBirthdayWhatsApp
    {
        private readonly ConcurrentQueue<HrBirthdayWhatsAppSend> _sends = new();

        public IReadOnlyCollection<HrBirthdayWhatsAppSend> Sends => _sends;

        public TaskNoticeWhatsAppResult Next { get; set; } = new TaskNoticeWhatsAppResult.Sent();

        public bool Throws { get; set; }

        public Task<TaskNoticeWhatsAppResult> SendAsync(HrBirthdayWhatsAppSend send, CancellationToken cancellationToken)
        {
            _sends.Enqueue(send);
            return Throws ? throw new InvalidOperationException("Meta is down.") : Task.FromResult(Next);
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly PostgreSqlContainer _database;
        private readonly NotificationsApiFactory _factory;

        private Rig(PostgreSqlContainer database, NotificationsApiFactory factory, Guid tenantId, FakeRecipients recipients, FakeHrWhatsApp whatsApp, RecordingEmailChannel email)
        {
            _database = database;
            _factory = factory;
            TenantId = tenantId;
            Recipients = recipients;
            WhatsApp = whatsApp;
            Email = email;
        }

        public Guid TenantId { get; }

        public FakeRecipients Recipients { get; }

        public FakeHrWhatsApp WhatsApp { get; }

        public RecordingEmailChannel Email { get; }

        public Guid ConnectionId { get; } = Guid.CreateVersion7();

        public Guid Author { get; } = Guid.CreateVersion7();

        public TaskNoticeRecipient Recipient { get; } = new(true, "Ana Gómez", "ana@example.com", "+573001234567");

        public string ConnectionString => _database.GetConnectionString();

        public static async Task<Rig> StartAsync()
        {
            var database = await StartDatabaseAsync();
            var recipients = new FakeRecipients();
            var whatsApp = new FakeHrWhatsApp();
            var email = new RecordingEmailChannel();
            var factory = new NotificationsApiFactory(
                database.GetConnectionString(),
                email,
                configureServices: services =>
                {
                    services.RemoveAll<IRealtimeMemberPublisher>();
                    services.AddSingleton<IRealtimeMemberPublisher>(new RecordingMemberPublisher());
                    services.RemoveAll<ITaskNoticeRecipients>();
                    services.AddSingleton<ITaskNoticeRecipients>(recipients);
                    services.RemoveAll<IHrBirthdayWhatsApp>();
                    services.AddSingleton<IHrBirthdayWhatsApp>(whatsApp);
                    // The hosted worker would claim the rows on its own; the tests drive the processor with their own clock.
                    foreach (var descriptor in services
                        .Where(item => item.ServiceType == typeof(IHostedService) && item.ImplementationType == typeof(HrBirthdayDeliveryWorker))
                        .ToList())
                    {
                        services.Remove(descriptor);
                    }
                });
            var (tenantId, _, _) = await RegisterOwnerAsync(factory);
            return new Rig(database, factory, tenantId, recipients, whatsApp, email);
        }

        public async Task<Seeded> SeedAsync(
            string channel,
            DateTimeOffset? expiresAt = null,
            int attempts = 0,
            string? payload = null,
            TaskNoticeRecipient? recipient = null)
        {
            var member = Guid.CreateVersion7();
            var id = await SeedDeliveryAsync(
                ConnectionString,
                TenantId,
                member,
                channel,
                channel == "whatsapp" ? ConnectionId : null,
                Author,
                payload ?? PartsPayload(
                    [new Person(Guid.CreateVersion7(), "Ana Pérez")], 1, [new Person(Guid.CreateVersion7(), "Luis Gómez")], 1, "2026-11-27"),
                expiresAt ?? T0.AddHours(10),
                attempts: attempts);
            Recipients.Members[member] = recipient ?? Recipient;
            return new Seeded(id, member);
        }

        public async Task<int> RunAsync(DateTimeOffset now)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<HrBirthdayDeliveryProcessor>().RunAsync(now, Ct);
        }

        public async ValueTask DisposeAsync()
        {
            _factory.Dispose();
            await _database.DisposeAsync();
        }
    }
}
