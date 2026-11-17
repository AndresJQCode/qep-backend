using System.Globalization;
using System.Text;

namespace Modules.Support.Domain;

/// <summary>Fase 4b (H15, H16): en qué va la encuesta de un ciclo de resolución. Se guarda por nombre.</summary>
public enum SupportSurveyStatus
{
    /// <summary>Pedida al resolver, todavía sin procesar por el worker (o esperando su reintento).</summary>
    Pending,

    /// <summary>Salió por su canal, o quedó publicada en la página de consulta (<see cref="SupportSurveyChannel.Portal"/>).</summary>
    Sent,

    /// <summary>«Sin encuesta»: el tenant la tiene apagada o el canal no la admite; el motivo va en <c>ReasonCode</c>.</summary>
    Skipped,

    /// <summary>El canal falló en los cuatro intentos.</summary>
    Failed,
}

/// <summary>Fase 4b (H16, owner 2026-11-16, Q-2): por dónde se pidió o se respondió la encuesta. Nunca por Instagram. Se guarda por
/// nombre.</summary>
public enum SupportSurveyChannel
{
    WhatsApp,
    Email,
    Portal,
}

/// <summary>Fase 4b: la calificación, Buena, Regular o Mala. Se guarda por nombre.</summary>
public enum SupportSurveyRating
{
    Good,
    Regular,
    Bad,
}

/// <summary>Los textos de los tres botones de la plantilla de la encuesta, que Meta devuelve en el toque (V-1).</summary>
public static class SupportSurveyRatings
{
    /// <summary>Copia de <c>SupportSurveyTemplateDefinition.ButtonLabels</c> de Mensajería, que Soporte no ve; las amarra una prueba
    /// de paridad en Bootstrapper.</summary>
    public static IReadOnlyList<(SupportSurveyRating Rating, string Label)> ButtonLabels { get; } =
    [
        (SupportSurveyRating.Good, "Buena"),
        (SupportSurveyRating.Regular, "Regular"),
        (SupportSurveyRating.Bad, "Mala"),
    ];

    /// <summary>La calificación del texto de un botón, sin mirar mayúsculas, tildes ni espacios en los bordes; null si no es uno de
    /// los tres.</summary>
    public static SupportSurveyRating? FromButtonText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var key = Fold(text);
        foreach (var (rating, label) in ButtonLabels)
        {
            if (string.Equals(Fold(label), key, StringComparison.Ordinal))
            {
                return rating;
            }
        }

        return null;
    }

    private static string Fold(string text)
    {
        var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }
}
