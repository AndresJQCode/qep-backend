using FluentValidation;

namespace Modules.Scheduling.Application;

public sealed class AvailabilityRequestValidator : SchedulingValidator<AvailabilityRequest>
{
    /// <summary>El rango más largo que se calcula de una vez: dos meses alcanzan para el calendario y acotan el trabajo del motor.</summary>
    public const int MaxDays = 62;

    public AvailabilityRequestValidator()
    {
        RuleFor(request => request.ServiceId)
            .Must(id => id is { } value && value != Guid.Empty).WithMessage("Elige el servicio.");
        RuleFor(request => request.From).NotNull().WithMessage("Indica desde qué día.");
        RuleFor(request => request.To)
            .NotNull().WithMessage("Indica hasta qué día.")
            .Must((request, to) => to is null || request.From is null || to >= request.From)
            .WithMessage("El rango no puede terminar antes de empezar.")
            .Must((request, to) => to is null || request.From is null || to.Value.DayNumber - request.From.Value.DayNumber <= MaxDays)
            .WithMessage($"Pide como máximo {MaxDays} días a la vez.");
        RuleFor(request => request.ResourceId)
            .Must(id => id is null || id.Value != Guid.Empty).WithMessage("Elige un recurso válido.");
    }
}

public sealed class NewCustomerRequestValidator : SchedulingValidator<NewCustomerRequest>
{
    /// <summary>El mismo tope de teléfono de Clientes: un valor más largo allá se rechaza con su propio código.</summary>
    public const int PhoneMaxLength = 32;

    public NewCustomerRequestValidator()
    {
        RuleFor(request => request.Name).MustBeAName();
        RuleFor(request => request.Phone)
            .Must(phone => phone is null || phone.Trim().Length <= PhoneMaxLength)
            .WithMessage($"El teléfono no puede pasar de {PhoneMaxLength} caracteres.");
    }
}

public sealed class BookAppointmentRequestValidator : SchedulingValidator<BookAppointmentRequest>
{
    public const int NoteMaxLength = 1000;

    public const int ClientRequestIdMaxLength = 100;

    public BookAppointmentRequestValidator()
    {
        RuleFor(request => request.ServiceId)
            .Must(id => id is { } value && value != Guid.Empty).WithMessage("Elige el servicio.");
        RuleFor(request => request.ResourceId)
            .Must(id => id is null || id.Value != Guid.Empty).WithMessage("Elige un recurso válido.");
        RuleFor(request => request.CustomerId)
            .Must(id => id is { } value && value != Guid.Empty).WithMessage("Elige el cliente.");
        RuleFor(request => request.StartsAt).NotNull().WithMessage("Indica cuándo empieza la cita.");
        RuleFor(request => request.Source)
            .Must(source => source is null || SchedulingEnums.TryParse<Domain.AppointmentSource>(source, out _))
            .WithMessage("Indica el origen de la cita: BackOffice o Channel.");
        RuleFor(request => request.Note)
            .Must(note => note is null || note.Trim().Length <= NoteMaxLength)
            .WithMessage($"La nota no puede pasar de {NoteMaxLength} caracteres.");
        RuleFor(request => request.ClientRequestId)
            .Must(key => key is null || key.Trim().Length <= ClientRequestIdMaxLength)
            .WithMessage($"La llave de la petición no puede pasar de {ClientRequestIdMaxLength} caracteres.");
        RuleFor(request => request.ConversationId)
            .Must(id => id is null || id.Value != Guid.Empty).WithMessage("Indica una conversación válida.");
    }
}

public sealed class CancelAppointmentRequestValidator : SchedulingValidator<CancelAppointmentRequest>
{
    public const int ReasonMaxLength = 500;

    public CancelAppointmentRequestValidator()
    {
        RuleFor(request => request.Reason)
            .Must(reason => reason is null || reason.Trim().Length <= ReasonMaxLength)
            .WithMessage($"El motivo no puede pasar de {ReasonMaxLength} caracteres.");
        RuleFor(request => request.Source)
            .Must(source => source is null || SchedulingEnums.TryParse<Domain.AppointmentSource>(source, out _))
            .WithMessage("Indica el origen de la cancelación: BackOffice o Channel.");
    }
}

public sealed class RescheduleAppointmentRequestValidator : SchedulingValidator<RescheduleAppointmentRequest>
{
    public RescheduleAppointmentRequestValidator()
    {
        RuleFor(request => request.ResourceId)
            .Must(id => id is null || id.Value != Guid.Empty).WithMessage("Elige un recurso válido.");
        RuleFor(request => request.StartsAt).NotNull().WithMessage("Indica el nuevo inicio.");
    }
}
