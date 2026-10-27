using FluentValidation;

namespace Modules.Identity.Application;

public static class PasswordRules
{
    public const int MinLength = 10;
    public const int MaxLength = 128;

    /// <summary>
    /// Sólo largo: sin reglas de composición. El error cae en el campo que lo declara.
    /// </summary>
    public static IRuleBuilderOptions<T, string> QepPassword<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty()
            .Length(MinLength, MaxLength)
            .WithErrorCode("identity.password.invalid");
}

public sealed record NewPasswordInput(string Password);

public sealed class NewPasswordValidator : AbstractValidator<NewPasswordInput>
{
    public NewPasswordValidator() => RuleFor(input => input.Password).QepPassword();
}
