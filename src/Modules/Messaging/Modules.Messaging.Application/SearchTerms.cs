using System.Text;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-09 §7.3, «Cómo se arma @q». Ningún texto de la persona llega a <c>to_tsquery</c>
/// como sintaxis: los tokens pasan por <c>to_tsvector</c> y sólo los lexemas (letras y dígitos) se unen.</summary>
public static class SearchTerms
{
    public const int MaxTokens = 8;
    public const int PrefixMinLength = 3;

    private static readonly HashSet<char> Operators = ['&', '|', '!', ':', '*', '(', ')', '\'', '\\', '<'];

    /// <summary><c>Trim</c>; los espacios en blanco, los operadores de <c>tsquery</c> y los caracteres de
    /// control separan tokens (<c>a:*|!(b)</c> → <c>a</c>, <c>b</c>) y nunca llegan a uno; se descartan los
    /// vacíos y quedan los primeros <see cref="MaxTokens"/>.</summary>
    public static IReadOnlyList<string> Tokenize(string q)
    {
        ArgumentNullException.ThrowIfNull(q);
        var tokens = new List<string>();
        var builder = new StringBuilder();
        foreach (var character in q.Trim())
        {
            if (!char.IsWhiteSpace(character) && !Operators.Contains(character) && !char.IsControl(character))
            {
                builder.Append(character);
                continue;
            }

            if (Flush(builder, tokens))
            {
                return tokens;
            }
        }

        Flush(builder, tokens);
        return tokens;
    }

    /// <summary>Cierra el token en curso; <c>true</c> cuando ya hay <see cref="MaxTokens"/>.</summary>
    private static bool Flush(StringBuilder builder, List<string> tokens)
    {
        if (builder.Length > 0)
        {
            tokens.Add(builder.ToString());
            builder.Clear();
        }

        return tokens.Count == MaxTokens;
    }

    /// <summary>Cada token con su lexema (<c>null</c> = stop word o sólo símbolos). Prefijo con 3+ caracteres
    /// del <b>token</b>. <c>null</c> si no queda nada que buscar.</summary>
    public static string? Compose(IReadOnlyList<(string Token, string? Lexeme)> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        var parts = new List<string>();
        foreach (var (token, lexeme) in terms)
        {
            // Un lexema sólo tiene letras y dígitos; cualquier otra cosa se descarta en vez de escaparla.
            if (string.IsNullOrEmpty(lexeme) || !lexeme.All(char.IsLetterOrDigit))
            {
                continue;
            }

            parts.Add(token.Length >= PrefixMinLength ? lexeme + ":*" : lexeme);
        }

        return parts.Count == 0 ? null : string.Join(" & ", parts);
    }
}
