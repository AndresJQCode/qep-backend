using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Scheduling.Application;
using Modules.Scheduling.Domain;
using Modules.Scheduling.Infrastructure.Persistence;
using Modules.Scheduling.Infrastructure.Reminders;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Scheduling.IntegrationTests;

/// <summary>
/// Plan 2026-11-21, Tarea 12 y Review Focus 7: el barredor del recordatorio de cita. Corre el procesador real contra una base migrada y
/// sustituye sólo los puertos de afuera (el envío por Mensajería, el cliente, las membresías, los módulos del tenant y el reloj). Una cita
/// <c>Confirmed</c> dentro de su ventana recibe <b>un</b> recordatorio, también con dos pasadas; una cancelada, una que ya empezó o la de
/// un tenant sin Mensajería no lo reciben; un fallo reintenta con tope y lo que se omite lo dice el historial de la cita.
/// </summary>
public sealed class AppointmentReminderWorkerTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 24, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Connection = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class FakeSender : IAppointmentReminderSender
    {
        public List<ReminderMessage> Messages { get; } = [];

        public ReminderDelivery Result { get; set; } = new ReminderDelivery.Sent();

        /// <summary>Runs once, inside the next send and before it answers: the way a test changes the world while the pass is busy.</summary>
        public Func<Task>? OnNextSend { get; set; }

        public async Task<ReminderDelivery> SendAsync(ReminderMessage message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            if (OnNextSend is { } hook)
            {
                OnNextSend = null;
                await hook();
            }

            return Result;
        }
    }

    private sealed class FakeRecipients : ISchedulingReminderRecipients
    {
        public int Calls { get; private set; }

        public ReminderRecipient? Recipient { get; set; }

        public Task<ReminderRecipient?> FindAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Recipient is null ? null : Recipient with { CustomerId = customerId });
        }
    }

    private sealed class FakeModules : ITenantModules
    {
        public bool Messaging { get; set; } = true;

        /// <summary>Tenants whose Messaging is off while it stays on for the rest.</summary>
        public HashSet<Guid> WithoutMessaging { get; } = [];

        public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            List<TenantModuleKey> keys = [TenantModuleKeys.Customers, TenantModuleKeys.Scheduling];
            if (Messaging && !WithoutMessaging.Contains(tenantId))
            {
                keys.Add(TenantModuleKeys.Messaging);
            }

            return Task.FromResult<TenantModuleSet?>(TenantModuleSet.FromStored(keys));
        }
    }

    private sealed class FakeMembers : ISchedulingMemberDirectory
    {
        public HashSet<Guid> Active { get; } = [];

        public Task<IReadOnlyList<SchedulingMemberRef>> FindAsync(
            Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SchedulingMemberRef>>(
                [.. memberIds.Distinct().Select(id => new SchedulingMemberRef(id, "Miembro", Active.Contains(id)))]);

        public Task<SchedulingMemberRef?> FindBySubjectAsync(Guid tenantId, Guid subjectId, CancellationToken cancellationToken) =>
            Task.FromResult<SchedulingMemberRef?>(null);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SchedulingDatabase _database;
        private ServiceProvider _provider = null!;

        private Harness(SchedulingDatabase database, Guid memberId, AppointmentId appointmentId, Guid customerId, Guid? defaultSenderId)
        {
            _database = database;
            DefaultSenderId = defaultSenderId;
            MemberId = memberId;
            AppointmentId = appointmentId;
            CustomerId = customerId;
        }

        public FakeClock Clock { get; } = new();

        public FakeSender Sender { get; } = new();

        public FakeRecipients Recipients { get; } = new();

        public FakeModules Modules { get; } = new();

        public FakeMembers Members { get; } = new();

        public Guid MemberId { get; }

        /// <summary>The tenant's default sender (<c>reminderSenderMemberId</c>), when the harness was started with one.</summary>
        public Guid? DefaultSenderId { get; }

        public Guid CustomerId { get; }

        public AppointmentId AppointmentId { get; }

        /// <param name="resourceMember">Con <c>true</c> el miembro está vinculado al recurso; con <c>false</c> sólo aparece como quien creó la cita.</param>
        /// <param name="historyActor">Con <c>false</c> (y sin miembro en el recurso) la cita no tiene actor humano: nace por el canal.</param>
        /// <param name="defaultSender">Con <c>true</c> las reglas llevan un remitente por defecto, que queda activo salvo que la prueba lo quite.</param>
        public static async Task<Harness> StartAsync(bool resourceMember = true, bool historyActor = true, bool defaultSender = false)
        {
            var database = await SchedulingDatabase.StartAsync();
            var memberId = Guid.CreateVersion7();
            var customerId = Guid.CreateVersion7();
            Guid? defaultSenderId = defaultSender ? Guid.CreateVersion7() : null;
            var now = Start.AddDays(-5);

            await using var context = database.CreateContext();
            var location = Location.Create(LocationId.New(), Tenant, "Sede Centro", true, now);
            var resource = Resource.Create(ResourceId.New(), Tenant, location.Id, "Dra. Paula Ruiz", resourceMember ? memberId : null, now);
            var service = Service.Create(ServiceId.New(), Tenant, "Consulta general", 30, 0, true, now);
            var session = Session.Create(SessionId.New(), Tenant, resource.Id, service.Id, Start, Start.AddMinutes(30), 0, 1, false, now);
            var settings = SchedulingSettings.Create(Tenant, now);
            settings.Update(ConfirmationMode.Automatic, 60, 30, 12, reminderEnabled: true, 24, Connection, defaultSenderId, now);
            var appointment = Appointment.Create(
                AppointmentId.New(), Tenant, location.Id, resource.Id, service.Id, customerId, session.Id, blockId: null, Start,
                Start.AddMinutes(30), AppointmentStatus.Confirmed, AppointmentSource.BackOffice, conversationId: null, note: null,
                resourceMember || !historyActor ? null : memberId, now);
            context.AddRange(location, resource, service, session, settings, appointment);
            await context.SaveChangesAsync(Ct);

            var harness = new Harness(database, memberId, appointment.Id, customerId, defaultSenderId);
            harness.Wire();
            return harness;
        }

        private void Wire()
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => _database.CreateContext());
            services.AddSingleton<IClock>(Clock);
            services.AddSingleton<IAppointmentReminderSender>(Sender);
            services.AddSingleton<ISchedulingReminderRecipients>(Recipients);
            services.AddSingleton<ITenantModules>(Modules);
            services.AddSingleton<ISchedulingMemberDirectory>(Members);
            _provider = services.BuildServiceProvider();
            Members.Active.Add(MemberId);
            if (DefaultSenderId is { } defaultSender)
            {
                Members.Active.Add(defaultSender);
            }

            Recipients.Recipient = new ReminderRecipient(CustomerId, "Ana Pérez", "+573001234567", IsActive: true, ConsentRevoked: false, MarketingOptOut: false);
            // Faltan 20 h: dentro de la ventana de 24 h.
            Clock.UtcNow = Start.AddHours(-20);
        }

        public Task<int> PassAsync() =>
            new AppointmentReminderProcessor(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AppointmentReminderProcessor>.Instance)
                .RunOnceAsync(Ct);

        public SchedulingDbContext NewContext() => _database.CreateContext();

        public async Task<Appointment> LoadAsync()
        {
            await using var context = _database.CreateContext();
            return await context.Appointments.AsNoTracking().Include(appointment => appointment.Events).SingleAsync(value => value.Id == AppointmentId, Ct);
        }

        /// <summary>
        /// Another tenant with the reminder on and <paramref name="count"/> confirmed appointments inside its window that start <b>before</b>
        /// the harness appointment, so they fill the whole batch of a pass if nobody filters them out.
        /// </summary>
        public async Task SeedCrowdedTenantAsync(Guid tenantId, int count)
        {
            await using var context = _database.CreateContext();
            var now = Start.AddDays(-5);
            var location = Location.Create(LocationId.New(), tenantId, "Sede Norte", true, now);
            var resource = Resource.Create(ResourceId.New(), tenantId, location.Id, "Sala 1", null, now);
            var service = Service.Create(ServiceId.New(), tenantId, "Control", 5, 0, true, now);
            var settings = SchedulingSettings.Create(tenantId, now);
            settings.Update(ConfirmationMode.Automatic, 60, 30, 12, reminderEnabled: true, 24, Connection, null, now);
            context.AddRange(location, resource, service, settings);
            for (var index = 0; index < count; index++)
            {
                // Five-minute appointments one after the other from 19 h before the harness one: all inside the window, none overlapping.
                var start = Start.AddHours(-19).AddMinutes(5 * index);
                var session = Session.Create(SessionId.New(), tenantId, resource.Id, service.Id, start, start.AddMinutes(5), 0, 1, false, now);
                var appointment = Appointment.Create(
                    AppointmentId.New(), tenantId, location.Id, resource.Id, service.Id, Guid.CreateVersion7(), session.Id, blockId: null, start,
                    start.AddMinutes(5), AppointmentStatus.Confirmed, AppointmentSource.BackOffice, conversationId: null, note: null, null, now);
                context.AddRange(session, appointment);
            }

            await context.SaveChangesAsync(Ct);
        }

        public async Task<int> UntouchedAppointmentsOfAsync(Guid tenantId)
        {
            await using var context = _database.CreateContext();
            return await context.Appointments.AsNoTracking().CountAsync(value => value.TenantId == tenantId && value.ReminderAttempts == 0, Ct);
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            await _database.DisposeAsync();
        }
    }

    [Fact]
    public async Task TwoPassesSendOneReminder()
    {
        await using var h = await Harness.StartAsync();

        await h.PassAsync();
        await h.PassAsync();

        var message = Assert.Single(h.Sender.Messages);
        Assert.Equal(AppointmentReminderRules.KeyOf(h.AppointmentId.Value, Start), message.IdempotencyKey);
        Assert.Equal(
            (Tenant, Connection, h.MemberId, h.CustomerId, "Ana Pérez", "+573001234567"),
            (message.TenantId, message.ConnectionId, message.SentByMemberId, message.CustomerId, message.CustomerName, message.PhoneE164));
        Assert.Equal(
            ("Consulta general", "Dra. Paula Ruiz", "Sede Centro", Start),
            (message.ServiceName, message.ResourceName, message.LocationName, message.StartsAt));
        Assert.Equal(h.Clock.UtcNow, (await h.LoadAsync()).ReminderSentAt);
    }

    // Written, not run. The batch is 50 rows ordered by start across every tenant: 60 earlier appointments of a tenant without Messaging must
    // not keep the appointment of the tenant that has it out of the batch on every pass.
    [Fact]
    public async Task AppointmentsOfATenantWithoutMessagingDoNotStarveTheOthers()
    {
        await using var h = await Harness.StartAsync();
        var crowded = Guid.CreateVersion7();
        await h.SeedCrowdedTenantAsync(crowded, AppointmentReminderRules.BatchSize + 10);
        h.Modules.WithoutMessaging.Add(crowded);

        await h.PassAsync();

        Assert.Equal(h.CustomerId, Assert.Single(h.Sender.Messages).CustomerId);
        Assert.Equal(h.Clock.UtcNow, (await h.LoadAsync()).ReminderSentAt);
        // Nothing of the other tenant was claimed: turning Messaging on later finds those appointments intact.
        Assert.Equal(AppointmentReminderRules.BatchSize + 10, await h.UntouchedAppointmentsOfAsync(crowded));
    }

    // Written, not run. Recording the reminder is a system write: the version the open screens hold stays valid.
    [Fact]
    public async Task RecordingTheReminderDoesNotBumpTheVersion()
    {
        await using var h = await Harness.StartAsync();
        var before = (await h.LoadAsync()).Version;

        await h.PassAsync();

        var after = await h.LoadAsync();
        Assert.NotNull(after.ReminderSentAt);
        Assert.Equal(before, after.Version);
    }

    // Written, not run. The sender does not use reminder_template_id (it sends the approved template of the connection), so a rules row
    // without it must not hide the appointment from the sweeper.
    [Fact]
    public async Task TheTemplateIdOfTheRulesIsNotRequiredToRemind()
    {
        await using var h = await Harness.StartAsync();
        await using (var context = h.NewContext())
        {
            await context.Database.ExecuteSqlAsync($"UPDATE scheduling.settings SET reminder_template_id = NULL WHERE tenant_id = {Tenant}", Ct);
        }

        await h.PassAsync();

        Assert.Single(h.Sender.Messages);
        Assert.NotNull((await h.LoadAsync()).ReminderSentAt);
    }

    // Written, not run. The last attempt sends and then loses a race against another write to the appointment (its version moved): the
    // pass reads the appointment again, sends with the same key and records it. It must not end as send_failed nor without any mark.
    [Fact]
    public async Task TheLastAttemptThatLosesARaceWhileRecordingStillRecordsTheSend()
    {
        await using var h = await Harness.StartAsync();
        h.Sender.Result = new ReminderDelivery.Failed("connection_unavailable:paused");
        await h.PassAsync();
        h.Clock.UtcNow = h.Clock.UtcNow.Add(AppointmentReminderRules.Leases[0]).AddSeconds(1);
        await h.PassAsync();
        h.Clock.UtcNow = h.Clock.UtcNow.Add(AppointmentReminderRules.Leases[1]).AddSeconds(1);
        h.Sender.Result = new ReminderDelivery.Sent();
        h.Sender.OnNextSend = async () =>
        {
            await using var context = h.NewContext();
            await context.Database.ExecuteSqlAsync($"UPDATE scheduling.appointments SET version = version + 1", Ct);
        };

        await h.PassAsync();

        var appointment = await h.LoadAsync();
        Assert.Equal(AppointmentReminderRules.MaxAttempts, appointment.ReminderAttempts);
        Assert.NotNull(appointment.ReminderSentAt);
        Assert.Null(appointment.ReminderSkippedReason);
        Assert.Equal(AppointmentAction.ReminderSent, appointment.Events.OrderBy(value => value.At).Last().Action);
        // The same key every time: Messaging answers a repeat with the message it already has.
        Assert.Single(h.Sender.Messages.Select(message => message.IdempotencyKey).Distinct());
    }

    [Fact]
    public async Task ACancelledAppointmentIsNotReminded()
    {
        await using var h = await Harness.StartAsync();
        // El primer intento falla y deja el reclamo vivo: la cita se cancela mientras espera su reintento.
        h.Sender.Result = new ReminderDelivery.Failed("connection_unavailable:paused");
        await h.PassAsync();
        Assert.Single(h.Sender.Messages);
        await using (var context = h.NewContext())
        {
            var appointment = await context.Appointments.Include(value => value.Events).SingleAsync(Ct);
            appointment.Cancel("El cliente avisó", h.MemberId, h.Clock.UtcNow);
            await context.SaveChangesAsync(Ct);
        }

        h.Sender.Result = new ReminderDelivery.Sent();
        h.Clock.UtcNow = h.Clock.UtcNow.AddHours(1);
        await h.PassAsync();

        Assert.Single(h.Sender.Messages);
        Assert.Null((await h.LoadAsync()).ReminderSentAt);
    }

    // Plan, correction 49: rescheduling clears the reminder and the new time is reminded again, with a key Messaging does not deduplicate.
    [Fact]
    public async Task ARescheduledAppointmentIsRemindedAgainForTheNewTimeWithANewKey()
    {
        await using var h = await Harness.StartAsync();
        await h.PassAsync();
        Assert.NotNull((await h.LoadAsync()).ReminderSentAt);

        var newStart = Start.AddDays(2);
        await using (var context = h.NewContext())
        {
            var appointment = await context.Appointments.Include(value => value.Events).SingleAsync(Ct);
            appointment.Reschedule(appointment.ResourceId, appointment.SessionId, newStart, newStart.AddMinutes(30), h.MemberId, h.Clock.UtcNow);
            await context.SaveChangesAsync(Ct);
        }

        Assert.Null((await h.LoadAsync()).ReminderSentAt);
        await h.PassAsync();
        Assert.Single(h.Sender.Messages);
        h.Clock.UtcNow = newStart.AddHours(-20);
        await h.PassAsync();

        Assert.Equal(2, h.Sender.Messages.Count);
        Assert.Equal(AppointmentReminderRules.KeyOf(h.AppointmentId.Value, newStart), h.Sender.Messages[1].IdempotencyKey);
        Assert.NotEqual(h.Sender.Messages[0].IdempotencyKey, h.Sender.Messages[1].IdempotencyKey);
        Assert.NotNull((await h.LoadAsync()).ReminderSentAt);
    }

    [Fact]
    public async Task AnAppointmentThatAlreadyStartedIsNotReminded()
    {
        await using var h = await Harness.StartAsync();
        h.Clock.UtcNow = Start.AddMinutes(1);

        await h.PassAsync();

        Assert.Empty(h.Sender.Messages);
        Assert.Equal(0, (await h.LoadAsync()).ReminderAttempts);
    }

    [Fact]
    public async Task AnAppointmentOutsideTheWindowIsNotRemindedYet()
    {
        await using var h = await Harness.StartAsync();
        h.Clock.UtcNow = Start.AddHours(-30);

        await h.PassAsync();
        Assert.Empty(h.Sender.Messages);

        // Entra a la ventana cuando faltan 24 h exactas.
        h.Clock.UtcNow = Start.AddHours(-24);
        await h.PassAsync();
        Assert.Single(h.Sender.Messages);
    }

    [Fact]
    public async Task ATenantWithoutMessagingNeverReminds()
    {
        await using var h = await Harness.StartAsync();
        h.Modules.Messaging = false;

        await h.PassAsync();

        Assert.Empty(h.Sender.Messages);
        // No se reclama: encender Mensajería después no encuentra la cita gastada.
        Assert.Equal(0, (await h.LoadAsync()).ReminderAttempts);
    }

    [Fact]
    public async Task ACustomerWithoutAPhoneIsSkippedWithAReason()
    {
        await using var h = await Harness.StartAsync();
        h.Recipients.Recipient = h.Recipients.Recipient! with { PhoneE164 = null };

        await h.PassAsync();
        await h.PassAsync();

        Assert.Empty(h.Sender.Messages);
        var appointment = await h.LoadAsync();
        Assert.Equal("no_phone", appointment.ReminderSkippedReason);
        var entry = appointment.Events.OrderBy(value => value.At).Last();
        Assert.Equal((AppointmentAction.ReminderSkipped, "no_phone"), (entry.Action, entry.Detail));
        // Lo omitido no se vuelve a evaluar en cada pasada.
        Assert.Equal(1, h.Recipients.Calls);
    }

    [Fact]
    public async Task ARevokedCustomerIsNotReminded()
    {
        await using var h = await Harness.StartAsync();
        h.Recipients.Recipient = h.Recipients.Recipient! with { ConsentRevoked = true };

        await h.PassAsync();

        Assert.Empty(h.Sender.Messages);
        Assert.Equal("consent_revoked", (await h.LoadAsync()).ReminderSkippedReason);
    }

    [Fact]
    public async Task AMarketingOptOutCustomerIsNotReminded()
    {
        await using var h = await Harness.StartAsync();
        h.Recipients.Recipient = h.Recipients.Recipient! with { MarketingOptOut = true };

        await h.PassAsync();

        Assert.Empty(h.Sender.Messages);
        Assert.Equal("marketing_opt_out", (await h.LoadAsync()).ReminderSkippedReason);
    }

    [Fact]
    public async Task AFailureRetriesUpToTheCap()
    {
        await using var h = await Harness.StartAsync();
        h.Sender.Result = new ReminderDelivery.Failed("template_not_approved");

        await h.PassAsync();
        // Dentro del reclamo del primer intento no se vuelve a intentar.
        await h.PassAsync();
        Assert.Single(h.Sender.Messages);

        for (var attempt = 2; attempt <= AppointmentReminderRules.MaxAttempts; attempt++)
        {
            h.Clock.UtcNow = h.Clock.UtcNow.Add(AppointmentReminderRules.Leases[attempt - 2]).AddSeconds(1);
            await h.PassAsync();
            Assert.Equal(attempt, h.Sender.Messages.Count);
        }

        var appointment = await h.LoadAsync();
        Assert.Equal((AppointmentReminderRules.MaxAttempts, "template_not_approved", "send_failed:template_not_approved"),
            (appointment.ReminderAttempts, appointment.ReminderLastError, appointment.ReminderSkippedReason));
        Assert.Null(appointment.ReminderSentAt);
        Assert.Equal(AppointmentAction.ReminderSkipped, appointment.Events.OrderBy(value => value.At).Last().Action);

        // Pasado el tope no vuelve a salir, por mucho que pase el tiempo.
        h.Clock.UtcNow = h.Clock.UtcNow.AddHours(2);
        await h.PassAsync();
        Assert.Equal(AppointmentReminderRules.MaxAttempts, h.Sender.Messages.Count);
    }

    [Fact]
    public async Task TheReminderLeavesAHistoryEntry()
    {
        await using var h = await Harness.StartAsync();

        await h.PassAsync();

        var appointment = await h.LoadAsync();
        var entry = appointment.Events.OrderBy(value => value.At).Last();
        Assert.Equal((AppointmentAction.ReminderSent, h.Clock.UtcNow), (entry.Action, entry.At));
        Assert.Null(entry.ActorMemberId);
        Assert.Null(appointment.ReminderSkippedReason);
    }

    // La fila de Mensajería exige un autor: el miembro del recurso; sin él, el último que tocó la cita; y sin ninguno activo, se omite.
    [Fact]
    public async Task WithoutAResourceMemberTheLastActorOfTheHistorySigns()
    {
        await using var h = await Harness.StartAsync(resourceMember: false);

        await h.PassAsync();

        Assert.Equal(h.MemberId, Assert.Single(h.Sender.Messages).SentByMemberId);
    }

    [Fact]
    public async Task WithoutAnActiveMemberTheReminderIsSkippedWithAReason()
    {
        await using var h = await Harness.StartAsync();
        h.Members.Active.Clear();

        await h.PassAsync();

        Assert.Empty(h.Sender.Messages);
        Assert.Equal("no_sender_member", (await h.LoadAsync()).ReminderSkippedReason);
    }

    // D-18: el remitente por defecto de las reglas entra último a los candidatos.
    [Fact]
    public async Task WithoutResourceMemberOrActorTheDefaultSenderSends()
    {
        await using var h = await Harness.StartAsync(resourceMember: false, historyActor: false, defaultSender: true);

        await h.PassAsync();

        Assert.Equal(h.DefaultSenderId, Assert.Single(h.Sender.Messages).SentByMemberId);
        Assert.Null((await h.LoadAsync()).ReminderSkippedReason);
    }

    [Fact]
    public async Task TheResourceMemberWinsOverTheDefaultSender()
    {
        await using var h = await Harness.StartAsync(defaultSender: true);

        await h.PassAsync();

        Assert.Equal(h.MemberId, Assert.Single(h.Sender.Messages).SentByMemberId);
    }

    [Fact]
    public async Task ASuspendedDefaultSenderStillSkipsWithNoSenderMember()
    {
        await using var h = await Harness.StartAsync(resourceMember: false, historyActor: false, defaultSender: true);
        h.Members.Active.Remove(h.DefaultSenderId!.Value);

        await h.PassAsync();

        Assert.Empty(h.Sender.Messages);
        Assert.Equal("no_sender_member", (await h.LoadAsync()).ReminderSkippedReason);
    }
}
