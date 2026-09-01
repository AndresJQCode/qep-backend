namespace Modules.Customers.Application;

/// <summary>
/// Arma el Excel del padron de clientes. Puerto en Application y adaptador ClosedXML en
/// Infrastructure, igual que <see cref="ICustomerImportTemplateBuilder"/>: que libreria escribe el
/// archivo es un detalle de infraestructura.
/// </summary>
public interface ICustomerExportBuilder
{
    CustomerExportFile Build(
        IReadOnlyList<CustomerDto> customers,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken);
}

public sealed record CustomerExportFile(byte[] Content, string FileName);
