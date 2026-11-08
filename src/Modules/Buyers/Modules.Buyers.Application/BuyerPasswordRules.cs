using FluentValidation;

namespace Modules.Buyers.Application;

/// <summary>
/// La contraseña del comprador. Los límites son los de Identity (NIST: sólo largo, sin reglas de composición) y una prueba de
/// paridad en Bootstrapper.UnitTests los mantiene iguales.
/// </summary>
public static class BuyerPasswordRules
{
    public const int MinLength = 10;
    public const int MaxLength = 128;

    public const string ErrorCode = "store.buyer.password_invalid";

    /// <summary>
    /// Una sola regla (no NotEmpty + Length) para que vacía, corta y larga den un único error con el mismo código:
    /// <c>WithErrorCode</c> sólo marca la última regla de la cadena.
    /// </summary>
    public static IRuleBuilderOptions<T, string> BuyerPassword<T>(this IRuleBuilder<T, string> rule) =>
        rule.Must(password => password is { Length: >= MinLength and <= MaxLength })
            .WithErrorCode(ErrorCode)
            .WithMessage($"La contraseña debe tener entre {MinLength} y {MaxLength} caracteres.");
}
