namespace Modules.Support.Domain;

/// <summary>
/// Fase 4b (H6): la regla del «siguiente paso» de la respuesta formal, la misma para el tenant (<see cref="SupportTenantSettings"/>)
/// y para el tipo (<see cref="SupportTicketType"/>), que pisa al del tenant. Texto plano: el PDF lo imprime como texto, nunca como
/// marcado. El validador da el campo; acá se repiten el largo y los caracteres de control, para que un camino que se salte el
/// validador no guarde más de lo que cabe ni un carácter que Postgres rechaza (un <c>\0</c> es 22021).
/// </summary>
public static class SupportFormalNextStep
{
    public const int MaxLength = 2000;

    /// <summary>Recortado; vacío o sólo espacios es null. Más de <see cref="MaxLength"/> después de recortar, o un carácter de control
    /// que no sea salto de línea, retorno o tabulación, lanza.</summary>
    public static string? Normalize(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > MaxLength)
        {
            throw new ArgumentException($"The formal next step has at most {MaxLength} characters.", nameof(text));
        }

        if (trimmed.Any(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t')))
        {
            throw new ArgumentException("The formal next step has no control characters.", nameof(text));
        }

        return trimmed;
    }
}
