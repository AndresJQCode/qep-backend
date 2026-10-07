using System.Globalization;

namespace Modules.Pos.Domain;

/// <summary>Numeración propia por tenant, sin año ni formato configurable (spec, decisión 15).</summary>
public static class PosSaleNumber
{
    public static string Format(long value) =>
        "POS-" + value.ToString("D6", CultureInfo.InvariantCulture);
}
