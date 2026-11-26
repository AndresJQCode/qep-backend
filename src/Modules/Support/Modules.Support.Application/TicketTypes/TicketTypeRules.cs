using FluentValidation;
using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>Las reglas de un tipo de ticket que comparten el alta y la edición: cubren cada guarda de <see cref="SupportTicketType"/>
/// (nombre, término, legal sin término) para que ningún cuerpo llegue al dominio y salga como 500.</summary>
internal static class TicketTypeRules
{
    /// <summary>Se mide recortado, como lo guarda el dominio: «   » es un nombre vacío.</summary>
    public static IRuleBuilderOptions<T, string> MustBeATypeName<T>(this IRuleBuilder<T, string> rule) =>
        rule.Must(text => text?.Trim() is { Length: > 0 and <= SupportTicketType.NameMaxLength })
            .WithMessage($"The name must have between 1 and {SupportTicketType.NameMaxLength} characters.");

    public static IRuleBuilderOptions<T, int?> MustBeATerm<T>(this IRuleBuilder<T, int?> rule) =>
        rule.Must(term => term is null or >= 1 and <= SupportTicketType.MaxTermBusinessDays)
            .WithMessage($"The term is empty or between 1 and {SupportTicketType.MaxTermBusinessDays} business days.");

    /// <summary>Spec §7.5: un tipo sin término no tiene vencimiento, así que no puede prometer un plazo legal. El error va en
    /// <c>TermIsLegal</c>, que es el interruptor que el formulario marca.</summary>
    public static IRuleBuilderOptions<T, bool?> MustBeLegalOnlyWithTerm<T>(this IRuleBuilder<T, bool?> rule, Func<T, int?> term) =>
        rule.NotNull()
            .Must((command, legal) => legal != true || term(command) is not null)
            .WithMessage("A ticket type without term cannot be legal.");
}
