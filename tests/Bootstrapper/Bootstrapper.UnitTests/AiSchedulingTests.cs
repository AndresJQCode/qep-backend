using System.Reflection;
using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Ai.Application;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Scheduling.Application;
using Modules.Scheduling.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-11-23 §7.2, §7.6 y §8: el adaptador de la Agenda para el agente. Traduce las excepciones de Scheduling a los códigos que lee el
/// modelo y resuelve el cliente de la conversación (releyéndola en cada llamada, D-9); lo de Scheduling mismo lo prueban sus pruebas.
/// </summary>
public sealed class AiSchedulingTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid AgentId = Guid.CreateVersion7();
    private static readonly Guid JobId = Guid.CreateVersion7();
    private static readonly Guid ConversationId = Guid.CreateVersion7();
    private static readonly Guid CustomerA = Guid.CreateVersion7();
    private static readonly Guid CustomerB = Guid.CreateVersion7();
    private static readonly Guid ServiceId = Guid.CreateVersion7();
    private static readonly Guid AppointmentId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Start = new(2026, 11, 24, 9, 0, 0, DateTimeKind.Unspecified);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeChannelScheduling _channel = new();
    private readonly List<string> _resolverCalls = [];
    private ConversationRow? _row = Row(CustomerA);

    private static AiSchedulingCaller Caller(Guid? conversation = null, Guid? customer = null, bool isTest = false) =>
        new(TenantId, AgentId, JobId, conversation, customer, isTest);

    private static ConversationRow Row(Guid? customerId, string? userId = "CO.laura1") => new(
        ConversationId, TenantId, Guid.CreateVersion7(), userId, "573001234567", null, "Laura", customerId, null,
        ConversationStatus.Open, 0, Now, null, null, null, null, null, null, Now, 1);

    private AiScheduling Adapter(Guid? ensuredCustomer = null)
    {
        var conversations = Stub<IConversationQueries>((method, _) => method.Name == nameof(IConversationQueries.FindAsync)
            ? Task.FromResult(_row)
            : throw new NotSupportedException(method.Name));
        var repository = Stub<IConversationRepository>((method, _) =>
        {
            _resolverCalls.Add(method.Name);
            return method.Name == nameof(IConversationRepository.LinkCustomerAsync)
                ? Task.FromResult<Guid?>(ensuredCustomer)
                : throw new NotSupportedException(method.Name);
        });
        var directory = Stub<IMessagingCustomerDirectory>((method, _) =>
        {
            _resolverCalls.Add(method.Name);
            return method.Name == nameof(IMessagingCustomerDirectory.EnsureAsync)
                ? Task.FromResult(new MessagingEnsuredCustomer(ensuredCustomer ?? Guid.CreateVersion7(), true))
                : throw new NotSupportedException(method.Name);
        });
        var notifier = Stub<IMessagingNotifier>((method, _) => method.Name == nameof(IMessagingNotifier.ConversationChangedAsync)
            ? Task.CompletedTask
            : throw new NotSupportedException(method.Name));
        var clock = Stub<IClock>((_, _) => Now);
        var resolver = new ConversationCustomerResolver(repository, directory, notifier, clock, NullLogger<ConversationCustomerResolver>.Instance);
        return new AiScheduling(_channel, conversations, resolver);
    }

    [Fact]
    public async Task TranslatesEachSchedulingFailure()
    {
        (Exception Thrown, string Code, string? Handoff)[] cases =
        [
            (new RequestForbiddenException("tenancy.module_not_enabled", "x"), "scheduling_unavailable", null),
            (new ResourceConflictException(SchedulingErrorCodes.SlotTaken, "x"), "slot_taken", null),
            (new ResourceConflictException(SchedulingErrorCodes.SessionFull, "x"), "session_full", null),
            (new SchedulingDomainException(SchedulingErrorCodes.OutsideRules, "x"), "outside_rules", null),
            (new SchedulingDomainException(SchedulingErrorCodes.InvalidTransition, "x"), "appointment_change_blocked", "appointment_change_blocked"),
            (new SchedulingDomainException(SchedulingErrorCodes.ResourceNotChoosable, "x"), "resource_not_choosable", null),
            (new SchedulingDomainException(SchedulingErrorCodes.BeforeStart, "x"), "tool_failed", null),
            (new ResourceNotFoundException(SchedulingErrorCodes.AppointmentNotFound, "x"), "appointment_not_found", null),
            (new ResourceNotFoundException(SchedulingErrorCodes.ServiceNotFound, "x"), "service_not_found", null),
            (new ResourceNotFoundException(SchedulingErrorCodes.ResourceNotFound, "x"), "resource_not_choosable", null),
            (new ValidationException([new ValidationFailure("clientRequestId", "x")]), "scheduling_unavailable", null),
            (new ValidationException([new ValidationFailure("serviceId", "x")]), "service_not_found", null),
            (new ValidationException([new ValidationFailure("resourceId", "x")]), "resource_not_choosable", null),
            (new ValidationException([new ValidationFailure("customerId", "x")]), "customer_unknown", "appointment_customer_unavailable"),
            (new RequestConcurrencyException("scheduling.appointment.changed", "x"), "appointment_changed", null),
        ];

        foreach (var (thrown, code, handoff) in cases)
        {
            _channel.Throws = thrown;

            var result = await Adapter().BookAsync(Caller(ConversationId), ServiceId, Start, null, null, "ai:k", Ct);

            Assert.Equal((code, handoff), (result.ErrorCode, result.HandoffCode));
            Assert.Null(result.Value);
        }
    }

    [Fact]
    public async Task AnUnknownFailureGoesUpForTheLoopToTurnIntoToolFailed()
    {
        _channel.Throws = new InvalidOperationException("boom");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Adapter().BookAsync(Caller(ConversationId), ServiceId, Start, null, null, "ai:k", Ct));
    }

    [Fact]
    public async Task RereadsTheConversationCustomerOnEveryCall()
    {
        var adapter = Adapter();

        await adapter.ListMyAppointmentsAsync(Caller(ConversationId, customer: CustomerA), Ct);
        _row = Row(CustomerB);
        await adapter.ListMyAppointmentsAsync(Caller(ConversationId, customer: CustomerA), Ct);

        Assert.Equal([CustomerA, CustomerB], _channel.ListedFor);
    }

    [Fact]
    public async Task BookCreatesAndLinksTheCustomerWhenMissing()
    {
        _row = Row(customerId: null);
        var created = Guid.CreateVersion7();

        var result = await Adapter(created).BookAsync(Caller(ConversationId), ServiceId, Start, null, null, "ai:k", Ct);

        Assert.NotNull(result.Value);
        Assert.Equal(
            [nameof(IMessagingCustomerDirectory.EnsureAsync), nameof(IConversationRepository.LinkCustomerAsync)], _resolverCalls);
        Assert.Equal(created, _channel.LastBooking?.CustomerId);
    }

    [Fact]
    public async Task BookWithoutChannelIdentityHandsOff()
    {
        _row = Row(customerId: null, userId: null);

        var result = await Adapter().BookAsync(Caller(ConversationId), ServiceId, Start, null, null, "ai:k", Ct);

        Assert.Equal(("customer_unknown", "appointment_customer_unavailable"), (result.ErrorCode, result.HandoffCode));
        Assert.Null(_channel.LastBooking);
    }

    [Fact]
    public async Task BookForAMissingConversationHandsOff()
    {
        _row = null;

        var result = await Adapter().BookAsync(Caller(ConversationId), ServiceId, Start, null, null, "ai:k", Ct);

        Assert.Equal("appointment_customer_unavailable", result.HandoffCode);
    }

    [Fact]
    public async Task ListWithoutCustomerIsEmpty()
    {
        _row = Row(customerId: null);

        var result = await Adapter().ListMyAppointmentsAsync(Caller(ConversationId), Ct);

        Assert.Empty(result.Value!.Appointments);
        Assert.Equal("America/Bogota", result.Value.TimeZone);
        Assert.Empty(_channel.ListedFor);
    }

    [Fact]
    public async Task PreviewRescheduleChecksTheMoveForTheConversationCustomer()
    {
        var result = await Adapter().PreviewRescheduleAsync(Caller(ConversationId), AppointmentId, Start, null, Ct);

        Assert.False(result.Value!.AlreadyDone);
        Assert.Equal((CustomerA, AppointmentId), _channel.LastRescheduleCheck);
        Assert.Equal(0, _channel.WriteCalls);
    }

    [Fact]
    public async Task CancelWithoutCustomerIsNotFound()
    {
        _row = Row(customerId: null);
        var adapter = Adapter();

        var cancel = await adapter.CancelAsync(Caller(ConversationId), AppointmentId, null, Ct);
        var move = await adapter.RescheduleAsync(Caller(ConversationId), AppointmentId, Start, null, Ct);
        var find = await adapter.FindMyAppointmentAsync(Caller(ConversationId), AppointmentId, Ct);
        var preview = await adapter.PreviewRescheduleAsync(Caller(ConversationId), AppointmentId, Start, null, Ct);

        Assert.All([cancel.ErrorCode, move.ErrorCode, find.ErrorCode, preview.ErrorCode], code => Assert.Equal("appointment_not_found", code));
        Assert.Equal(0, _channel.WriteCalls);
        Assert.Null(_channel.LastRescheduleCheck);
    }

    [Fact]
    public async Task ThePlaygroundUsesTheTestCustomerAndNeverTheConversation()
    {
        var queried = false;
        var conversations = Stub<IConversationQueries>((_, _) =>
        {
            queried = true;
            return Task.FromResult<ConversationRow?>(null);
        });
        var adapter = new AiScheduling(_channel, conversations, null!);

        await adapter.ListMyAppointmentsAsync(Caller(conversation: null, customer: CustomerB, isTest: true), Ct);

        Assert.False(queried);
        Assert.Equal([CustomerB], _channel.ListedFor);
    }

    [Fact]
    public async Task BookPassesAgentConversationAndKey()
    {
        await Adapter().BookAsync(Caller(ConversationId), ServiceId, Start, null, "Nota.", $"ai:{JobId:N}:book:2", Ct);

        var booking = Assert.IsType<ChannelBooking>(_channel.LastBooking);
        Assert.Equal((TenantId, AgentId, CustomerA, ConversationId), (booking.TenantId, booking.AgentId, booking.CustomerId, booking.ConversationId));
        Assert.Equal(($"ai:{JobId:N}:book:2", "Nota.", Start), (booking.ClientRequestId, booking.Note, booking.LocalStartsAt));
    }

    [Fact]
    public async Task ReadinessPassesThrough()
    {
        var readiness = await Adapter().GetReadinessAsync(TenantId, Ct);

        Assert.Equal((true, 2, true), (readiness.ModuleEnabled, readiness.BookableServices, readiness.Ready));
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> handler)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, StubProxy>();
        ((StubProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    public class StubProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            return Handler(targetMethod, args);
        }
    }

    private sealed class FakeChannelScheduling : IChannelScheduling
    {
        private static readonly ChannelAppointment Stored = new(
            AppointmentId, "Pending", new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.FromHours(-5)),
            new DateTimeOffset(2026, 11, 24, 9, 30, 0, TimeSpan.FromHours(-5)), "America/Bogota", ServiceId, "Consulta", "Dra. Pérez", "Sede",
            true, true, new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.FromHours(-5)));

        public Exception? Throws { get; set; }

        public List<Guid> ListedFor { get; } = [];

        public ChannelBooking? LastBooking { get; private set; }

        public int WriteCalls { get; private set; }

        public (Guid CustomerId, Guid AppointmentId)? LastRescheduleCheck { get; private set; }

        private void MaybeThrow()
        {
            if (Throws is { } error)
            {
                throw error;
            }
        }

        public Task<ChannelReadiness> GetReadinessAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new ChannelReadiness(true, 2));

        public Task<ChannelServiceList> ListBookableServicesAsync(Guid tenantId, int take, CancellationToken cancellationToken)
        {
            MaybeThrow();
            return Task.FromResult(new ChannelServiceList("America/Bogota", [], false));
        }

        public Task<ChannelAvailability> SearchAvailabilityAsync(
            Guid tenantId, Guid serviceId, DateOnly fromDate, DateOnly toDate, Guid? resourceId, int take, CancellationToken cancellationToken)
        {
            MaybeThrow();
            return Task.FromResult(new ChannelAvailability("America/Bogota", serviceId, [], false));
        }

        public Task<ChannelAppointmentList> ListCustomerAppointmentsAsync(
            Guid tenantId, Guid customerId, int take, CancellationToken cancellationToken)
        {
            MaybeThrow();
            ListedFor.Add(customerId);
            return Task.FromResult(new ChannelAppointmentList("America/Bogota", [Stored], false));
        }

        public Task<ChannelAppointment?> FindCustomerAppointmentAsync(
            Guid tenantId, Guid customerId, Guid appointmentId, CancellationToken cancellationToken)
        {
            MaybeThrow();
            return Task.FromResult<ChannelAppointment?>(Stored);
        }

        public Task<ChannelRescheduleCheck> CheckRescheduleAsync(
            Guid tenantId, Guid customerId, Guid appointmentId, DateTime localStartsAt, Guid? resourceId, CancellationToken cancellationToken)
        {
            MaybeThrow();
            LastRescheduleCheck = (customerId, appointmentId);
            return Task.FromResult(new ChannelRescheduleCheck(
                new ChannelStartCheck("Confirmed", Stored.StartsAt, Stored.EndsAt, "America/Bogota", "Consulta", "Dra. Pérez", "Sede"), false));
        }

        public Task<ChannelStartCheck> CheckStartAsync(
            Guid tenantId, Guid serviceId, DateTime localStartsAt, Guid? resourceId, CancellationToken cancellationToken)
        {
            MaybeThrow();
            return Task.FromResult(new ChannelStartCheck("Pending", Stored.StartsAt, Stored.EndsAt, "America/Bogota", "Consulta", "Dra. Pérez", "Sede"));
        }

        public Task<ChannelWriteResult> BookAsync(ChannelBooking booking, CancellationToken cancellationToken)
        {
            MaybeThrow();
            LastBooking = booking;
            WriteCalls++;
            return Task.FromResult(new ChannelWriteResult(Stored, false));
        }

        public Task<ChannelWriteResult> CancelAsync(
            Guid tenantId, Guid agentId, Guid customerId, Guid appointmentId, string? reason, CancellationToken cancellationToken)
        {
            MaybeThrow();
            WriteCalls++;
            return Task.FromResult(new ChannelWriteResult(Stored, false));
        }

        public Task<ChannelWriteResult> RescheduleAsync(
            Guid tenantId, Guid agentId, Guid customerId, Guid appointmentId, DateTime localStartsAt, Guid? resourceId,
            CancellationToken cancellationToken)
        {
            MaybeThrow();
            WriteCalls++;
            return Task.FromResult(new ChannelWriteResult(Stored, false));
        }
    }
}
