namespace Modules.Billing.Application;

/// <summary>
/// El duplicado dentro del archivo (§3.2b, <c>duplicate_service</c>): mismo cliente por la clave de emparejamiento
/// (<see cref="BillingImportRow.CustomerKey"/>) y mismo servicio sin mayúsculas ni espacios extra. La primera fila gana.
/// Pura, como <c>ExcelCustomerRowDeduplicator</c>: se prueba sin infraestructura.
/// </summary>
public static class ExcelBillingRowDeduplicator
{
    public static (IReadOnlyList<BillingImportRow> FirstOccurrences, IReadOnlyList<BillingImportRow> Duplicates) Partition(
        IReadOnlyList<BillingImportRow> rows) => Partition(rows, row => row.CustomerKey);

    /// <summary>
    /// Con la clave del cliente ya resuelto por el emparejamiento (<see cref="BillingImportAnalyzer"/>): el cliente de la base
    /// que encontró, o el cliente nuevo al que la fila se suma.
    /// </summary>
    public static (IReadOnlyList<BillingImportRow> FirstOccurrences, IReadOnlyList<BillingImportRow> Duplicates) Partition(
        IReadOnlyList<BillingImportRow> rows, Func<BillingImportRow, string?> customerKeyOf)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(customerKeyOf);
        var seen = new HashSet<(string Customer, string Service)>();
        var firsts = new List<BillingImportRow>(rows.Count);
        var duplicates = new List<BillingImportRow>();
        foreach (var row in rows)
        {
            // Sin clave no hay con qué comparar: la fila sigue, y el plan la rechaza si tiene que crear el cliente.
            if (customerKeyOf(row) is not { } customer || seen.Add((customer, row.ServiceKey)))
            {
                firsts.Add(row);
            }
            else
            {
                duplicates.Add(row);
            }
        }

        return (firsts, duplicates);
    }
}
