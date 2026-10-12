using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Modules.Messaging.Infrastructure.Options;

namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>Decisión del owner 2026-10-12 (P1): ¿el botón de respuesta rápida que tocó la persona es el de «Detener promociones»? Compara el
/// texto y el payload del botón con la lista configurada, sin mayúsculas, sin tildes y sin espacios en los bordes. Nunca mira un mensaje de
/// texto: la palabra escrita a mano no es un consentimiento. Singleton: la lista se normaliza una vez.</summary>
internal sealed class MarketingOptOutButtons(IOptions<MessagingMarketingOptOutOptions> options)
{
    private readonly HashSet<string> _texts = [.. (options.Value.ButtonTexts is { Length: > 0 } configured
        ? configured : [.. MessagingMarketingOptOutOptions.DefaultButtonTexts])
        .Select(Normalize).Where(text => text.Length > 0)];

    public bool Matches(QuickReplyButton button)
    {
        ArgumentNullException.ThrowIfNull(button);
        return _texts.Contains(Normalize(button.Text)) || (button.Payload is not null && _texts.Contains(Normalize(button.Payload)));
    }

    private static string Normalize(string? value)
    {
        var decomposed = (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC).ToUpperInvariant();
    }
}
