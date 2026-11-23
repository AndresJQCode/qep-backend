using System.Globalization;

namespace Modules.Hr.Domain;

/// <summary>Un tramo tal como lo escribe el operador, antes de ser entidad: límites en múltiplos de SMMLV y tasa como fracción.</summary>
public sealed record LegalBracketInput(decimal LowerBound, decimal? UpperBound, decimal Rate);

/// <summary>
/// Las reglas de un valor legal y de un conjunto de tramos (spec 2026-11-22, «Validación por clave» y «Tramos»). Una función pura que
/// devuelve el mensaje en español (o <c>null</c> si es válido): el formulario, el Excel y el escritor comparten la misma verdad.
/// Un valor fuera de rango es 422, nunca se corrige en silencio.
/// </summary>
public static class LegalParameterRules
{
    public const string RateOutOfRangeMessage =
        "La tasa tiene que estar entre 0 % y 100 %. ¿Escribiste 12 en una celda sin formato de porcentaje?";

    // `value` es numeric(18,6) y la tasa de un tramo también: Postgres redondearía en silencio lo que pase de 6 decimales.
    private const int ValueScale = 6;

    // Los límites de un tramo son numeric(9,4): cuatro decimales y, como mucho, 99999.9999.
    private const int BoundScale = 4;
    private const decimal MaxBound = 99999.9999m;

    private const decimal MaxWeeklyHours = 168m;

    // numeric(18,6) guarda 12 dígitos enteros: un valor mayor haría fallar el guardado con un 500 en vez de un 422.
    private const decimal MaxValue = 999_999_999_999.999999m;

    /// <summary><c>null</c> si el valor es válido para la clave; si no, el mensaje que se le muestra al operador.</summary>
    public static string? Check(LegalParameterKey key, decimal value)
    {
        var label = LegalParameterLabels.Of(key);
        var name = label.Name;

        var rangeError = label.Unit switch
        {
            LegalParameterUnit.Fraction => value is > 0m and <= 1m ? null : RateOutOfRangeMessage,
            LegalParameterUnit.Currency => value > 0m ? null : $"{name} tiene que ser un monto mayor que cero.",
            LegalParameterUnit.SmmlvMultiple => value > 0m ? null : $"{name} tiene que ser un múltiplo de SMMLV mayor que cero.",
            LegalParameterUnit.Hours => value is > 0m and <= MaxWeeklyHours
                ? null
                : $"{name} tiene que estar entre 0 y 168 horas, sin incluir el cero.",
            LegalParameterUnit.Flag => value == 1m ? null : $"{name} sólo puede valer 1: lo que importa es desde cuándo rige.",
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "La clave no tiene una unidad conocida."),
        };
        if (rangeError is not null)
        {
            return rangeError;
        }

        if (value > MaxValue)
        {
            return $"{name} no puede pasar de 999999999999.999999: revisa el valor que escribiste.";
        }

        return HasMoreDecimalsThan(value, ValueScale)
            ? $"{name} admite como máximo {ValueScale} decimales: revisa el valor que escribiste."
            : null;
    }

    /// <summary>Lanza <see cref="HrDomainException"/> (422 <c>hr.legal_parameter.value_invalid</c>) si el valor no es válido.</summary>
    public static void EnsureValid(LegalParameterKey key, decimal value)
    {
        var message = Check(key, value);
        if (message is not null)
        {
            throw new HrDomainException(HrErrorCodes.LegalParameterValueInvalid, message);
        }
    }

    /// <summary>
    /// <c>null</c> si el conjunto es válido: al menos un tramo, ordenados por límite inferior, el primero desde 0 o más, contiguos
    /// (cada uno empieza donde termina el anterior), con límite superior mayor que el inferior, sin tope sólo en el último, y la
    /// tasa en <c>(0, 1]</c> con hasta 6 decimales. Los límites llevan hasta 4 decimales y no pasan de 99999.9999.
    /// </summary>
    public static string? CheckBrackets(IReadOnlyList<LegalBracketInput> brackets)
    {
        if (brackets.Count == 0)
        {
            return "El conjunto de tramos tiene que traer al menos un tramo.";
        }

        if (brackets[0].LowerBound < 0m)
        {
            return "El primer tramo no puede empezar por debajo de cero.";
        }

        for (var i = 0; i < brackets.Count; i++)
        {
            var bracket = brackets[i];
            var position = (i + 1).ToString(CultureInfo.InvariantCulture);
            var isLast = i == brackets.Count - 1;

            if (HasMoreDecimalsThan(bracket.LowerBound, BoundScale)
                || (bracket.UpperBound is { } upperScale && HasMoreDecimalsThan(upperScale, BoundScale)))
            {
                return $"Los límites del tramo {position} admiten como máximo {BoundScale} decimales.";
            }

            if (bracket.LowerBound > MaxBound || (bracket.UpperBound is { } upperMax && upperMax > MaxBound))
            {
                return $"Los límites del tramo {position} no pueden pasar de 99999.9999 SMMLV.";
            }

            // Pasarse de 1 es casi siempre un «12» en vez de 12 %; cero o negativo no, y esa pista sería engañosa.
            if (bracket.Rate > 1m)
            {
                return RateOutOfRangeMessage;
            }

            if (bracket.Rate <= 0m)
            {
                return $"La tasa del tramo {position} tiene que estar entre 0 % y 100 %, sin incluir el 0 %.";
            }

            if (HasMoreDecimalsThan(bracket.Rate, ValueScale))
            {
                return $"La tasa del tramo {position} admite como máximo {ValueScale} decimales.";
            }

            if (bracket.UpperBound is null)
            {
                if (!isLast)
                {
                    return $"Sólo el último tramo puede quedar sin tope: el tramo {position} no tiene límite superior.";
                }
            }
            else if (bracket.UpperBound <= bracket.LowerBound)
            {
                return $"En el tramo {position} el límite superior tiene que ser mayor que el inferior.";
            }

            if (i > 0)
            {
                var previous = brackets[i - 1];
                if (bracket.LowerBound < previous.LowerBound)
                {
                    return $"Los tramos tienen que ir ordenados por límite inferior: el tramo {position} va antes de lo debido.";
                }

                if (bracket.LowerBound != previous.UpperBound)
                {
                    return $"Los tramos tienen que ser contiguos: el tramo {position} tiene que empezar donde termina el anterior.";
                }
            }
        }

        return null;
    }

    /// <summary>Lanza <see cref="HrDomainException"/> (422 <c>hr.legal_parameter.value_invalid</c>) si el conjunto no es válido.</summary>
    public static void EnsureValidBrackets(IReadOnlyList<LegalBracketInput> brackets)
    {
        var message = CheckBrackets(brackets);
        if (message is not null)
        {
            throw new HrDomainException(HrErrorCodes.LegalParameterValueInvalid, message);
        }
    }

    // Compara contra el redondeo: 0.0400000 tiene escala 7 pero no más precisión que 0.04.
    private static bool HasMoreDecimalsThan(decimal value, int scale) => decimal.Round(value, scale) != value;
}
