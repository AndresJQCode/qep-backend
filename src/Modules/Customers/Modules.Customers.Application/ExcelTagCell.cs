namespace Modules.Customers.Application;

/// <summary>
/// La celda «Etiquetas» del Excel de clientes (spec 2026-11-17): varios nombres separados por <see cref="Separator"/>. Cada uno
/// se recorta; los vacíos se ignoran y los repetidos (sin distinguir mayúsculas) cuentan una vez, conservando el primero. Es la
/// única lectura de esa celda: el validador de la fila y el handler de importación la comparten para no divergir.
/// </summary>
public static class ExcelTagCell
{
    public const char Separator = ';';

    public static IReadOnlyList<string> Parse(string? cell)
    {
        if (string.IsNullOrWhiteSpace(cell))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var part in cell.Split(Separator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (seen.Add(part))
            {
                names.Add(part);
            }
        }

        return names;
    }
}
