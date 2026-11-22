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
