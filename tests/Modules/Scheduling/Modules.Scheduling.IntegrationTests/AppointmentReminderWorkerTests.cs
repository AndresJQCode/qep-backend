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
    private static readonly Guid Template = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class FakeSender : IAppointmentReminderSender
    {
        public List<ReminderMessage> Messages { get; } = [];

        public ReminderDelivery Result { get; set; } = new ReminderDelivery.Sent();

        public Task<ReminderDelivery> SendAsync(ReminderMessage message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return Task.FromResult(Result);
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

        public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            List<TenantModuleKey> keys = [TenantModuleKeys.Customers, TenantModuleKeys.Scheduling];
            if (Messaging)
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

        private Harness(SchedulingDatabase database, Guid memberId, AppointmentId appointmentId, Guid customerId)
        {
            _database = database;
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

        public Guid CustomerId { get; }

        public AppointmentId AppointmentId { get; }

        /// <param name="resourceMember">Con <c>true</c> el miembro está vinculado al recurso; con <c>false</c> sólo aparece como quien creó la cita.</param>
        public static async Task<Harness> StartAsync(bool resourceMember = true)
        {
            var database = await SchedulingDatabase.StartAsync();
            var memberId = Guid.CreateVersion7();
            var customerId = Guid.CreateVersion7();
            var now = Start.AddDays(-5);

            await using var context = database.CreateContext();
            var location = Location.Create(LocationId.New(), Tenant, "Sede Centro", true, now);
            var resource = Resource.Create(ResourceId.New(), Tenant, location.Id, "Dra. Paula Ruiz", resourceMember ? memberId : null, now);
            var service = Service.Create(ServiceId.New(), Tenant, "Consulta general", 30, 0, true, now);
            var session = Session.Create(SessionId.New(), Tenant, resource.Id, service.Id, Start, Start.AddMinutes(30), 0, 1, false, now);
            var settings = SchedulingSettings.Create(Tenant, now);
            settings.Update(ConfirmationMode.Automatic, 60, 30, 12, reminderEnabled: true, 24, Connection, Template, now);
            var appointment = Appointment.Create(
                AppointmentId.New(), Tenant, location.Id, resource.Id, service.Id, customerId, session.Id, blockId: null, Start,
                Start.AddMinutes(30), AppointmentStatus.Confirmed, AppointmentSource.BackOffice, conversationId: null, note: null,
                resourceMember ? null : memberId, now);
            context.AddRange(location, resource, service, session, settings, appointment);
            await context.SaveChangesAsync(Ct);

            var harness = new Harness(database, memberId, appointment.Id, customerId);
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
            return await context.Appointments.AsNoTracking().Include(appointment => appointment.Events).SingleAsync(Ct);
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
        Assert.Equal(AppointmentReminderRules.KeyOf(h.AppointmentId.Value), message.IdempotencyKey);
        Assert.Equal(
            (Tenant, Connection, Template, h.MemberId, h.CustomerId, "Ana Pérez", "+573001234567"),
            (message.TenantId, message.ConnectionId, message.TemplateId, message.SentByMemberId, message.CustomerId, message.CustomerName, message.PhoneE164));
        Assert.Equal(
            ("Consulta general", "Dra. Paula Ruiz", "Sede Centro", Start),
            (message.ServiceName, message.ResourceName, message.LocationName, message.StartsAt));
        Assert.Equal(h.Clock.UtcNow, (await h.LoadAsync()).ReminderSentAt);
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
}
