namespace Modules.ServiceContracts.Application;

/// <summary>
/// El duplicado dentro del archivo (§3.2b, <c>duplicate_service</c>): mismo cliente por la clave de emparejamiento
/// (<see cref="ServiceContractsImportRow.CustomerKey"/>) y mismo servicio sin mayúsculas ni espacios extra. La primera fila gana.
/// Pura, como <c>ExcelCustomerRowDeduplicator</c>: se prueba sin infraestructura.
/// </summary>
public static class ExcelServiceContractsRowDeduplicator
{
    public static (IReadOnlyList<ServiceContractsImportRow> FirstOccurrences, IReadOnlyList<ServiceContractsImportRow> Duplicates) Partition(
        IReadOnlyList<ServiceContractsImportRow> rows) => Partition(rows, row => row.CustomerKey);

    /// <summary>
    /// Con la clave del cliente ya resuelto por el emparejamiento (<see cref="ServiceContractsImportAnalyzer"/>): el cliente de la base
    /// que encontró, o el cliente nuevo al que la fila se suma.
    /// </summary>
    public static (IReadOnlyList<ServiceContractsImportRow> FirstOccurrences, IReadOnlyList<ServiceContractsImportRow> Duplicates) Partition(
        IReadOnlyList<ServiceContractsImportRow> rows, Func<ServiceContractsImportRow, string?> customerKeyOf)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(customerKeyOf);
        var seen = new HashSet<(string Customer, string Service)>();
        var firsts = new List<ServiceContractsImportRow>(rows.Count);
        var duplicates = new List<ServiceContractsImportRow>();
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
