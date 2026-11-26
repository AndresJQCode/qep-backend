using System.Diagnostics.CodeAnalysis;
using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.Scheduling.Domain;

namespace Modules.Scheduling.Application;

/// <summary>
/// El <c>If-Match</c> de un <c>PUT</c>. Llega crudo al caso de uso y se lee <b>después</b> de autorizar: quien opera en otro tenant
/// recibe 403 y no un 428 sobre un encabezado que todavía no puede usar. Sin encabezado válido es 428; la versión vieja es 412.
/// Calco de <c>HrIfMatch</c>.
/// </summary>
internal static class SchedulingIfMatch
{
    public static long Require(string? ifMatch) =>
        TryParse(ifMatch, out var version)
            ? version
            : throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded record version is required.");

    /// <summary>Un <c>PUT</c> se aplica sobre lo que la pantalla cargó: otra versión es 412.</summary>
    public static void EnsureVersion(long current, long expected)
    {
        if (current != expected)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The scheduling record changed after it was loaded.");
        }
    }

    private static bool TryParse(string? etag, out long version)
    {
        version = 0;
        if (string.IsNullOrWhiteSpace(etag))
        {
            return false;
        }

        var normalized = etag.Trim();
        if (normalized.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..].Trim();
        }

        normalized = normalized.Trim('"');
        return long.TryParse(normalized, System.Globalization.CultureInfo.InvariantCulture, out version) && version > 0;
    }
}

// Las búsquedas siempre están acotadas al tenant del llamador, así que «no encontrado» significa «no está entre tus datos».
internal static class SchedulingNotFound
{
    public static ResourceNotFoundException Location(Guid id) =>
        new(SchedulingErrorCodes.LocationNotFound, $"Location '{id}' was not found.");

    public static ResourceNotFoundException Resource(Guid id) =>
        new(SchedulingErrorCodes.ResourceNotFound, $"Resource '{id}' was not found.");

    public static ResourceNotFoundException Service(Guid id) =>
        new(SchedulingErrorCodes.ServiceNotFound, $"Service '{id}' was not found.");

    public static ResourceNotFoundException Block(Guid id) =>
        new(SchedulingErrorCodes.BlockNotFound, $"Availability block '{id}' was not found.");

    public static ResourceNotFoundException Closure(Guid id) =>
        new(SchedulingErrorCodes.ClosureNotFound, $"Closure '{id}' was not found.");

    public static ResourceNotFoundException Session(Guid id) =>
        new(SchedulingErrorCodes.SessionNotFound, $"Published session '{id}' was not found.");

    public static ResourceNotFoundException Appointment(Guid id) =>
        new(SchedulingErrorCodes.AppointmentNotFound, $"Appointment '{id}' was not found.");
}

/// <summary>
/// Un error de campo que sólo se sabe con lo cargado (el miembro no es del tenant, la sede no existe): sale como 422
/// <c>validation.failed</c> con el mapa <c>errors</c>, igual que un validador. La clave va en camelCase. Calco de <c>HrFieldErrors</c>.
/// </summary>
internal static class SchedulingFieldErrors
{
    [DoesNotReturn]
    public static void Throw(string field, string message) =>
        throw new ValidationException([new ValidationFailure(field, message)]);
}

internal static class SchedulingEnums
{
    /// <summary>Sólo el nombre exacto del miembro: un número no es un enum, y <c>Enum.TryParse</c> lo aceptaría.</summary>
    public static bool TryParse<TEnum>(string? text, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        return text is not null
            && Enum.GetNames<TEnum>().Contains(text, StringComparer.Ordinal)
            && Enum.TryParse(text, ignoreCase: false, out value);
    }
}
