using Bootstrapper;
using Modules.Messaging.Application;
using Modules.Scheduling.Application;
using Modules.Tenancy.Application;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Plan 2026-11-21, Tarea 12: el adaptador que manda el recordatorio de cita por Mensajería, calcado de
/// <see cref="WholesaleInvitationNoticeSenderTests"/>. Llena las cinco variables propias de la plantilla (las cuatro fijas las pone
/// Mensajería), da la fecha y la hora en la zona del tenant, deriva el <c>clientId</c> de la llave de la cita y traduce el resultado a los
/// códigos que el barredor guarda.
/// </summary>
public sealed class SchedulingReminderSenderTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ConnectionId = Guid.CreateVersion7();
    private static readonly ApprovedTemplate Template = new("qep_agenda_recordatorio_v2", "es", "UTILITY", "NAMED", "[]");

    // 24 de noviembre de 2026, 15:00 UTC = 10:00 en Colombia (UTC-5, sin horario de verano).
    private static readonly DateTimeOffset StartsAt = new(2026, 11, 24, 15, 0, 0, TimeSpan.Zero);

    private readonly StubReadiness _readiness = new();
    private readonly StubOutbound _outbound = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SchedulingReminderSenderTests() => _readiness.Approved[(TenantId, ConnectionId)] = Template;

    [Fact]
    public async Task TheReminderFillsTheFiveVariablesWithTheDateInTheTenantZone()
    {
        var message = Message() with { CustomerName = " Ana\tPérez ", ResourceName = "Dra.\n Paula  Ruiz" };

        var result = await Sender().SendAsync(message, Ct);

        Assert.IsType<ReminderDelivery.Sent>(result);
        var send = Assert.Single(_outbound.Sends);
        Assert.Equal(["fecha_hora", "nombre_cliente", "recurso", "sede", "servicio"], send.Values.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(
            ("Ana Pérez", "Consulta general", "martes 24 de noviembre a las 10:00", "Dra. Paula Ruiz", "Sede Centro"),
            (send.Values["nombre_cliente"], send.Values["servicio"], send.Values["fecha_hora"], send.Values["recurso"], send.Values["sede"]));
        Assert.Equal(
            (TenantId, ConnectionId, message.SentByMemberId, message.CustomerId, "573001234567", "Ana Pérez", Template),
            (send.TenantId, send.ConnectionId, send.SentByMemberId, send.CustomerId, send.WaId, send.RecipientName, send.Template));
    }

    [Fact]
    public async Task TheClientIdIsDerivedFromTheKeyOfTheAppointmentSoARetryNeverSendsTwice()
    {
        var message = Message();

        await Sender().SendAsync(message, Ct);
        await Sender().SendAsync(message, Ct);
        await Sender().SendAsync(Message(), Ct);

        Assert.Equal(_outbound.Sends[0].ClientId, _outbound.Sends[1].ClientId);
        Assert.NotEqual(_outbound.Sends[0].ClientId, _outbound.Sends[2].ClientId);
        Assert.Equal(SchedulingReminderSender.ClientIdOf(message.IdempotencyKey), _outbound.Sends[0].ClientId);
        Assert.NotEqual(Guid.Empty, _outbound.Sends[0].ClientId);
    }

    [Fact]
    public void TheClientIdIsAStableFunctionOfTheKey()
    {
        const string Key = "appointment-reminder:0192a7d0-0000-7000-8000-000000000001";

        Assert.Equal(SchedulingReminderSender.ClientIdOf(Key), SchedulingReminderSender.ClientIdOf(Key));
        Assert.NotEqual(SchedulingReminderSender.ClientIdOf(Key), SchedulingReminderSender.ClientIdOf(Key + "x"));
    }

    [Fact]
    public async Task ATemplateThatIsNotApprovedFailsWithoutSending()
    {
        _readiness.Approved.Clear();

        var result = await Sender().SendAsync(Message(), Ct);

        Assert.Equal("template_not_approved", Assert.IsType<ReminderDelivery.Failed>(result).Code);
        Assert.Empty(_outbound.Sends);
    }

    [Fact]
    public async Task ConnectionUnavailableAndFailedMapToTheFailureCodes()
    {
        Assert.Equal("connection_unavailable:paused", await FailureFor(new AppointmentReminderSendResult.ConnectionUnavailable("paused")));
        Assert.Equal("connection_unavailable:unknown", await FailureFor(new AppointmentReminderSendResult.ConnectionUnavailable(null)));
        Assert.Equal("messaging.message.rejected", await FailureFor(new AppointmentReminderSendResult.Failed("messaging.message.rejected")));
    }

    private async Task<string> FailureFor(AppointmentReminderSendResult outcome)
    {
        _outbound.Next = outcome;
        var result = await Sender().SendAsync(Message(), Ct);
        return Assert.IsType<ReminderDelivery.Failed>(result).Code;
    }

    private SchedulingReminderSender Sender() => new(_outbound, _readiness, new StubClock());

    private static ReminderMessage Message() => new(
        TenantId, Guid.CreateVersion7(), "appointment-reminder:" + Guid.CreateVersion7().ToString("D"), ConnectionId,
        Guid.CreateVersion7(), Guid.CreateVersion7(), "Ana Pérez", "+573001234567", "Consulta general", "Dra. Paula Ruiz", "Sede Centro", StartsAt);

    private sealed class StubClock : ITenantClock
    {
        public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new TenantCalendar(
                StartsAt, TimeZoneInfo.CreateCustomTimeZone("co", TimeSpan.FromHours(-5), "Colombia", "Colombia")));
    }

    private sealed class StubReadiness : IAppointmentReminderReadiness
    {
        public Dictionary<(Guid TenantId, Guid ConnectionId), ApprovedTemplate> Approved { get; } = [];

        public Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult(Approved.GetValueOrDefault((tenantId, connectionId)));

        public Task<AppointmentReminderTemplateStatus?> FindStatusAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The sender only reads the approved row.");
    }

    private sealed class StubOutbound : IAppointmentReminderOutbound
    {
        public List<AppointmentReminderSend> Sends { get; } = [];

        public AppointmentReminderSendResult Next { get; set; } = new AppointmentReminderSendResult.Sent(Guid.CreateVersion7(), Guid.CreateVersion7());

        public Task<AppointmentReminderSendResult> SendAsync(AppointmentReminderSend send, CancellationToken cancellationToken)
        {
            Sends.Add(send);
            return Task.FromResult(Next);
        }
    }
}
