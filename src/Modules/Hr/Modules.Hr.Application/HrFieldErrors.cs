using System.Diagnostics.CodeAnalysis;
using FluentValidation;
using FluentValidation.Results;

namespace Modules.Hr.Application;

/// <summary>
/// Un error de campo que sólo se sabe con lo cargado (la empresa no está activa, la sede no existe): sale como 422
/// <c>validation.failed</c> con el mapa <c>errors</c>, igual que un validador. La clave va en camelCase.
/// </summary>
internal static class HrFieldErrors
{
    public static ValidationException Error(string field, string message) =>
        new([new ValidationFailure(field, message)]);

    [DoesNotReturn]
    public static void Throw(string field, string message) => throw Error(field, message);
}
