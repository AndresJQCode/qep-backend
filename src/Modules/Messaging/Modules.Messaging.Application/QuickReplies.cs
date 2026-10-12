using FluentValidation;
using FluentValidation.Results;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

// Spec 2026-10-12 §3.3: las respuestas rápidas. Todo handler revalida tenant, permiso y módulo antes de tocar nada (403 con código,
// nunca 404 ante otro tenant) y valida después, así el 422 por campo no le cuenta a un extraño qué existe.

/// <summary>Lo que comparten crear y editar: el cuerpo de <c>SaveQuickReplyRequest</c> sin la versión.</summary>
public interface IQuickReplyInput
{
    string? Shortcut { get; }

    string? Body { get; }
}

/// <summary>Los textos de <c>errors</c>, tuteando. Fuera de <see cref="QuickReplyInputValidator{T}"/> porque un tipo genérico no
/// lleva miembros estáticos (CA1000).</summary>
public static class QuickReplyMessages
{
    public const string Shortcut = "El atajo lleva de 2 a 32 letras minúsculas, números o guiones, sin «/» ni espacios.";

    public const string Body = "Escribe una respuesta de 1 a 4096 caracteres.";

    public const string MissingVersion = "Falta la versión de la respuesta que estás viendo.";

    public static string UnknownVariable(string variable) => $"«{variable}» no es una variable. Usa {{nombre}}, {{asesor}} o {{empresa}}.";
}

/// <summary>
/// §3.3: las reglas de formato de <see cref="QuickReply"/> con la clave del cuerpo JSON (<c>shortcut</c>, <c>body</c>). Usa los mismos
/// métodos del agregado, así que no pueden divergir. Una variable desconocida también se marca acá: sin eso el 422 saldría con
/// <c>messaging.quick_reply.unknown_variable</c> y sin <c>errors</c>, y el formulario no sabría qué campo pintar.
/// </summary>
public abstract class QuickReplyInputValidator<T> : AbstractValidator<T>
    where T : IQuickReplyInput
{
    protected QuickReplyInputValidator()
    {
        RuleFor(input => input.Shortcut)
            .Must(shortcut => QuickReply.IsValidShortcut(QuickReply.NormalizeShortcut(shortcut)))
            .OverridePropertyName("shortcut").WithMessage(QuickReplyMessages.Shortcut);
        // Custom con ValidationFailure armado a mano: el mensaje lleva {nombre} literal, y WithMessage lo pasaría por el formateador
        // de FluentValidation, que trata {…} como placeholder.
        RuleFor(input => input.Body).Custom((body, context) =>
        {
            var trimmed = QuickReply.TrimBody(body);
            if (!QuickReply.IsValidBodyLength(trimmed))
            {
                context.AddFailure(new ValidationFailure("body", QuickReplyMessages.Body));
            }
            else if (QuickReply.FindUnknownVariable(trimmed) is { } unknown)
            {
                context.AddFailure(new ValidationFailure("body", QuickReplyMessages.UnknownVariable(unknown)));
            }
        });
    }
}
