using FluentValidation;
using Modules.Scheduling.Domain;

namespace Modules.Scheduling.Application;

internal static class SchedulingRules
{
    public const int NameMaxLength = 120;

    public static IRuleBuilderOptions<T, string?> MustBeAName<T>(this IRuleBuilder<T, string?> rule) => rule
        .Must(name => !string.IsNullOrWhiteSpace(name)).WithMessage("Escribe un nombre.")
        .Must(name => name is null || name.Trim().Length <= NameMaxLength)
        .WithMessage($"El nombre no puede pasar de {NameMaxLength} caracteres.");

    /// <summary>Un campo numérico requerido y dentro de su rango. Nulo es «falta», no cero.</summary>
    public static IRuleBuilderOptions<T, int?> MustBeBetween<T>(this IRuleBuilder<T, int?> rule, int min, int max, string message) => rule
        .Must(value => value is { } number && number >= min && number <= max).WithMessage(message);
}

// Los validadores son públicos a propósito: AddValidatorsFromAssembly sólo registra tipos públicos, y uno que no se registra deja
// pasar el cuerpo sin validar.
public sealed class LocationRequestValidator : SchedulingValidator<LocationRequest>
{
    public LocationRequestValidator() => RuleFor(request => request.Name).MustBeAName();
}

public sealed class ResourceRequestValidator : SchedulingValidator<ResourceRequest>
{
    public ResourceRequestValidator()
    {
        RuleFor(request => request.DisplayName).MustBeAName();
        RuleFor(request => request.LocationId)
            .Must(id => id is null || id.Value != Guid.Empty).WithMessage("Elige una sede válida.");
        RuleFor(request => request.MemberId)
            .Must(id => id is null || id.Value != Guid.Empty).WithMessage("Elige un miembro válido.");
    }
}

public sealed class ServiceRequestValidator : SchedulingValidator<ServiceRequest>
{
    public ServiceRequestValidator()
    {
        RuleFor(request => request.Name).MustBeAName();
        RuleFor(request => request.DurationMinutes)
            .Must(value => value is { } minutes && minutes >= 5 && minutes <= Service.MaxDurationMinutes && minutes % 5 == 0)
            .WithMessage($"La duración debe ser un múltiplo de 5 minutos, entre 5 y {Service.MaxDurationMinutes}.");
        RuleFor(request => request.BufferMinutes)
            .Must(value => value is null || (value >= 0 && value <= Service.MaxBufferMinutes && value % 5 == 0))
            .WithMessage($"El margen debe ser un múltiplo de 5 minutos, entre 0 y {Service.MaxBufferMinutes}.");
    }
}

public sealed class SettingsRequestValidator : SchedulingValidator<SettingsRequest>
{
    public SettingsRequestValidator()
    {
        RuleFor(request => request.ConfirmationMode)
            .Must(mode => SchedulingEnums.TryParse<ConfirmationMode>(mode, out _))
            .WithMessage("Elige cómo se confirman las citas: Automatic o Manual.");
        RuleFor(request => request.MinNoticeMinutes)
            .MustBeBetween(0, 10_080, "El aviso mínimo va de 0 minutos a 7 días (10080 minutos).");
        RuleFor(request => request.HorizonDays)
            .MustBeBetween(1, 365, "El horizonte de reserva va de 1 a 365 días.");
        RuleFor(request => request.ClientCancelCutoffHours)
            .MustBeBetween(0, 720, "El plazo para cancelar va de 0 a 720 horas.");
        RuleFor(request => request.ReminderOffsetHours)
            .MustBeBetween(1, 168, "El recordatorio sale entre 1 y 168 horas antes de la cita.");
    }
}
