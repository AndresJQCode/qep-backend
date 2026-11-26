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

public sealed class BlockRequestValidator : SchedulingValidator<BlockRequest>
{
    public BlockRequestValidator()
    {
        RuleFor(request => request.Name).MustBeAName();
        RuleFor(request => request.Kind)
            .Must(kind => SchedulingEnums.TryParse<BlockKind>(kind, out _))
            .WithMessage("Elige la repetición de la franja: Weekly u OneOff.");

        // Cada rama se valida sólo con su tipo: una franja semanal sin días, o puntual sin fecha, no dice nada que abrir.
        RuleFor(request => request.Weekly)
            .Must(BlockShape.WeeklyIsValid)
            .WithMessage("Indica al menos un día de la semana, sin repetirlo, con rangos de minutos válidos que no se solapen.")
            .When(request => request.Kind == nameof(BlockKind.Weekly));
        RuleFor(request => request.Weekly)
            .Must(weekly => weekly is null or { Count: 0 })
            .WithMessage("Una franja puntual no lleva días de la semana.")
            .When(request => request.Kind == nameof(BlockKind.OneOff));
        RuleFor(request => request.OneOff)
            .Must(BlockShape.OneOffIsValid)
            .WithMessage("Indica la fecha y al menos un rango de minutos válido, sin solapes.")
            .When(request => request.Kind == nameof(BlockKind.OneOff));
        RuleFor(request => request.OneOff)
            .Must(oneOff => oneOff is null)
            .WithMessage("Una franja semanal no lleva una fecha puntual.")
            .When(request => request.Kind == nameof(BlockKind.Weekly));

        RuleFor(request => request.ValidTo)
            .Must((request, validTo) => validTo is null || request.ValidFrom is null || validTo >= request.ValidFrom)
            .WithMessage("La vigencia no puede terminar antes de empezar.");
        RuleFor(request => request.CapacityPerResource)
            .Must(capacity => capacity is null || (capacity >= 1 && capacity <= AvailabilityBlock.MaxCapacity))
            .WithMessage($"El cupo por recurso va de 1 a {AvailabilityBlock.MaxCapacity}.");
        RuleFor(request => request.SessionMode)
            .Must(mode => mode is null || SchedulingEnums.TryParse<SessionMode>(mode, out _))
            .WithMessage("Elige el modo de sesión: FirstBookingOpens o PublishedSessions.");
        RuleFor(request => request.AssignmentRule)
            .Must(rule => rule is null || SchedulingEnums.TryParse<AssignmentRule>(rule, out _))
            .WithMessage("Elige una regla de asignación válida: FirstFree.");
        RuleFor(request => request.ServiceIds)
            .Must(BlockShape.IdsAreValid).WithMessage("Elige al menos un servicio, sin repetir ninguno.");
        RuleFor(request => request.ResourceIds)
            .Must(BlockShape.IdsAreValid).WithMessage("Elige al menos un recurso, sin repetir ninguno.");
    }
}

/// <summary>Lo que el dominio rechazaría con una excepción de programación, revisado antes para que el campo quede marcado.</summary>
internal static class BlockShape
{
    public static bool IdsAreValid(IReadOnlyList<Guid>? ids) =>
        ids is { Count: > 0 } && ids.All(id => id != Guid.Empty) && ids.Distinct().Count() == ids.Count;

    public static bool WeeklyIsValid(IReadOnlyList<WeekdayHoursRequest>? weekly) =>
        weekly is { Count: > 0 }
        && weekly.All(day => day is { Day: >= 1 and <= 7 } && RangesAreValid(day.Ranges))
        && weekly.Select(day => day.Day).Distinct().Count() == weekly.Count;

    public static bool OneOffIsValid(DateHoursRequest? oneOff) =>
        oneOff is { Date: not null } && RangesAreValid(oneOff.Ranges);

    private static bool RangesAreValid(IReadOnlyList<TimeRangeRequest>? ranges)
    {
        if (ranges is not { Count: > 0 }
            || ranges.Any(range => range is not { From: { } start, To: { } end } || start < 0 || end > TimeRange.MinutesInDay || start >= end))
        {
            return false;
        }

        var sorted = ranges.OrderBy(range => range.From).ToArray();
        for (var index = 1; index < sorted.Length; index++)
        {
            if (sorted[index].From < sorted[index - 1].To)
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class ClosureRequestValidator : SchedulingValidator<ClosureRequest>
{
    public const int ReasonMaxLength = 200;

    public ClosureRequestValidator()
    {
        RuleFor(request => request.LocationId)
            .Must(id => id is null || id.Value != Guid.Empty).WithMessage("Elige una sede válida.");
        RuleFor(request => request.ResourceId)
            .Must(id => id is null || id.Value != Guid.Empty).WithMessage("Elige un recurso válido.");
        RuleFor(request => request.StartsAt).NotNull().WithMessage("Indica cuándo empieza el bloqueo.");
        RuleFor(request => request.EndsAt)
            .NotNull().WithMessage("Indica cuándo termina el bloqueo.")
            .Must((request, endsAt) => endsAt is null || request.StartsAt is null || endsAt > request.StartsAt)
            .WithMessage("El bloqueo debe terminar después de empezar.");
        RuleFor(request => request.Reason)
            .Must(reason => reason is null || reason.Trim().Length <= ReasonMaxLength)
            .WithMessage($"El motivo no puede pasar de {ReasonMaxLength} caracteres.");
    }
}

public sealed class PublishSessionRequestValidator : SchedulingValidator<PublishSessionRequest>
{
    public PublishSessionRequestValidator()
    {
        RuleFor(request => request.BlockId)
            .Must(id => id is { } value && value != Guid.Empty).WithMessage("Elige la franja.");
        RuleFor(request => request.ResourceId)
            .Must(id => id is { } value && value != Guid.Empty).WithMessage("Elige el recurso.");
        RuleFor(request => request.ServiceId)
            .Must(id => id is { } value && value != Guid.Empty).WithMessage("Elige el servicio.");
        RuleFor(request => request.StartsAt).NotNull().WithMessage("Indica cuándo empieza la sesión.");
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
