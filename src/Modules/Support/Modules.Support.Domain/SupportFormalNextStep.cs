namespace Modules.Support.Domain;

/// <summary>
/// Fase 4b (H6): la regla del «siguiente paso» de la respuesta formal, la misma para el tenant (<see cref="SupportTenantSettings"/>)
/// y para el tipo (<see cref="SupportTicketType"/>), que pisa al del tenant. Texto plano: el PDF lo imprime como texto, nunca como
/// marcado. Los caracteres de control los frena el validador; acá sólo el largo, para que ningún camino guarde más de lo que cabe.
/// </summary>
public static class SupportFormalNextStep
{
    public const int MaxLength = 2000;

    /// <summary>Recortado; vacío o sólo espacios es null. Más de <see cref="MaxLength"/> después de recortar lanza.</summary>
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

        return trimmed;
    }
}
