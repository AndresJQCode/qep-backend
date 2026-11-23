using BuildingBlocks.Application;
using FluentValidation;
using Modules.Ai.Application;
using Modules.Ai.Domain;
using Modules.Messaging.Application;
using Modules.Scheduling.Application;
using Modules.Scheduling.Domain;
using Modules.Tenancy.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-23 §7.2 y §8: la Agenda del agente sobre <see cref="IChannelScheduling"/>, calco de <c>AiQuotationDrafts</c>. Traduce las
/// excepciones de Scheduling a los códigos cortos que lee el modelo y resuelve el cliente de la conversación.
/// <para>
/// El cliente se <b>relee en cada llamada</b> (D-9): una reserva que crea la ficha la deja visible para la herramienta siguiente del mismo
/// trabajo, y una fusión en el medio no apunta a un cliente inactivo. Sólo <c>book_appointment</c> crea y cuelga la ficha (D-6, con
/// <see cref="ConversationCustomerResolver"/>, el camino de «Política de datos» y «Acción de CRM»); las demás leen lo que la conversación ya
/// tiene. En el playground (sin conversación) el cliente es el de la prueba y nunca otro.
/// </para>
/// </summary>
internal sealed class AiScheduling(IChannelScheduling channel, IConversationQueries conversations, ConversationCustomerResolver resolver)
    : IAiScheduling
{
    private const int ServiceTake = 20;
    private const int SlotTake = 30;
    private const int AppointmentTake = 10;

    public async Task<AgentSchedulingReadiness> GetReadinessAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var readiness = await channel.GetReadinessAsync(tenantId, cancellationToken);
        return new AgentSchedulingReadiness(readiness.ModuleEnabled, readiness.BookableServices);
    }

    public Task<AiSchedulingResult<AiBookableServices>> ListBookableServicesAsync(AiSchedulingCaller caller, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            var list = await channel.ListBookableServicesAsync(caller.TenantId, ServiceTake, cancellationToken);
            return AiSchedulingResult<AiBookableServices>.Ok(new AiBookableServices(
                list.TimeZone,
                [.. list.Services.Select(service => new AiBookableService(service.ServiceId, service.Name, service.DurationMinutes))],
                list.Truncated));
        });

    public Task<AiSchedulingResult<AiAvailability>> SearchAvailabilityAsync(
        AiSchedulingCaller caller, Guid serviceId, DateOnly fromDate, DateOnly toDate, Guid? resourceId, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            var found = await channel.SearchAvailabilityAsync(caller.TenantId, serviceId, fromDate, toDate, resourceId, SlotTake, cancellationToken);
            return AiSchedulingResult<AiAvailability>.Ok(new AiAvailability(
                found.TimeZone,
                found.ServiceId,
                [
                    .. found.Slots.Select(slot => new AiSlot(
                        slot.StartsAt,
                        slot.EndsAt,
                        slot.SeatsLeft,
                        [.. slot.Resources.Select(resource => new AiSlotResource(resource.ResourceId, resource.Name))])),
                ],
                found.Truncated));
        });

    public Task<AiSchedulingResult<AiAppointmentWrite>> BookAsync(
        AiSchedulingCaller caller,
        Guid serviceId,
        DateTime localStartsAt,
        Guid? resourceId,
        string? note,
        string clientRequestId,
        CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            if (await BookingCustomerAsync(caller, cancellationToken) is not { } customerId)
            {
                return CustomerUnavailable<AiAppointmentWrite>();
            }

            var written = await channel.BookAsync(
                new ChannelBooking(
                    caller.TenantId, caller.AgentId, customerId, caller.ConversationId, serviceId, localStartsAt, resourceId, note, clientRequestId),
                cancellationToken);
            return AiSchedulingResult<AiAppointmentWrite>.Ok(ToWrite(written));
        });

    public Task<AiSchedulingResult<AiStartPreview>> PreviewBookingAsync(
        AiSchedulingCaller caller, Guid serviceId, DateTime localStartsAt, Guid? resourceId, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            var check = await channel.CheckStartAsync(caller.TenantId, serviceId, localStartsAt, resourceId, cancellationToken);
            return AiSchedulingResult<AiStartPreview>.Ok(new AiStartPreview(
                check.Status, check.StartsAt, check.EndsAt, check.TimeZone, check.ServiceName, check.ResourceName, check.LocationName));
        });

    public Task<AiSchedulingResult<AiReschedulePreview>> PreviewRescheduleAsync(
        AiSchedulingCaller caller, Guid appointmentId, DateTime localStartsAt, Guid? resourceId, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            if (await ReadingCustomerAsync(caller, cancellationToken) is not { } customerId)
            {
                // Sin ficha no hay citas propias: el mismo silencio que cancelar o mover.
                return AiSchedulingResult<AiReschedulePreview>.Error(AgentToolErrors.AppointmentNotFound);
            }

            var check = await channel.CheckRescheduleAsync(caller.TenantId, customerId, appointmentId, localStartsAt, resourceId, cancellationToken);
            var start = check.Start;
            return AiSchedulingResult<AiReschedulePreview>.Ok(new AiReschedulePreview(
                new AiStartPreview(start.Status, start.StartsAt, start.EndsAt, start.TimeZone, start.ServiceName, start.ResourceName, start.LocationName),
                check.AlreadyDone));
        });

    public Task<AiSchedulingResult<AiAppointments>> ListMyAppointmentsAsync(AiSchedulingCaller caller, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            if (await ReadingCustomerAsync(caller, cancellationToken) is not { } customerId)
            {
                // Un contacto sin ficha no tiene citas (§5.4): vacío, no error. La zona sale de la misma guarda del módulo.
                var zone = (await channel.ListBookableServicesAsync(caller.TenantId, 0, cancellationToken)).TimeZone;
                return AiSchedulingResult<AiAppointments>.Ok(new AiAppointments(zone, [], false));
            }

            var list = await channel.ListCustomerAppointmentsAsync(caller.TenantId, customerId, AppointmentTake, cancellationToken);
            return AiSchedulingResult<AiAppointments>.Ok(
                new AiAppointments(list.TimeZone, [.. list.Appointments.Select(ToAppointment)], list.Truncated));
        });

    public Task<AiSchedulingResult<AiAppointment>> FindMyAppointmentAsync(
        AiSchedulingCaller caller, Guid appointmentId, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            if (await ReadingCustomerAsync(caller, cancellationToken) is not { } customerId
                || await channel.FindCustomerAppointmentAsync(caller.TenantId, customerId, appointmentId, cancellationToken) is not { } found)
            {
                return AiSchedulingResult<AiAppointment>.Error(AgentToolErrors.AppointmentNotFound);
            }

            return AiSchedulingResult<AiAppointment>.Ok(ToAppointment(found));
        });

    public Task<AiSchedulingResult<AiAppointmentWrite>> CancelAsync(
        AiSchedulingCaller caller, Guid appointmentId, string? reason, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            if (await ReadingCustomerAsync(caller, cancellationToken) is not { } customerId)
            {
                return AiSchedulingResult<AiAppointmentWrite>.Error(AgentToolErrors.AppointmentNotFound);
            }

            var written = await channel.CancelAsync(caller.TenantId, caller.AgentId, customerId, appointmentId, reason, cancellationToken);
            return AiSchedulingResult<AiAppointmentWrite>.Ok(ToWrite(written));
        });

    public Task<AiSchedulingResult<AiAppointmentWrite>> RescheduleAsync(
        AiSchedulingCaller caller, Guid appointmentId, DateTime localStartsAt, Guid? resourceId, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            if (await ReadingCustomerAsync(caller, cancellationToken) is not { } customerId)
            {
                return AiSchedulingResult<AiAppointmentWrite>.Error(AgentToolErrors.AppointmentNotFound);
            }

            var written = await channel.RescheduleAsync(
                caller.TenantId, caller.AgentId, customerId, appointmentId, localStartsAt, resourceId, cancellationToken);
            return AiSchedulingResult<AiAppointmentWrite>.Ok(ToWrite(written));
        });

    /// <summary>Lo que la conversación tiene hoy; sin conversación (playground), el cliente de la prueba (§7.2).</summary>
    private async Task<Guid?> ReadingCustomerAsync(AiSchedulingCaller caller, CancellationToken cancellationToken)
    {
        if (caller.ConversationId is not { } conversationId)
        {
            return caller.CustomerId;
        }

        return (await conversations.FindAsync(caller.TenantId, conversationId, cancellationToken))?.CustomerId;
    }

    /// <summary>D-6: reservar crea y cuelga la ficha si falta. <c>null</c> si no hay identidad de canal ni cliente posible.</summary>
    private async Task<Guid?> BookingCustomerAsync(AiSchedulingCaller caller, CancellationToken cancellationToken)
    {
        if (caller.ConversationId is not { } conversationId)
        {
            return caller.CustomerId;
        }

        if (await conversations.FindAsync(caller.TenantId, conversationId, cancellationToken) is not { } row)
        {
            return null;
        }

        return await resolver.ResolveAsync(row, cancellationToken) is ConversationCustomer.Linked linked ? linked.CustomerId : null;
    }

    private static AiSchedulingResult<T> CustomerUnavailable<T>()
        where T : class =>
        AiSchedulingResult<T>.HandedOff(AgentToolErrors.CustomerUnknown, AgentHandoffCodes.AppointmentCustomerUnavailable);

    private static async Task<AiSchedulingResult<T>> GuardAsync<T>(Func<Task<AiSchedulingResult<T>>> action)
        where T : class
    {
        try
        {
            return await action();
        }
        catch (Exception error) when (TryTranslate(error, out var code, out var handoff))
        {
            return handoff is null ? AiSchedulingResult<T>.Error(code) : AiSchedulingResult<T>.HandedOff(code, handoff);
        }
    }

    /// <summary>La tabla de spec §8. Lo que no está acá sube y el bucle lo convierte en <c>tool_failed</c> (el detalle va al log, no al modelo).</summary>
    private static bool TryTranslate(Exception error, out string code, out string? handoff)
    {
        handoff = null;
        switch (error)
        {
            case RequestForbiddenException { Code: TenantModuleGuard.ModuleNotEnabledCode }:
                code = AgentToolErrors.SchedulingUnavailable;
                return true;
            case ResourceConflictException { Code: SchedulingErrorCodes.SlotTaken }:
                code = AgentToolErrors.SlotTaken;
                return true;
            case ResourceConflictException { Code: SchedulingErrorCodes.SessionFull }:
                code = AgentToolErrors.SessionFull;
                return true;
            case SchedulingDomainException { Code: SchedulingErrorCodes.OutsideRules }:
                code = AgentToolErrors.OutsideRules;
                return true;
            case SchedulingDomainException { Code: SchedulingErrorCodes.InvalidTransition }:
                // D-3: fuera de plazo, cita cerrada o servicio sin reserva por chat: lo resuelve una persona.
                code = AgentToolErrors.AppointmentChangeBlocked;
                handoff = AgentHandoffCodes.AppointmentChangeBlocked;
                return true;
            case SchedulingDomainException { Code: SchedulingErrorCodes.ResourceNotChoosable }:
                code = AgentToolErrors.ResourceNotChoosable;
                return true;
            case SchedulingDomainException { Code: SchedulingErrorCodes.BeforeStart }:
                // No es alcanzable por estas herramientas (sólo asistencia y «no asistió»): falla genérica por defensa.
                code = AgentToolErrors.ToolFailed;
                return true;
            case ResourceNotFoundException { Code: SchedulingErrorCodes.AppointmentNotFound }:
                code = AgentToolErrors.AppointmentNotFound;
                return true;
            case ResourceNotFoundException { Code: SchedulingErrorCodes.ServiceNotFound }:
                code = AgentToolErrors.ServiceNotFound;
                return true;
            case ResourceNotFoundException { Code: SchedulingErrorCodes.ResourceNotFound }:
                code = AgentToolErrors.ResourceNotChoosable;
                return true;
            case ValidationException validation when FieldOf(validation, "clientRequestId"):
                // The key belongs to another customer's booking: nothing of it goes to the model.
                code = AgentToolErrors.SchedulingUnavailable;
                return true;
            case ValidationException validation when FieldOf(validation, "serviceId"):
                code = AgentToolErrors.ServiceNotFound;
                return true;
            case ValidationException validation when FieldOf(validation, "resourceId"):
                code = AgentToolErrors.ResourceNotChoosable;
                return true;
            case ValidationException validation when FieldOf(validation, "customerId"):
                code = AgentToolErrors.CustomerUnknown;
                handoff = AgentHandoffCodes.AppointmentCustomerUnavailable;
                return true;
            case RequestConcurrencyException:
                code = AgentToolErrors.AppointmentChanged;
                return true;
            default:
                code = string.Empty;
                return false;
        }
    }

    private static bool FieldOf(ValidationException validation, string field) =>
        validation.Errors.Any(failure => string.Equals(failure.PropertyName, field, StringComparison.OrdinalIgnoreCase));

    private static AiAppointmentWrite ToWrite(ChannelWriteResult written) => new(ToAppointment(written.Appointment), written.AlreadyDone);

    private static AiAppointment ToAppointment(ChannelAppointment appointment) => new(
        appointment.AppointmentId,
        appointment.Status,
        appointment.StartsAt,
        appointment.EndsAt,
        appointment.TimeZone,
        appointment.ServiceId,
        appointment.ServiceName,
        appointment.ResourceName,
        appointment.LocationName,
        appointment.CanChangeByChat,
        appointment.CanRescheduleByChat,
        appointment.ChangeByChatUntil);
}
