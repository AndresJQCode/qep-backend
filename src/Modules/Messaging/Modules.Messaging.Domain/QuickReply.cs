using System.Text.RegularExpressions;

namespace Modules.Messaging.Domain;

/// <summary>
/// Spec 2026-10-12 §3.1 y §3.2: una respuesta rápida del tenant (D-Q1: global, sin dueño). El atajo se guarda normalizado —sin
/// espacios a los lados y en minúsculas— y cumple <c>^[a-z0-9-]{2,32}$</c>, así que una <c>/</c> inicial no pasa. El cuerpo se guarda
/// recortado, de 1 a 4096 caracteres (el tope de texto de WhatsApp), y sólo admite las variables de D-Q3; una llave suelta es texto.
/// El validador de Application usa estas mismas reglas para marcar el campo; acá son la última línea de defensa.
/// </summary>
public sealed partial class QuickReply
{
    public const int ShortcutMaxLength = 32;

    public const int BodyMaxLength = 4096;

    private QuickReply()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Shortcut { get; private set; } = string.Empty;

    public string Body { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public static QuickReply Create(Guid tenantId, string? shortcut, string? body, DateTimeOffset now)
    {
        var (normalizedShortcut, trimmedBody) = Validate(shortcut, body);
        return new QuickReply
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Shortcut = normalizedShortcut,
            Body = trimmedBody,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };
    }

    /// <summary>Reemplaza los dos campos. Valida todo antes de tocar nada: un rechazo deja la respuesta como estaba.</summary>
    public void Update(string? shortcut, string? body, DateTimeOffset now)
    {
        var (normalizedShortcut, trimmedBody) = Validate(shortcut, body);
        Shortcut = normalizedShortcut;
        Body = trimmedBody;
        UpdatedAt = now;
        Version++;
    }

    public static string NormalizeShortcut(string? shortcut) => (shortcut ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsValidShortcut(string normalizedShortcut)
    {
        ArgumentNullException.ThrowIfNull(normalizedShortcut);
        return ShortcutPattern().IsMatch(normalizedShortcut);
    }

    public static string TrimBody(string? body) => (body ?? string.Empty).Trim();

    public static bool IsValidBodyLength(string trimmedBody)
    {
        ArgumentNullException.ThrowIfNull(trimmedBody);
        return trimmedBody.Length is > 0 and <= BodyMaxLength;
    }

    /// <summary>D-Q3: el primer <c>{…}</c> que no es <c>{nombre_contacto}</c>, <c>{asesor}</c>, <c>{empresa}</c> ni <c>{saludo}</c> (con sus
    /// llaves), o <c>null</c>. Sensible a mayúsculas: el frontend reemplaza exactamente ésas, y cualquier otra llegaría literal al cliente.
    /// <c>{nombre}</c> es el nombre viejo de <c>{nombre_contacto}</c>: entra porque un SPA de antes del despliegue lo sigue mandando, pero
    /// los mensajes de error ya no lo ofrecen.</summary>
    public static string? FindUnknownVariable(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        foreach (Match match in VariablePattern().Matches(body))
        {
            if (match.Groups[1].Value is not ("nombre_contacto" or "asesor" or "empresa" or "saludo" or "nombre"))
            {
                return match.Value;
            }
        }

        return null;
    }

    private static (string Shortcut, string Body) Validate(string? shortcut, string? body)
    {
        var normalizedShortcut = NormalizeShortcut(shortcut);
        if (!IsValidShortcut(normalizedShortcut))
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.QuickReplyInvalidShortcut, "The shortcut takes 2 to 32 lowercase letters, digits or hyphens.");
        }

        var trimmedBody = TrimBody(body);
        if (!IsValidBodyLength(trimmedBody))
        {
            throw new MessagingDomainException(MessagingErrorCodes.QuickReplyInvalidBody, $"The body takes 1 to {BodyMaxLength} characters.");
        }

        if (FindUnknownVariable(trimmedBody) is { } unknown)
        {
            throw new MessagingDomainException(
                MessagingErrorCodes.QuickReplyUnknownVariable, $"'{unknown}' is not a variable; use {{nombre_contacto}}, {{asesor}}, {{empresa}} or {{saludo}}.");
        }

        return (normalizedShortcut, trimmedBody);
    }

    // \z y no $: $ también acepta un salto de línea al final.
    [GeneratedRegex(@"^[a-z0-9-]{2,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ShortcutPattern();

    // Lo de adentro de un par de llaves sin otra llave en medio: en «{{nombre}}» encuentra «{nombre}» y las de afuera son texto.
    [GeneratedRegex(@"\{([^{}]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex VariablePattern();
}
