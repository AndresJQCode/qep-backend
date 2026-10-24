namespace Modules.Customers.Application;

/// <summary>
/// Spec 2026-10-24 §3.2: el número de documento sin signos ni mayúsculas, que es con lo que se comparan dos clientes.
/// La misma regla que la expresión SQL del índice <c>IX_customers_tenant_identification_normalized</c>
/// (<c>CustomerIdentificationSql.NormalizedNumber</c>): sólo letras y dígitos ASCII, en mayúsculas.
/// </summary>
public static class CustomerIdentificationNormalizer
{
    public static string Normalize(string? number)
    {
        if (string.IsNullOrEmpty(number))
        {
            return string.Empty;
        }

        Span<char> buffer = number.Length <= 128 ? stackalloc char[number.Length] : new char[number.Length];
        var length = 0;
        foreach (var character in number)
        {
            if (character is >= '0' and <= '9' or >= 'A' and <= 'Z')
            {
                buffer[length++] = character;
            }
            else if (character is >= 'a' and <= 'z')
            {
                buffer[length++] = (char)(character - 32);
            }
        }

        return new string(buffer[..length]);
    }
}
