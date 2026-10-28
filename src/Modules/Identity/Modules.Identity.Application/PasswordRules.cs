using FluentValidation;

namespace Modules.Identity.Application;

public static class PasswordRules
{
    public const int MinLength = 10;
    public const int MaxLength = 128;

    /// <summary>
    /// Sólo largo: sin reglas de composición. El error cae en el campo que lo declara. Es una sola
    /// regla (no NotEmpty + Length) para que vacío, corto y largo den un único error con el mismo
    /// código: <c>WithErrorCode</c> sólo marca la última regla de la cadena.
    /// </summary>
    public static IRuleBuilderOptions<T, string> QepPassword<T>(this IRuleBuilder<T, string> rule) =>
        rule.Must(password => password is { Length: >= MinLength and <= MaxLength })
            .WithErrorCode("identity.password.invalid")
            .WithMessage($"'{{PropertyName}}' must be between {MinLength} and {MaxLength} characters.");
}

public sealed record NewPasswordInput(string Password);

public sealed class NewPasswordValidator : AbstractValidator<NewPasswordInput>
{
    public NewPasswordValidator() => RuleFor(input => input.Password).QepPassword();
}
